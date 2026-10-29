namespace Vndb.Core;

/// <summary>Business intent; HTTP and persisted JSON map explicitly at their boundaries.</summary>
public sealed record CatalogQuery(string Kind, string Query = "", string? Id = null, string? ProducerId = null,
    int Page = 1, int PageSize = 10, string Mode = "local_first")
{
    public static CatalogQuery FromText(string text, int pageSize = 10)
    {
        text = text.Trim();
        return new CatalogQuery(text.Length > 1 && text[0] == 'v' && text[1..].All(char.IsAsciiDigit) ? "vn_get" : "vn_search",
            text, text, PageSize: pageSize).Validate();
    }
    public CatalogQuery Validate(bool requireText = true)
    {
        if (Kind is not ("vn_search" or "vn_get" or "vn_releases" or "producer_search" or "producer_get" or "producer_vns"))
            throw new ArgumentException("未知查询类型。");
        if (Query is null || Query.Trim().Length > 200 || Query.Any(char.IsControl)) throw new ArgumentException("查询文字最多 200 字符，不能含控制字符。");
        if (Page < 1 || PageSize is < 1 or > 100 || (long)(Page - 1) * PageSize > int.MaxValue)
            throw new ArgumentException("页码须大于 0，每页数量须为 1–100。");
        if (Mode is not ("local_only" or "local_first" or "explicit_refresh")) throw new ArgumentException("无效的查询模式。");
        var text = Query.Trim();
        if (requireText && Kind.EndsWith("search", StringComparison.Ordinal) && text.Length == 0) throw new ArgumentException("请输入查询文字。");
        return this with { Query = text, ProducerId = string.IsNullOrWhiteSpace(ProducerId) ? null : VndbId.Normalize(ProducerId, 'p'),
            Id = Kind is "vn_get" or "vn_releases" or "producer_get" or "producer_vns" ? VndbId.Normalize(Id!, Kind is "vn_get" or "vn_releases" ? 'v' : 'p') : null };
    }
}

public sealed record CatalogCover(string Status, string? Url = null, string? ContentType = null, string? Sha256 = null);
public sealed record CatalogEntry(string VndbId, string Title, string Kind, VisualNovel? Novel,
    IReadOnlyList<Producer> Developers, Producer? Producer, CatalogCover Cover, DateTimeOffset FetchedAt,
    string Provenance, string Completeness = "full_text");
public sealed record CatalogResult(IReadOnlyList<CatalogEntry> Items, string Source, DateTimeOffset? FetchedAt,
    int Page, int PageSize, int? Total, bool More, bool Partial = false, bool NeedsRemote = false,
    bool PendingImport = false, string VerificationStatus = "vndb_metadata", IReadOnlyList<string>? Warnings = null);

public interface ICatalogSource
{
    Task<CatalogResult> QueryAsync(CatalogQuery query, CancellationToken ct);
}
public interface ICatalogSnapshots
{
    Task<CatalogResult?> ReadAsync(CatalogQuery query, CancellationToken ct);
    Task SaveAsync(CatalogQuery query, CatalogResult result, CancellationToken ct);
}
public class CatalogUnavailableException : Exception;
public sealed class CatalogCacheMissException : Exception;
