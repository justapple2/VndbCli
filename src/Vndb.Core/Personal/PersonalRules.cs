namespace Vndb.Core;

public sealed record PersonalDetails(string Title, string? NovelId, string Status, DateOnly? StartedOn,
    DateOnly? FinishedOn, int? DurationMinutes, decimal? Score, string? Review, string? Notes, string? ExtraNotes, string? Language);

public static class PersonalRules
{
    public static IReadOnlyList<string> States { get; } = Array.AsReadOnly(new[] { "not_started", "playing", "played", "completed", "on_hold", "dropped" });
    public static bool IsPlayed(string? status) => status is "playing" or "played" or "completed" or "on_hold" or "dropped";
    public static void Validate(PersonalDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        Text(details.Title, 500, true);
        if (!States.Contains(details.Status)) throw new ArgumentException("无效的游玩状态。");
        if (details.NovelId is { } id && VndbId.Normalize(id, 'v') != id) throw new ArgumentException("关联的VNDB标识无效。");
        if (details.StartedOn > details.FinishedOn) throw new ArgumentException("结束日期不能早于开始日期。");
        if (details.DurationMinutes is < 0) throw new ArgumentException("实际游玩时长不能为负数。");
        if (details.Score is { } score && (score < 0 || score > 10 || decimal.Round(score, 1) != score))
            throw new ArgumentException("个人评分须为 0–10 分，最多一位小数；未知时留空。");
        if (details.Status == "not_started" && (details.StartedOn is not null || details.FinishedOn is not null || details.DurationMinutes is > 0))
            throw new ArgumentException("未玩状态不能包含实际游玩日期或时长。");
        Text(details.Review, 20000); Text(details.Notes, 10000); Text(details.ExtraNotes, 10000); Text(details.Language, 40);
    }
    private static void Text(string? value, int maximum, bool required = false)
    {
        if (required && string.IsNullOrWhiteSpace(value) || value?.Length > maximum || value?.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) == true)
            throw new ArgumentException("个人记录文本为空、过长或包含无效控制字符。");
    }
}
