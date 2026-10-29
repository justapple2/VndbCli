namespace Vndb.Core;

/// <summary>Shared upstream enrichment; no Web credentials or persistence.</summary>
public sealed class VndbCatalogSource(VndbClient core, TimeProvider? time = null) : ICatalogSource
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    public async Task<CatalogResult> QueryAsync(CatalogQuery input, CancellationToken ct)
    {
        var query = input.Validate();
        var q = query;
        IReadOnlyList<CatalogEntry> items; bool more = false;
        if (query.Kind is "producer_search" or "producer_get")
        {
            IReadOnlyList<Producer> producers;
            if (query.Kind == "producer_get")
            { var p = await core.GetProducerAsync(query.Id!, ct); producers = p is null ? [] : [p]; }
            else
            {
                var page = await core.SearchProducersAsync(q.Query, q.PageSize, q.Page, ct); more = page.More;
                producers = page.Results.Count == 0 ? [] : (await core.GetProducersAsync(page.Results.Select(p => p.Id).ToArray(), ct)).Results;
            }
            items = producers.Select(p => new CatalogEntry(p.Id, p.Name, "producer", null, [], p, new("not_applicable"), _time.GetUtcNow(), "client_vndb")).ToArray();
        }
        else
        {
            IReadOnlyList<VisualNovel> novels;
            if (query.Kind is "vn_get" or "vn_releases")
            {
                var n = await core.GetNovelAsync(query.Id!, ct);
                if (n is not null && query.Kind == "vn_releases") n = n with { ReleaseCatalog = await core.GetNovelReleasesAsync(n.Id, ct) };
                novels = n is null ? [] : [n];
            }
            else
            {
                var page = query.Kind == "producer_vns" ? await core.GetDeveloperNovelsAsync(query.Id!, q.PageSize, q.Page, ct) :
                    await core.SearchNovelsAsync(q.Query, q.PageSize, q.Page, q.ProducerId, ct);
                more = page.More;
                novels = page.Results.Count == 0 ? [] : (await core.GetNovelsAsync(page.Results.Select(n => n.Id).ToArray(), ct)).Results;
            }
            var ids = novels.SelectMany(n => n.Developers).Select(p => p.Id).Distinct().ToArray();
            var producers = new List<Producer>();
            foreach (var chunk in ids.Chunk(100)) producers.AddRange((await core.GetProducersAsync(chunk, ct)).Results);
            items = novels.Select(n => new CatalogEntry(n.Id, n.Title, "vn", n,
                producers.Where(p => n.Developers.Any(d => d.Id == p.Id)).ToArray(), null,
                new(n.Image is null ? "none" : "not_downloaded"), _time.GetUtcNow(), "client_vndb",
                n.Developers.All(d => producers.Any(p => p.Id == d.Id)) ? "full_text" : "partial_text")).ToArray();
        }
        return new(items, "client_vndb", items.Count == 0 ? null : items.Min(i => i.FetchedAt), q.Page, q.PageSize, null, more, PendingImport: items.Count > 0);
    }
}
