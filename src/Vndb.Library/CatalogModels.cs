using Vndb.Core;

namespace Vndb.Library;

public sealed record LocalGameInput(string Machine, string Path, string Title, string? CompanyHint,
    DateTimeOffset ObservedAt, string Source, string SourceSha256);
public sealed record LocalGame(string Id, LocalGameInput Details, string? NovelId, long Revision,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record LocalGameBatch(IReadOnlyList<LocalGameInput> Rows);
public sealed record LocalGameImportResult(int Inserted, int Existing, int Total);
public sealed record CatalogCandidate(string Id, string Title, string? Original, string? Released,
    IReadOnlyList<ProducerSummary> Developers);
public sealed record CatalogLookup(string Key, string Title, string Query, string Status, string? NovelId,
    string Basis, IReadOnlyList<CatalogCandidate> Candidates, bool Truncated, DateTimeOffset CheckedAt, string? Error);
public sealed record CatalogSource(string Key, string Title, IReadOnlyList<string> PlayIds,
    IReadOnlyList<string> LocalIds, IReadOnlyList<string> ArchiveIds, IReadOnlyList<string> NovelIds, IReadOnlyList<string>? ItemIds = null);
public sealed record CatalogRow(CatalogSource Source, string Status, CatalogLookup? Lookup,
    IReadOnlyList<LocalGame> Locations);
public sealed record CatalogPage(IReadOnlyList<CatalogRow> Results, int Total, int Page, int PageSize, bool More);
public sealed record CatalogStatistics(int Titles, int Linked, int Pending, int NeedsReview, int NotFound,
    int Errors, int PlayRecords, int LocalDirectories, int ArchiveLocations);
public sealed record CatalogBatchRequest(int Limit = 10, string? Key = null, bool Retry = false);
public sealed record CatalogBatchResult(int Processed, int Imported, int LinkedPlays, int LinkedLocal,
    int Pending, IReadOnlyList<CatalogLookup> Results);
public sealed record CatalogSearch(string Key, string Query);
public sealed record CatalogResolve(string Key, string NovelId);

public interface ICatalogStore
{
    IReadOnlyList<LocalGame> LocalGames();
    LocalGameImportResult ImportLocalGames(IReadOnlyList<LocalGameInput> rows);
    LocalGame LinkLocalGame(string id, string novelId, long revision);
    IReadOnlyList<CatalogLookup> CatalogLookups();
    void SaveCatalogLookup(CatalogLookup lookup);
}

public static class CatalogRules
{
    public static string Key(string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 500 || title.Any(char.IsControl))
            throw new ArgumentException("作品标题须为 1–500 字符且不能包含控制字符。");
        var key = PlayRules.NormalizeTitle(title);
        return key.Length == 0 ? PlayRules.Hash(System.Text.Encoding.UTF8.GetBytes(title)) : key;
    }
    public static IEnumerable<string> Titles(VisualNovel novel) => novel.Titles.SelectMany(t => new[] { t.Title, t.Latin ?? "" })
        .Concat(novel.Aliases).Concat([novel.Title, novel.AltTitle ?? ""]).Where(t => !string.IsNullOrWhiteSpace(t) && t.Length <= 500 && !t.Any(char.IsControl));
    public static CatalogCandidate Candidate(VisualNovel n) => new(n.Id, n.Title,
        n.Titles.FirstOrDefault(t => t.Main)?.Title ?? n.AltTitle, n.Released, n.Developers);
    public static string LocalId(LocalGameInput value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Key(value.Title);
        if (string.IsNullOrWhiteSpace(value.Machine) || value.Machine.Length > 128 || value.Machine.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(value.Path) || value.Path.Length > 4096 || value.Path.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(value.Source) || value.Source.Length > 255 || value.Source.Any(char.IsControl) ||
            value.CompanyHint?.Length > 255 || value.SourceSha256 is null || value.SourceSha256.Length != 64 ||
            value.SourceSha256.Any(c => !char.IsAsciiHexDigit(c)) || value.ObservedAt == default)
            throw new ArgumentException("本地目录来源、路径或核对信息无效。");
        var path = value.Path.Replace('\\', '/').TrimEnd('/');
        var windows = path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/';
        if ((!windows && !path.StartsWith('/')) || path.Split('/').Any(p => p is "." or ".."))
            throw new ArgumentException("本地游戏需要绝对路径，不能包含 . 或 .. 段。");
        return PlayRules.Hash(System.Text.Encoding.UTF8.GetBytes(value.Machine.ToUpperInvariant() + "\0" + (windows ? path.ToUpperInvariant() : path)));
    }
}
