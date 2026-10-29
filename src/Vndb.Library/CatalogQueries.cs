using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Library;

/// <summary>One server query/persistence workflow for v1 and legacy HTTP projections.</summary>
public sealed class CatalogQueries(ILibraryStore store, CatalogEnricher catalog, IAutomationStore automation,
    VndbClient upstream, LibraryImporter importer, TimeProvider time) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (MetadataResponse Response, DateTimeOffset At)> _queries = [];

    public static MetadataSnapshot Snapshot(LibraryEntry entry) => MetadataImports.Snapshot(entry);
    private MetadataSnapshot Snapshot(StoredProducer entry)
    {
        var source = (store as SqliteLibraryStore)?.Provenance(entry.Producer.Id);
        return new(entry.Producer.Id, entry.Producer.Name, "producer", null, [], entry.Producer, new("not_applicable"),
            source?.FetchedAt ?? entry.FetchedAt, source?.Source ?? "web_vndb", source?.Completeness ?? "full_text");
    }

    public MetadataResponse Local(string kind, MetadataQuery request, string? id = null)
    {
        var q = QueryRules.Validate(request);
        if (kind is "vn_get" or "vn_releases" or "producer_get")
        {
            var item = kind is "vn_get" or "vn_releases" ? store.Get(VndbId.Normalize(id!, 'v')) is { } vn ? Snapshot(vn) : null :
                store.GetProducer(VndbId.Normalize(id!, 'p')) is { } p ? Snapshot(p) : null;
            if (kind == "vn_releases" && item?.Novel?.ReleaseCatalog is null) item = null;
            return QueryRules.Response(item is null ? [] : [item], "library", q, item is null ? 0 : 1, false, needsRemote: item is null);
        }
        if (kind == "producer_search")
        {
            var all = store.FindProducers(q.Query);
            return QueryRules.Response(all.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).Select(Snapshot).ToArray(),
                "library", q, all.Count, (long)q.Page * q.PageSize < all.Count, partial: true, needsRemote: all.Count == 0);
        }
        if (kind == "producer_vns")
        {
            var all = store.List(producer: VndbId.Normalize(id!, 'p'), page: q.Page, limit: q.PageSize);
            return QueryRules.Response(all.Results.Select(Snapshot).ToArray(), "library", q, all.Total, all.More,
                partial: true, needsRemote: all.Total == 0);
        }
        var result = ReadNovels(new(q.Query, q.ProducerId, q.Page, q.PageSize));
        if (result.Source == "local-query-cache")
        {
            var remembered = automation.GetLibrarySearchCache(AutomationRules.SearchKey(new(q.Query, q.ProducerId, q.Page, q.PageSize)));
            if (remembered is { NovelIds.Count: 0 } && time.GetUtcNow() - remembered.SavedAt >= TimeSpan.FromHours(6))
                return QueryRules.Response([], "library", q, 0, false, needsRemote: true);
            return QueryRules.Response(result.Results.Select(Snapshot).ToArray(), "query_cache", q, null, result.More)
                with { FetchedAt = remembered?.SavedAt };
        }
        return QueryRules.Response(result.Results.Select(Snapshot).ToArray(), "library",
            q, result.Source == "local" ? result.Total : null, result.More, partial: true, needsRemote: result.NeedsRemote);
    }

    public async Task<MetadataResponse> QueryAsync(string kind, MetadataQuery request, string? id, CancellationToken ct)
    {
        var q = QueryRules.Validate(request);
        if (id is not null) id = VndbId.Normalize(id, kind is "vn_get" or "vn_releases" ? 'v' : 'p');
        if (kind.EndsWith("search", StringComparison.Ordinal) && q.Query.Length == 0)
            throw new ArgumentException("联网搜索需要标题或会社名；浏览本地库请使用 GET。");
        var key = System.Text.Json.JsonSerializer.Serialize(new { kind, id, query = q with { Mode = "local_first" } }, VndbJson.Options);
        await _gate.WaitAsync(ct);
        try
        {
            if (q.Mode != "explicit_refresh")
            {
                var local = Local(kind, q, id);
                if (!local.NeedsRemote || q.Mode == "local_only") return local;
                if (_queries.TryGetValue(key, out var remembered) && time.GetUtcNow() - remembered.At < TimeSpan.FromHours(6))
                    return remembered.Response with { Source = "query_cache" };
            }
            if (q.PageSize > 20) throw new ArgumentException("联网查询每页最多 20 条，本地浏览最多 100 条。");
            IReadOnlyList<MetadataSnapshot> items; bool more = false;
            if (kind.StartsWith("producer_", StringComparison.Ordinal) && kind != "producer_vns")
            {
                IReadOnlyList<Producer> producers;
                if (kind == "producer_get")
                {
                    var producer = await upstream.GetProducerAsync(id!, ct);
                    producers = producer is null ? [] : [producer];
                }
                else
                {
                    var page = await upstream.SearchProducersAsync(q.Query, q.PageSize, q.Page, ct);
                    more = page.More;
                    producers = page.Results.Count == 0 ? [] : (await upstream.GetProducersAsync(page.Results.Select(p => p.Id).ToArray(), ct)).Results;
                }
                var saved = producers.Select(p => new StoredProducer(p, time.GetUtcNow())).ToArray();
                foreach (var p in saved) store.SaveProducer(p);
                items = saved.Select(Snapshot).ToArray();
            }
            else
            {
                IReadOnlyList<string> ids;
                if (kind is "vn_get" or "vn_releases") ids = [id!];
                else if (kind == "vn_search" && q.Query.Length > 1 && char.ToLowerInvariant(q.Query[0]) == 'v' && q.Query[1..].All(char.IsAsciiDigit))
                    ids = [VndbId.Normalize(q.Query, 'v')];
                else
                {
                    var page = kind == "producer_vns" ? await upstream.GetDeveloperNovelsAsync(id!, q.PageSize, q.Page, ct) :
                        await upstream.SearchNovelsAsync(q.Query, q.PageSize, q.Page, q.ProducerId, ct);
                    ids = page.Results.Select(n => n.Id).Distinct().ToArray(); more = page.More;
                }
                var details = ids.Count == 0 ? [] : (await upstream.GetNovelsAsync(ids, ct)).Results;
                if (kind == "vn_releases" && details.Count > 0)
                    details = [details[0] with { ReleaseCatalog = await upstream.GetNovelReleasesAsync(id!, ct) }];
                if (q.ProducerId is not null) details = details.Where(n => n.Developers.Any(p => p.Id == q.ProducerId)).ToArray();
                if (q.Mode == "explicit_refresh")
                    foreach (var chunk in details.SelectMany(n => n.Developers).Select(p => p.Id).Distinct().Chunk(100))
                        foreach (var producer in (await upstream.GetProducersAsync(chunk, ct)).Results)
                            store.SaveProducer(new(producer, time.GetUtcNow()));
                await importer.SaveFetchedManyAsync(details, ct);
                items = details.Select(n => Snapshot(store.Get(n.Id)!)).ToArray();
            }
            var response = QueryRules.Response(items, "vndb", q, null, more);
            response = response with { FetchedAt = response.FetchedAt ?? time.GetUtcNow() };
            if (kind == "vn_search")
            {
                var requestKey = new LibrarySearchRequest(q.Query, q.ProducerId, q.Page, q.PageSize);
                automation.SaveLibrarySearchCache(new(AutomationRules.SearchKey(requestKey), requestKey,
                    items.Select(i => i.VndbId).ToArray(), more, response.FetchedAt.Value));
            }
            // Bounded process cache includes genuine negative results. Errors are never cached.
            if (_queries.Count >= 512) _queries.Remove(_queries.MinBy(p => p.Value.At).Key);
            _queries[key] = (response, time.GetUtcNow());
            return response;
        }
        finally { _gate.Release(); }
    }
    private LibrarySearchResult ReadNovels(LibrarySearchRequest input)
    {
        var r = AutomationRules.Search(input);
        if (IsId(r.Query) && store.Get(r.Query) is { } exact)
            return r.Producer is null || exact.Novel.Developers.Any(p => p.Id == r.Producer) ?
                new([exact], 1, 1, r.Limit, false, "local") : new([], 0, 1, r.Limit, false, "local");
        var direct = store.List(r.Query, r.Producer, r.Page, r.Limit);
        if (direct.Total > 0 || r.Query.Length == 0)
            return new(direct.Results, direct.Total, direct.Page, direct.PageSize, direct.More, "local");
        var key = CatalogRules.Key(r.Query);
        var associated = catalog.Rows().Where(row => row.Source.Key.Contains(key, StringComparison.Ordinal))
            .SelectMany(row => row.Source.NovelIds).ToHashSet(StringComparer.Ordinal);
        var matches = new List<LibraryEntry>();
        for (var page = 1; ; page++)
        {
            var entries = store.List(producer: r.Producer, page: page, limit: 100);
            matches.AddRange(entries.Results.Where(e => associated.Contains(e.Novel.Id) ||
                CatalogRules.Titles(e.Novel).Any(t => CatalogRules.Key(t).Contains(key, StringComparison.Ordinal))));
            if (!entries.More) break;
        }
        if (matches.Count > 0)
            return new((store is SqliteLibraryStore sqlite ? sqlite.PrioritizeMatches(matches) : matches).Skip((r.Page - 1) * r.Limit).Take(r.Limit).ToArray(), matches.Count, r.Page, r.Limit,
                (long)r.Page * r.Limit < matches.Count, "local");
        var cached = automation.GetLibrarySearchCache(AutomationRules.SearchKey(r));
        if (cached is not null)
        {
            var entries = cached.NovelIds.Select(store.Get).OfType<LibraryEntry>().ToArray();
            if (entries.Length == cached.NovelIds.Count)
                return new(entries, (r.Page - 1) * r.Limit + entries.Length + (cached.More ? 1 : 0), r.Page, r.Limit, cached.More, "local-query-cache");
        }
        return new([], 0, r.Page, r.Limit, false, "local", true);
    }
    // Legacy HTTP shape, backed by the same query/persistence/cache workflow as v1.
    public LibrarySearchResult Local(LibrarySearchRequest input)
    {
        var request = AutomationRules.Search(input);
        return Legacy(Local("vn_search", new(request.Query, request.Producer, request.Page, request.Limit)), request);
    }
    public async Task<LibrarySearchResult> SearchAsync(LibrarySearchRequest input, CancellationToken ct = default)
    {
        var request = AutomationRules.Search(input);
        if (request.Query.Length == 0 || request.Limit > 20) throw new ArgumentException("联网查询需要标题，且每页最多 20 个结果。");
        return Legacy(await QueryAsync("vn_search", new(request.Query, request.Producer, request.Page, request.Limit), null, ct), request);
    }
    private LibrarySearchResult Legacy(MetadataResponse response, LibrarySearchRequest request) => new(
        response.Items.Select(i => store.Get(i.VndbId)).OfType<LibraryEntry>().ToArray(),
        response.Total ?? (request.Page - 1) * request.Limit + response.Items.Count + (response.More ? 1 : 0),
        request.Page, request.Limit, response.More,
        response.Source switch { "library" => "local", "query_cache" => "local-query-cache", _ => response.Source }, response.NeedsRemote);
    private static bool IsId(string query) => query.Length > 1 && char.ToLowerInvariant(query[0]) == 'v' && query[1..].All(char.IsAsciiDigit);
    public void Dispose() => _gate.Dispose();
}
