using Vndb.Core;

namespace Vndb.Library;

/// <summary>Bounded metadata completion for explicit requests and the persisted NAS catalog queue.</summary>
public sealed class CatalogEnricher(VndbClient client, LibraryImporter importer, ILibraryStore store,
    ICatalogStore catalog) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static IEnumerable<PlayRecord> ReadPlays(ILibraryStore store)
    {
        for (var page = 1; ; page++)
        {
            var rows = store.Plays(played: true, page: page, limit: 100);
            foreach (var row in rows.Results) yield return row;
            if (!rows.More) yield break;
        }
    }
    private List<LibraryEntry> ReadLibrary()
    {
        var entries = new List<LibraryEntry>();
        for (var page = 1; ; page++)
        {
            var rows = store.List(page: page, limit: 100); entries.AddRange(rows.Results);
            if (!rows.More) return entries;
        }
    }
    public IReadOnlyList<CatalogRow> Rows()
    {
        var plays = ReadPlays(store).ToArray(); var local = catalog.LocalGames(); var archives = store.Archives();
        var groups = new Dictionary<string, (string Title, List<string> Plays, List<string> Local, List<string> Archives, List<string> Items, HashSet<string> Novels)>();
        void Add(string title, string? vn, string id, string kind)
        {
            var key = CatalogRules.Key(title);
            if (!groups.TryGetValue(key, out var group)) group = (title, [], [], [], [], new(StringComparer.Ordinal));
            if (kind == "play") group.Plays.Add(id); else if (kind == "local") group.Local.Add(id); else if (kind == "item") group.Items.Add(id); else group.Archives.Add(id);
            if (vn is not null) group.Novels.Add(vn);
            groups[key] = group;
        }
        foreach (var record in plays) Add(record.Details.Title, record.Details.NovelId, record.Id, "play");
        foreach (var record in local) Add(record.Details.Title, record.NovelId, record.Id, "local");
        foreach (var record in archives.Where(a => a.Registration.State != "planned"))
        {
            var n = store.Get(record.Registration.NovelId)?.Novel;
            if (n is not null) Add(n.Titles.FirstOrDefault(t => t.Main)?.Title ?? n.AltTitle ?? n.Title, n.Id, record.Id, "archive");
        }
        var present = store is SqliteLibraryStore sql ? sql.PresentItemIds().ToHashSet(StringComparer.Ordinal) : [];
        var items = store is SqliteLibraryStore sqlItems ? sqlItems.PersonalItems().Where(i => i.DeletedAt is null && present.Contains(i.Id)).ToDictionary(i => i.Id) : new Dictionary<string, Vndb.Contracts.LibraryItem>();
        foreach (var item in items.Values) Add(item.Details.Title, item.Details.NovelId, item.Id, "item");
        var playMap = plays.ToDictionary(p => p.Id); var localMap = local.ToDictionary(l => l.Id);
        var lookups = catalog.CatalogLookups().ToDictionary(l => l.Key);
        return groups.Select(pair =>
        {
            var g = pair.Value;
            var complete = g.Plays.All(id => playMap[id].Details.NovelId is not null) &&
                g.Local.All(id => localMap[id].NovelId is not null) && g.Items.All(id => items[id].Details.NovelId is not null) && g.Novels.Count > 0;
            lookups.TryGetValue(pair.Key, out var lookup);
            // Legacy lookup keys discarded '+'. Never reuse a fandisc proof for its base title.
            if (lookup is not null && CatalogRules.Key(lookup.Title) != pair.Key) lookup = null;
            var status = complete ? "matched" : lookup?.Status is null or "matched" ? "pending" : lookup.Status;
            return new CatalogRow(new(pair.Key, g.Title, g.Plays, g.Local, g.Archives, g.Novels.ToArray(), g.Items), status,
                lookup, g.Local.Select(id => localMap[id]).ToArray());
        }).OrderBy(r => r.Source.Title, StringComparer.Ordinal).ToArray();
    }
    public CatalogStatistics Statistics()
    {
        var rows = Rows();
        return new(rows.Count, rows.Count(r => r.Status == "matched"), rows.Count(r => r.Status == "pending"),
            rows.Count(r => r.Status == "needs_review"), rows.Count(r => r.Status == "not_found"), rows.Count(r => r.Status == "error"),
            rows.Sum(r => r.Source.PlayIds.Count), (store is SqliteLibraryStore sql ? sql.PresentItemIds().Count : 0) + catalog.LocalGames().Count, store.Archives().Count(a => a.Registration.State != "planned"));
    }
    public CatalogPage Page(string? query = null, string? status = null, string? scope = null, int page = 1, int limit = 20)
    {
        LibraryRules.ValidatePage(page, limit);
        if (status is not null && status is not ("matched" or "pending" or "needs_review" or "not_found" or "error"))
            throw new ArgumentException("无效的补全状态。");
        if (scope is not null && scope is not ("played" or "local")) throw new ArgumentException("无效的资料来源筛选。");
        var rows = Rows().Where(r => (status is null || r.Status == status) &&
            (scope != "played" || r.Source.PlayIds.Count > 0) && (scope != "local" || r.Source.LocalIds.Count + r.Source.ArchiveIds.Count + (r.Source.ItemIds?.Count ?? 0) > 0) &&
            (string.IsNullOrWhiteSpace(query) || string.Join('\n', r.Source.Title, r.Lookup?.Query,
                string.Join('\n', r.Locations.Select(l => l.Details.Path))).Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        return new(rows.Skip((page - 1) * limit).Take(limit).ToArray(), rows.Length, page, limit, (long)page * limit < rows.Length);
    }
    private async Task<(IReadOnlyList<VisualNovel> Novels, bool Truncated)> Search(IReadOnlyList<string> queries, CancellationToken token)
    {
        var first = await client.SearchNovelDetailsAsync(queries, cancellationToken: token);
        if (first.More && queries.Count > 1)
        {
            var midpoint = queries.Count / 2;
            var left = await Search(queries.Take(midpoint).ToArray(), token);
            var right = await Search(queries.Skip(midpoint).ToArray(), token);
            return (left.Novels.Concat(right.Novels).DistinctBy(n => n.Id).ToArray(), left.Truncated || right.Truncated);
        }
        var results = first.Results.ToList(); var more = first.More;
        for (var page = 2; more && page <= 5; page++)
        {
            var next = await client.SearchNovelDetailsAsync(queries, page, token); results.AddRange(next.Results); more = next.More;
        }
        return (results.DistinctBy(n => n.Id).ToArray(), more);
    }
    private (int Plays, int Local) Link(CatalogSource source, string id)
    {
        var plays = 0; var local = 0;
        foreach (var key in source.PlayIds)
        {
            var current = store.GetPlay(key);
            if (current is null || current.Details.NovelId is not null || CatalogRules.Key(current.Details.Title) != source.Key) continue;
            store.SavePlay(current.Details with { NovelId = id }, current.Id, current.Revision); plays++;
        }
        var locations = catalog.LocalGames().ToDictionary(l => l.Id);
        foreach (var key in source.LocalIds)
        {
            if (!locations.TryGetValue(key, out var current) || current.NovelId is not null || CatalogRules.Key(current.Details.Title) != source.Key) continue;
            catalog.LinkLocalGame(current.Id, id, current.Revision); local++;
        }
        if (store is SqliteLibraryStore sql)
            foreach (var key in source.ItemIds ?? [])
            {
                var item = sql.PersonalItems().Single(i => i.Id == key);
                if (item.Details.NovelId is null) { sql.LinkItem(key, new(id, item.Revision), "catalog-link-" + key + "-" + id); local++; }
            }
        return (plays, local);
    }
    private static string Query(CatalogRow row) => (row.Lookup?.Query ?? row.Source.Title).Trim().Normalize(System.Text.NormalizationForm.FormKC)[..Math.Min(200,
        (row.Lookup?.Query ?? row.Source.Title).Trim().Normalize(System.Text.NormalizationForm.FormKC).Length)];
    private CatalogLookup Lookup(CatalogRow row, string query, string status, string? id, string basis,
        IReadOnlyList<VisualNovel> candidates, bool truncated = false, string? error = null)
    {
        var queryKey = CatalogRules.Key(query);
        var ranked = candidates.OrderByDescending(n => CatalogRules.Titles(n).Any(t =>
            CatalogRules.Key(t) == row.Source.Key || CatalogRules.Key(t) == queryKey));
        return new(row.Source.Key, row.Source.Title, query, status, id, basis,
            ranked.Take(20).Select(CatalogRules.Candidate).ToArray(), truncated || candidates.Count > 20,
            DateTimeOffset.UtcNow, error);
    }
    public async Task<CatalogBatchResult> CompleteAsync(CatalogBatchRequest request, CancellationToken token = default)
    {
        if (request.Limit is < 1 or > 10) throw new ArgumentException("每批补全数量须为 1–10。");
        await _gate.WaitAsync(token);
        try
        {
            var rows = Rows().Where(r => request.Key is null ? r.Status == "pending" || (request.Retry && r.Status == "error") :
                r.Source.Key == request.Key && (r.Status != "matched" || request.Retry)).Take(request.Limit).ToArray();
            var results = new List<CatalogLookup>(); var linkedPlays = 0; var linkedLocal = 0; var imported = 0;
            var waiting = new List<CatalogRow>();
            foreach (var row in rows)
            {
                // Reuse only a previous verified lookup, not an arbitrary unique item in a partial NAS cache.
                if (row.Lookup is { Status: "matched", NovelId: { } confirmed } &&
                    row.Source.NovelIds.All(id => id == confirmed) && store.Get(confirmed) is { } existing)
                {
                    var n = existing.Novel; var counts = Link(row.Source, n.Id);
                    linkedPlays += counts.Plays; linkedLocal += counts.Local;
                    var result = Lookup(row, Query(row), "matched", n.Id, "nas-cache", [n]);
                    catalog.SaveCatalogLookup(result); results.Add(result);
                }
                else waiting.Add(row);
            }
            if (waiting.Count > 0)
            {
                try
                {
                    var decisions = new List<(CatalogRow Row, string Query, string Status, VisualNovel? Selected, IReadOnlyList<VisualNovel> Candidates, bool Truncated)>();
                    var searches = new Dictionary<string, (IReadOnlyList<VisualNovel> Novels, bool Truncated)>(StringComparer.Ordinal);
                    foreach (var row in waiting)
                    {
                        var query = Query(row);
                        // Multi-search OR queries exceeded VNDB's CPU budget in the real backfill.
                        // Search each distinct title once through the shared rate-limited client.
                        if (!searches.TryGetValue(query, out var found))
                        {
                            found = await Search([query], token);
                            searches.Add(query, found);
                        }
                        var matches = found.Novels.Where(n => CatalogRules.Titles(n).Any(t => CatalogRules.Key(t) == row.Source.Key)).ToArray();
                        var selected = matches.Length == 1 && !found.Truncated && row.Source.NovelIds.All(id => id == matches[0].Id) ? matches[0] : null;
                        decisions.Add((row, query, selected is not null ? "matched" : found.Novels.Count == 0 ? "not_found" : "needs_review",
                            selected, selected is not null ? matches : found.Novels, found.Truncated));
                    }
                    var missing = decisions.Where(d => d.Selected is not null).Select(d => d.Selected!).DistinctBy(n => n.Id)
                        .Where(n => store.Get(n.Id) is null || store.Get(n.Id)!.CoverStatus == "failed").ToArray();
                    if (missing.Length > 0) imported = (await importer.SaveFetchedManyAsync(missing, token)).Count;
                    foreach (var decision in decisions)
                    {
                        if (decision.Selected is { } n)
                        {
                            var counts = Link(decision.Row.Source, n.Id); linkedPlays += counts.Plays; linkedLocal += counts.Local;
                        }
                        var result = Lookup(decision.Row, decision.Query, decision.Status, decision.Selected?.Id,
                            decision.Selected is null ? "review-required" : "vndb-exact-title-or-alias", decision.Candidates, decision.Truncated);
                        catalog.SaveCatalogLookup(result); results.Add(result);
                    }
                }
                catch (Exception exception) when (exception is VndbApiException or HttpRequestException or TimeoutException or System.Text.Json.JsonException or IOException)
                {
                    foreach (var row in waiting.Where(r => results.All(done => done.Key != r.Source.Key)))
                    {
                        var result = Lookup(row, Query(row), "error", null, "request-failed", [], error: "资料获取失败，可重试；已保存的资料和个人记录保留。");
                        catalog.SaveCatalogLookup(result); results.Add(result);
                    }
                }
            }
            return new(results.Count, imported, linkedPlays, linkedLocal, Statistics().Pending, results);
        }
        finally { _gate.Release(); }
    }
    public async Task<CatalogLookup> SearchAsync(CatalogSearch request, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 200) throw new ArgumentException("请输入 1–200 字符的标题。");
        await _gate.WaitAsync(token);
        try
        {
            var row = Rows().FirstOrDefault(r => r.Source.Key == request.Key) ?? throw new ArgumentException("来源记录不存在。");
            var found = await Search([request.Query], token);
            var result = Lookup(row, request.Query, found.Novels.Count == 0 ? "not_found" : "needs_review", null, "manual-search", found.Novels, found.Truncated);
            catalog.SaveCatalogLookup(result); return result;
        }
        finally { _gate.Release(); }
    }
    public async Task<CatalogLookup> ResolveAsync(CatalogResolve request, CancellationToken token = default)
    {
        var id = VndbId.Normalize(request.NovelId, 'v');
        await _gate.WaitAsync(token);
        try
        {
            var row = Rows().FirstOrDefault(r => r.Source.Key == request.Key) ?? throw new ArgumentException("来源记录不存在。");
            if (row.Source.NovelIds.Any(existing => existing != id))
                throw new PlayConflictException("该标题已有其他作品关联，请逐条核对个人记录，避免覆盖身份。");
            var entry = await importer.ImportAsync(id, cancellationToken: token) ?? throw new ArgumentException("VNDB 中找不到该作品。");
            Link(row.Source, id);
            var result = Lookup(row, Query(row), "matched", id, "manual-confirmation", [entry.Novel]);
            catalog.SaveCatalogLookup(result); return result;
        }
        finally { _gate.Release(); }
    }
    public void Dispose() => _gate.Dispose();
}
