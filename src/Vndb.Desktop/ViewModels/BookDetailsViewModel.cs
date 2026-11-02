using CommunityToolkit.Mvvm.ComponentModel;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public sealed record DetailField(string Label, string Value);
public sealed record PlayDetailRow(PersonalPlayView Play)
{
    public string Identity => $"{Play.Id} · 修订 {Play.Revision}";
    public string Heading => $"{BookViewModel.Status(Play.Details.Status)} · {Play.Details.StartedOn?.ToString("yyyy-MM-dd") ?? "开始未知"} → {Play.Details.FinishedOn?.ToString("yyyy-MM-dd") ?? "结束未知"}";
    public string Time => $"手工基数：{Play.Details.DurationMinutes?.ToString() ?? "未知"} 分钟" +
        (Play.Time is { } t ? $"\n会话 {t.SessionSeconds} 秒 · 调整 {t.AdjustmentSeconds} 秒\n合计：{(t.TotalSeconds is { } total ? (total / 3600d).ToString("0.##") + " 小时" : "未知")} · 待核对 {t.PendingSessions}" : "");
    public string Score => "个人评分：" + (Play.Details.Score is { } s ? s.ToString("0.#") + " / 10" : "未评分");
    public string Review => Play.Details.Review ?? "尚未填写感想";
    public IReadOnlyList<DetailField> Notes => new[] { new DetailField("备注一", Play.Details.Notes ?? ""), new("备注二", Play.Details.ExtraNotes ?? ""), new("游玩语言", Play.Details.Language ?? "") }.Where(f => f.Value.Length > 0).ToArray();
}

/// <summary>Read-only presentation of one exact shelf item. Never probes files or infers launch permission.</summary>
public sealed partial class BookDetailsViewModel : ObservableObject
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CoverMetadata)), NotifyPropertyChangedFor(nameof(DetailsPending))] private BookViewModel? _book;
    public MetadataSnapshot? CoverMetadata => Book?.Metadata;
    public bool DetailsPending => Book?.DetailsPending == true;
    [ObservableProperty] private IReadOnlyList<DetailField> _metadata = [];
    [ObservableProperty] private IReadOnlyList<PlayDetailRow> _plays = [];
    [ObservableProperty] private bool _hasInstallation;
    [ObservableProperty] private bool _launchConfigured;
    [ObservableProperty] private string _localStatus = "没有本机登记";
    [ObservableProperty] private bool _isPersonal;
    [ObservableProperty] private IReadOnlyList<DetailField> _personalFields = [];
    [ObservableProperty] private IReadOnlyList<DetailField> _localFields = [];
    public ReleaseBrowserViewModel ReleaseBrowser { get; } = new();
    public RelationsViewModel Relations { get; } = new();
    public void Update(BookViewModel? book, IReadOnlyList<LocalInstallationView> installations, IReadOnlyList<LaunchProfile> profiles, LocalInstallationView? chosen,
        IEnumerable<MetadataSnapshot>? known = null, string titlePreference = "japanese")
    {
        Book = book; OnPropertyChanged(nameof(CoverMetadata)); OnPropertyChanged(nameof(DetailsPending));
        Plays = book?.Plays.Select(p => new PlayDetailRow(p)).ToArray() ?? [];
        var novel = book?.Metadata?.Novel;
        var fields = new List<DetailField>();
        IsPersonal = book?.Item is not null;
        if (book is not null) fields.AddRange([new("发行日期", novel?.Released ?? "未知"), new("原始语言", novel?.OriginalLanguage ?? "未知"),
            new("VNDB作品", book.Metadata?.VndbId ?? "尚未关联公共资料"), new("资料范围", book.CollectionLabel), new("VNDB公共评分", NovelPresentation.PublicRating(novel?.Rating, novel?.VoteCount))]);
        if (novel is not null) fields.AddRange([new("公共资料标题", NovelPresentation.Title(novel, novel.Title, "original").Title),
            new("开发会社（公共资料）", book?.Metadata?.Developers.Count > 0 ? string.Join(" / ", book.Metadata.Developers.Select(p => p.Original ?? p.Name)) : "未知"),
            new("发行平台", novel.Platforms.Count > 0 ? string.Join(" / ", novel.Platforms) : "未知"),
            new("已收录语言（不含机翻）", novel.Languages.Count > 0 ? string.Join(" / ", novel.Languages.Select(ReleaseRules.Language)) : "未知")]);
        if (book?.Metadata is { } source) fields.AddRange([new("资料来源与观测时间", $"{PublicCatalogRules.SourceLabel(source.Provenance)} · {source.FetchedAt.ToLocalTime():g}"), new("字段覆盖", (source.Completeness == "partial_text" ? "部分文字字段；简介等可能缺失" : "作品文字资料") + "；封面独立缓存，缺图不代表没有作品")]);
        Metadata = fields;
        ReleaseBrowser.Set(book); Relations.Set(book, known, titlePreference);
        PersonalFields = book?.Item is not { } personal ? [] : new[] { new DetailField("书架条目", personal.Item.Id), new("作品关联", personal.Item.Details.NovelId ?? "尚未关联"), new("书架记录标题", personal.Item.Details.Title),
            new("个人显示标题覆盖", personal.Item.Details.TitleOverride ?? "未覆盖，使用公共标题偏好"),
            new("个人会社覆盖", personal.Item.Details.CompanyOverride ?? "未覆盖，使用公共会社／原提示"), new("游玩状态", book.ShelfState) }.Concat(book.BookNotes).ToArray();
        LocalFields = book?.Item is null ? [] : new[] { new DetailField("本机资源", book.ResourceLabel), new("本机启动", book.LaunchLabel),
            new("存在上报", book.ReportedPresence), new("本机观测", book.LocalObservation), new("安装包记录（仅本机）", book.PackageLocations) }.Where(f => f.Value.Length > 0).ToArray();
        var exact = book?.Item is { } item ? installations.Where(i => i.Installation.RemovedAt is null && i.Components.Any(c => c.Details.ItemId == item.Item.Id)).ToArray() : [];
        HasInstallation = exact.Length > 0;
        var available = exact.Where(i => i.Installation.Status == "present").ToArray();
        var selected = available.Length == 1 ? available[0] : available.SingleOrDefault(i => i.Installation.Id == chosen?.Installation.Id);
        var components = selected?.Components.Where(c => c.Details.ItemId == book?.Item?.Item.Id).Select(c => c.Id).ToHashSet() ?? [];
        var commands = LaunchConfiguration.CommandChoices(profiles.Where(p => p.InstallationId == selected?.Installation.Id && p.ComponentId is not null && components.Contains(p.ComponentId))).Count;
        LaunchConfigured = selected is not null && commands == 1;
        LocalStatus = !HasInstallation ? book?.PackagePaths.Count > 0 ? "本地已有安装包；安装后再登记实际目录与启动入口" : "尚未关联本机安装；存在标签不代表可启动" : available.Length == 0 ? "本机安装缺失或待核对，请在本机登记中重新确认" :
            selected is null ? "有多个本机安装，请先明确选择" : commands == 0 ? "安装已登记，请配置本机启动入口" : commands > 1 ? "有多个启动命令，请进入配置明确选择" : "已配置启动入口；启动时仍会复核文件与配置";
    }
}
