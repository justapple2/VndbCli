using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vndb.Core;

namespace Vndb.Library;

public sealed record PlayDetails(string Title, string? NovelId, string Status, DateOnly? StartedOn,
    DateOnly? FinishedOn, int? DurationMinutes, decimal? Score, string? Review, string? Notes,
    string? ExtraNotes, string? Language);
public sealed record PlaySource(string FileName, string Sha256, string Sheet, int Row,
    string? MonthLabel, string RawJson, IReadOnlyList<string> Warnings);
public sealed record PlayRecord(string Id, PlayDetails Details, PlaySource? Source, long Revision,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt = null)
{
    public bool HasPlayed => PersonalRules.IsPlayed(Details.Status);
}
public sealed record PlayEdit(PlayDetails Details, long? ExpectedRevision);
public sealed record PlayPage(IReadOnlyList<PlayRecord> Results, int Page, int PageSize, int Total, bool More);
public sealed record PlayStatistics(int Total, int Played, int Completed, int Rated, int Unmatched,
    long TotalMinutes, int KnownDurationCount, IReadOnlyList<int> Years);
public sealed record PlayImportRow(PlayDetails Details, string Sheet, int Row, string? MonthLabel,
    string RawJson, IReadOnlyList<string> Warnings);
public sealed record PlayImportBatch(string SourceFileName, string SourceSha256, IReadOnlyList<PlayImportRow> Rows);
public sealed record PlayImportResult(string SourceSha256, int Rows, int Inserted, int Existing, int Linked, int Unmatched);
public sealed class PlayConflictException(string message) : Exception(message);

public static class PlayRules
{
    public static readonly IReadOnlyList<string> States = PersonalRules.States;
    public static void Validate(PlayDetails details, Func<string, LibraryEntry?> getNovel)
    {
        ArgumentNullException.ThrowIfNull(details);
        PersonalRules.Validate(new(details.Title, details.NovelId, details.Status, details.StartedOn, details.FinishedOn,
            details.DurationMinutes, details.Score, details.Review, details.Notes, details.ExtraNotes, details.Language));
        if (details.NovelId is { } id && getNovel(id) is null)
            throw new ArgumentException("关联的 VNDB 作品需要先收录到本地资料库；也可以暂不关联。");
    }
    private static void Text(string? value, int max, bool required = false)
    {
        if ((required && string.IsNullOrWhiteSpace(value)) || value?.Length > max ||
            (value?.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) ?? false))
            throw new ArgumentException("个人记录文本为空、过长或包含无效控制字符。");
    }
    public static void ValidateBatch(PlayImportBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Text(batch.SourceFileName, 255, true);
        ArgumentNullException.ThrowIfNull(batch.Rows);
        if (batch.SourceSha256 is null || batch.SourceSha256.Length != 64 || batch.SourceSha256.Any(c => !char.IsAsciiHexDigit(c)) ||
            batch.SourceSha256 != batch.SourceSha256.ToLowerInvariant() || batch.Rows.Count is < 1 or > 5000)
            throw new ArgumentException("导入需要源文件 SHA256 和 1–5000 条记录。");
        var keys = new HashSet<(string, int)>();
        foreach (var row in batch.Rows)
        {
            Text(row.Sheet, 100, true);
            if (row.Row < 1 || !keys.Add((row.Sheet, row.Row))) throw new ArgumentException("来源行号无效或重复。");
            Text(row.MonthLabel, 100); Text(row.RawJson, 100000, true);
            using var raw = JsonDocument.Parse(row.RawJson);
            ArgumentNullException.ThrowIfNull(row.Warnings);
            if (row.Warnings.Count > 20) throw new ArgumentException("来源警告过多。");
            foreach (var warning in row.Warnings) Text(warning, 500, true);
        }
    }
    public static string ImportId(PlayImportBatch batch, PlayImportRow row) => "play-" + Hash(
        Encoding.UTF8.GetBytes(batch.SourceSha256.ToLowerInvariant() + "\0" + row.Sheet + "\0" + row.Row));
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string BatchHash(PlayImportBatch batch) => Hash(JsonSerializer.SerializeToUtf8Bytes(batch, VndbJson.Options));
    // '+' distinguishes base games and fandiscs; width/spacing must not merge those identities.
    public static string NormalizeTitle(string title) => string.Concat(title.Normalize(NormalizationForm.FormKC)
        .EnumerateRunes().Where(r => Rune.IsLetterOrDigit(r) || r.Value == '+')
        .Select(r => Rune.ToUpperInvariant(r).ToString()));
    public static IReadOnlyDictionary<string, string> TitleMap(ILibraryStore store)
    {
        var pairs = new List<(string Title, string Id)>();
        for (var page = 1; ; page++)
        {
            var data = store.List(page: page, limit: 100);
            foreach (var entry in data.Results)
            {
                var n = entry.Novel;
                foreach (var title in n.Titles.Select(t => t.Title).Concat(n.Aliases).Concat([n.Title, n.AltTitle ?? ""]))
                    if (!string.IsNullOrWhiteSpace(title)) pairs.Add((NormalizeTitle(title), n.Id));
            }
            if (!data.More) break;
        }
        return pairs.GroupBy(pair => pair.Title).Where(group => group.Select(pair => pair.Id).Distinct().Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.Ordinal);
    }
    public static int? Year(PlayRecord record) => record.Details.FinishedOn?.Year ?? record.Details.StartedOn?.Year ??
        (int.TryParse(record.Source?.Sheet, out var year) ? year : null);
    public static PlayPage Page(IEnumerable<PlayRecord> all, string? query, string? status, bool? played, int? year,
        bool unmatched, string? novelId, int page, int limit)
    {
        LibraryRules.ValidatePage(page, limit);
        if (status is not null && !States.Contains(status)) throw new ArgumentException("无效的游玩状态筛选。");
        if (novelId is not null) novelId = VndbId.Normalize(novelId, 'v');
        var q = query?.Trim() ?? "";
        var results = all.Where(r => r.DeletedAt is null && (q.Length == 0 || string.Join('\n', r.Details.Title, r.Details.Notes, r.Details.ExtraNotes, r.Details.Review)
            .Contains(q, StringComparison.OrdinalIgnoreCase)) && (status is null || r.Details.Status == status) &&
            (played is null || r.HasPlayed == played) && (year is null || Year(r) == year) &&
            (!unmatched || r.Details.NovelId is null) && (novelId is null || r.Details.NovelId == novelId))
            .OrderByDescending(r => r.Details.FinishedOn ?? r.Details.StartedOn).ThenByDescending(r => r.UpdatedAt).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
        return new(results.Skip((page - 1) * limit).Take(limit).ToArray(), page, limit, results.Length, (long)page * limit < results.Length);
    }
    public static PlayStatistics Statistics(IEnumerable<PlayRecord> records)
    {
        var all = records.Where(r => r.DeletedAt is null).ToArray();
        return new(all.Length, all.Count(r => r.HasPlayed), all.Count(r => r.Details.Status == "completed"),
            all.Count(r => r.Details.Score is not null), all.Count(r => r.Details.NovelId is null),
            all.Sum(r => (long)(r.Details.DurationMinutes ?? 0)), all.Count(r => r.Details.DurationMinutes is not null),
            all.Select(Year).Where(y => y.HasValue).Select(y => y!.Value).Distinct().OrderDescending().ToArray());
    }
}
