using System.Text.Json.Serialization;

namespace Vndb.Core;

public sealed record ReleaseLanguage(string Lang, string? Title, string? Latin, bool Mtl, bool Main);
public sealed record ReleaseNovel(string Id, string Rtype);
public sealed record ReleaseProducer(string Id, string Name, string? Original, bool Developer, bool Publisher);
public sealed record NovelRelease
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    [JsonPropertyName("alttitle")] public string? AltTitle { get; init; }
    public string? Released { get; init; }
    public bool Official { get; init; }
    public bool Patch { get; init; }
    public IReadOnlyList<ReleaseLanguage> Languages { get; init; } = [];
    public IReadOnlyList<string> Platforms { get; init; } = [];
    public IReadOnlyList<ReleaseNovel> Vns { get; init; } = [];
    public IReadOnlyList<ReleaseProducer> Producers { get; init; } = [];
}
public sealed record ReleaseCatalog(IReadOnlyList<NovelRelease> Items, DateTimeOffset FetchedAt);

public static class ReleaseRules
{
    public const int MaximumReleases = 1000;
    public const int MaximumBytes = 4 * 1024 * 1024;
    public static void Validate(ReleaseCatalog catalog, string novelId)
    {
        if (catalog.Items is null || catalog.Items.Count > MaximumReleases || catalog.FetchedAt == default ||
            catalog.Items.Any(r => r is null) || catalog.Items.Select(r => r.Id).Distinct().Count() != catalog.Items.Count)
            throw new ArgumentException("发行目录无效或超过限额。");
        foreach (var r in catalog.Items)
        {
            if (VndbId.Normalize(r.Id, 'r') != r.Id || !Text(r.Title, 500, true) ||
                r.Languages is null || r.Languages.Count is < 1 or > 100 ||
                r.Languages.Any(l => l is null || !Text(l.Lang, 20) || l.Title?.Length > 500 || l.Latin?.Length > 500) ||
                r.Languages.Count(l => l.Main) != 1 || r.Languages.DistinctBy(l => l.Lang).Count() != r.Languages.Count ||
                r.Platforms is null || r.Platforms.Count > 100 || r.Platforms.Any(p => !Text(p, 30)) ||
                r.Vns is null || r.Vns.Count is < 1 or > 1000 || !r.Vns.Any(v => v?.Id == novelId) ||
                r.Vns.Any(v => v is null || VndbId.Normalize(v.Id, 'v') != v.Id || v.Rtype is not ("trial" or "partial" or "complete")) ||
                r.Vns.DistinctBy(v => v.Id).Count() != r.Vns.Count ||
                r.Producers is null || r.Producers.Count > 1000 || r.Producers.Any(p => p is null || VndbId.Normalize(p.Id, 'p') != p.Id || !Text(p.Name, 500)) ||
                r.Producers.DistinctBy(p => p.Id).Count() != r.Producers.Count || r.AltTitle?.Length > 500 || r.Released?.Length > 20)
                throw new ArgumentException("发行版本字段或作品关联无效。");
        }
        if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(catalog, VndbJson.Options).Length > MaximumBytes)
            throw new ArgumentException("发行目录超过4MiB。");
    }
    private static bool Text(string? text, int max, bool title = false) => !string.IsNullOrWhiteSpace(text) && text.Length <= max &&
        !text.Any(c => char.IsControl(c) && !(title && c is '\t' or '\r' or '\n'));
    public static ReleaseCatalog? Prefer(ReleaseCatalog? incoming, ReleaseCatalog? previous) =>
        incoming is null || previous is not null && previous.FetchedAt >= incoming.FetchedAt ? previous : incoming;
    public static string Language(string code) => code switch { "ja" => "日语", "en" => "英语", "zh-Hans" => "简体中文", "zh-Hant" => "繁体中文", "ko" => "韩语", _ => code };
    public static string Describe(NovelRelease release, string novelId) =>
        $"{release.AltTitle ?? release.Title} · {release.Id}\n" +
        string.Join(" / ", release.Languages.Select(l => Language(l.Lang) + (l.Mtl ? "（机翻）" : ""))) +
        $" · {string.Join(" / ", release.Platforms)} · {(release.Official ? "官方" : "非官方")}" +
        (release.Patch ? " · 补丁" : "") + " · " + (release.Vns.FirstOrDefault(v => v.Id == novelId)?.Rtype switch { "trial" => "试玩", "partial" => "部分", "complete" => "完整", _ => "未知类型" }) +
        $" · 发行日期：{release.Released ?? "未知"}";
}
