using Vndb.Core;

namespace Vndb.Contracts;

// Separate from upstream query intent: browsing never resolves, refreshes or creates personal records.
public sealed record PublicCatalogQuery(string Query = "", string? ProducerId = null, string Language = "",
    string ReleaseType = "", string Platform = "", bool IncludeMtl = false, string Sort = "personal",
    int Page = 1, int PageSize = 24);
public sealed record CatalogPersonalSummary(int LinkedItems, int Played, int Unplayed, int Unrecorded, int NeedsReview, bool IsLocal)
{
    public string Label => LinkedItems == 0 ? "○ 未记录 · 未加入书架" :
        $"关联 {LinkedItems} 项 · ✓ 曾玩过 {Played} · ◷ 未玩 {Unplayed} · ○ 未记录 {Unrecorded}" + (NeedsReview > 0 ? $" · ! 待核对 {NeedsReview}" : "");
}
public sealed record PublicCatalogEntry(MetadataSnapshot Metadata, CatalogPersonalSummary Personal, bool ReleasesKnown);
public sealed record CatalogProducerChoice(string Id, string Name, string? Original, int NovelCount)
{
    public string Label => $"{Original ?? Name} · {Id} ({NovelCount})";
}
public sealed record CatalogCoverage(int PublicNovels, DateTimeOffset? CompletedSnapshotAt, string? ActiveState,
    long Processed, long Total);
public sealed record PublicCatalogPage(IReadOnlyList<PublicCatalogEntry> Items, int Page, int PageSize, int Total,
    bool More, CatalogCoverage Coverage, DateTimeOffset ObservedAt, string Source = "web_catalog");
public static class PublicCatalogRules
{
    public static PublicCatalogQuery Validate(PublicCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Query is null || query.Query.Length > 200 || query.Query.Any(char.IsControl) ||
            !Code(query.Language, 20) || !Code(query.Platform, 30) ||
            query.ReleaseType is not ("" or "complete" or "partial" or "trial" or "patch") ||
            query.Sort is not ("personal" or "updated" or "title" or "released_desc" or "released_asc") ||
            query.Page is < 1 or > 100000 || query.PageSize is < 1 or > 48)
            throw new ArgumentException("公共目录筛选或分页无效。");
        return query with { Query = query.Query.Trim(), ProducerId = string.IsNullOrEmpty(query.ProducerId) ? null : VndbId.Normalize(query.ProducerId, 'p') };
    }
    private static bool Code(string value, int length) => value is not null && value.Length <= length && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    public static bool ValidPage(PublicCatalogPage? page, PublicCatalogQuery q)
    {
        if (page?.Items is null || page.Coverage is null || page.Page != q.Page || page.PageSize != q.PageSize || page.Total < 0 ||
            page.Source != "web_catalog" || page.ObservedAt == default || page.Items.Count > q.PageSize || page.Coverage.PublicNovels < 0 ||
            page.Items.Select(i => i?.Metadata?.VndbId).Distinct().Count() != page.Items.Count) return false;
        var offset = (long)(q.Page - 1) * q.PageSize;
        if (page.Items.Count != Math.Clamp((long)page.Total - offset, 0, q.PageSize) ||
            page.More != ((long)q.Page * q.PageSize < page.Total) || page.Total > page.Coverage.PublicNovels ||
            page.Coverage.Processed < 0 || page.Coverage.Total < page.Coverage.Processed) return false;
        try
        {
            return page.Items.All(i => i?.Personal is { } p && i.Metadata is { Kind: "vn", Novel: { } n, Cover: { }, Developers: { } } m &&
                VndbId.Normalize(m.VndbId, 'v') == m.VndbId && n.Id == m.VndbId && m.Title is not null &&
                n.Title is not null && n.Titles is { } titles && titles.All(t => t is { Title: not null, Lang: not null }) &&
                m.Developers.All(d => d is { Name: not null } && VndbId.Normalize(d.Id, 'p') == d.Id) &&
                p.LinkedItems >= 0 && p.Played >= 0 && p.Unplayed >= 0 && p.Unrecorded >= 0 && p.NeedsReview >= 0 &&
                (!p.IsLocal || p.LinkedItems > 0) &&
                (long)p.Played + p.Unplayed + p.Unrecorded + p.NeedsReview == p.LinkedItems);
        }
        catch (ArgumentException) { return false; }
    }
    public static string SourceLabel(string provenance) => provenance switch { "vndb_dump" => "VNDB官方快照", "web_vndb" => "VNDB API", "client_vndb_import" => "客户端VNDB快照", _ => provenance };
    public static string CoverageLabel(CatalogCoverage c) => $"Web公共资料 {c.PublicNovels} 部 · " +
        (c.CompletedSnapshotAt is { } at ? $"已验证快照 {at:yyyy-MM-dd}" : "尚无完整导入凭证") +
        (c.ActiveState is { } state ? $" · 导入 {state} {c.Processed}/{c.Total}" : "") +
        "；仅检索已收录资料，字段/封面可能缺失，不保证最新全站覆盖。语言/类型须命中同一发行版，含未来发行；不证明本机版本。";
}
