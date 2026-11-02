namespace Vndb.Core;

public sealed record PresentedTitle(string Title, string JapaneseSubtitle);

/// <summary>Display policy only. Never supplies a filesystem name or a launch target.</summary>
public static class NovelPresentation
{
    public static bool ValidPreference(string? value) => value is "japanese" or "original" or "latin";
    public static PresentedTitle Title(VisualNovel? novel, string? fallback, string preference = "japanese", bool japaneseSubtitle = true, string? titleOverride = null)
    {
        string? Pick(string? language, bool latin = false) => novel?.Titles.Where(t => t.Lang == language).OrderByDescending(t => t.Main)
            .Select(t => latin ? t.Latin : t.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        var japanese = Pick("ja");
        static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
        var original = Pick(novel?.OriginalLanguage) ?? NonBlank(novel?.AltTitle) ?? NonBlank(novel?.Title);
        var preferred = preference switch { "original" => original, "latin" => Pick(novel?.OriginalLanguage, true) ?? novel?.Title, _ => japanese ?? original };
        var title = !string.IsNullOrWhiteSpace(titleOverride) ? titleOverride : NonBlank(preferred) ?? NonBlank(fallback) ?? "未命名";
        return new(title, japaneseSubtitle && !string.IsNullOrWhiteSpace(japanese) && japanese != title ? japanese : "");
    }
    public static string Relation(string code) => code switch
    {
        "preq" => "前作（preq）", "seq" => "续作（seq）", "side" => "外传（side）", "orig" => "原作（orig）",
        "alt" => "不同版本（alt）", "set" => "同一背景（set）", "char" => "角色关联（char）", "ser" => "系列（ser）", "fan" => "同人（fan）", _ => "关系：" + code
    };
    public static string PublicRating(double? rating, int? votes) => rating is > 0 and <= 100
        ? (rating.Value / 10).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " / 10" + (votes is { } count ? $" · {count} 票" : "")
        : "暂无评分";
}
