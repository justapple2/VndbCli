using System.Text.Json;
using Vndb.Core;

namespace Vndb.Library;

public sealed record CatalogTask(string Key, string Title, bool Force = false);
public sealed record CatalogJob(string Id, IReadOnlyList<CatalogTask> Tasks, int IntervalSeconds,
    string Status, int Cursor, int Attempts, int Matched, int NeedsReview, int NotFound, int Errors, int Skipped,
    DateTimeOffset NextRunAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? LastError = null)
{
    public int Total => Tasks.Count;
    public string? CurrentTitle => Cursor < Tasks.Count ? Tasks[Cursor].Title : null;
}
public sealed record CatalogJobRequest(int? IntervalSeconds = null, string? Scope = null,
    IReadOnlyList<string>? Keys = null, bool RetryUnmatched = false);
public sealed record CatalogJobSummary(string Id, string Status, int Total, int Completed, int IntervalSeconds,
    string? CurrentTitle, int Attempts, int Matched, int NeedsReview, int NotFound, int Errors, int Skipped,
    DateTimeOffset NextRunAt, DateTimeOffset UpdatedAt, string? LastError);
public sealed record LibrarySearchRequest(string Query, string? Producer = null, int Page = 1, int Limit = 12);
public sealed record LibrarySearchCache(string Key, LibrarySearchRequest Request, IReadOnlyList<string> NovelIds,
    bool More, DateTimeOffset SavedAt);
public sealed record LibrarySearchResult(IReadOnlyList<LibraryEntry> Results, int Total, int Page, int PageSize,
    bool More, string Source, bool NeedsRemote = false);

public interface IAutomationStore
{
    CatalogJob? GetCatalogJob(string? id = null);
    CatalogJob QueueCatalogJob(IReadOnlyList<CatalogTask> tasks, int? intervalSeconds, DateTimeOffset now);
    CatalogJob? BeginCatalogJob(DateTimeOffset now);
    CatalogJob FinishCatalogItem(string id, string key, string outcome, DateTimeOffset now, string? error = null);
    CatalogJob PauseCatalogJob(string id, bool paused, DateTimeOffset now);
    LibrarySearchCache? GetLibrarySearchCache(string key);
    void SaveLibrarySearchCache(LibrarySearchCache cache);
}

public static class AutomationRules
{
    public static CatalogJobSummary? Summary(CatalogJob? j) => j is null ? null : new(j.Id, j.Status, j.Total,
        j.Cursor, j.IntervalSeconds, j.CurrentTitle, j.Attempts, j.Matched, j.NeedsReview, j.NotFound,
        j.Errors, j.Skipped, j.NextRunAt, j.UpdatedAt, j.LastError);
    public static bool Active(CatalogJob j) => j.Status is "queued" or "running" or "paused";
    public static void Interval(int? value)
    {
        if (value is < 3 or > 3600) throw new ArgumentException("后台任务间隔须为 3–3600 秒。");
    }
    public static LibrarySearchRequest Search(LibrarySearchRequest request)
    {
        if (request.Query is null || request.Query.Length > 200 || request.Query.Any(char.IsControl))
            throw new ArgumentException("请输入最多 200 字符的标题、别名或 VNDB ID。");
        LibraryRules.ValidatePage(request.Page, request.Limit);
        return request with { Query = request.Query.Trim(), Producer = string.IsNullOrWhiteSpace(request.Producer) ? null : VndbId.Normalize(request.Producer, 'p') };
    }
    public static string SearchKey(LibrarySearchRequest request) => PlayRules.Hash(JsonSerializer.SerializeToUtf8Bytes(
        request with { Query = request.Query.Normalize(System.Text.NormalizationForm.FormKC).ToUpperInvariant() }, VndbJson.Options));
}
