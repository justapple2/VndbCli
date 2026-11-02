using Vndb.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using Vndb.Local;
using Vndb.Core;

namespace Vndb.Desktop.ViewModels;

public sealed partial class BookViewModel(LibraryItemView? item, MetadataSnapshot? metadata, IReadOnlyList<PersonalPlayView>? plays = null, bool associationNeedsReview = false, bool summaryOnly = false) : ObservableObject
{
    private IReadOnlyList<PersonalPlayView> _plays = plays?.Where(p => p.DeletedAt is null).ToArray() ?? [];
    private bool _needsPlayReview = associationNeedsReview || !summaryOnly && plays is null && item?.PlayCount > 0;
    private bool _summaryPlays = summaryOnly;
    public bool PlaysPending => _summaryPlays;
    public bool DetailsPending { get; private set; } = summaryOnly;
    [ObservableProperty] private bool _selected;
    public bool IsLocal { get; set; }
    public bool HasInstallation { get; private set; }
    public string LocalObservation { get; private set; } = "";
    public bool CanLaunchObserved { get; private set; } = true;
    public bool HasInstallationRecord { get; private set; }
    public bool HasMissingResource { get; private set; }
    public bool HasUnknownResource { get; private set; }
    public bool LaunchConfigured { get; private set; }
    private bool _observedReady;
    private bool _observationProblem;
    public bool CanQuickLaunch => HasInstallation && LaunchConfigured && CanLaunchObserved;
    public bool HasLocalProblem => HasMissingResource || HasUnknownResource || _observationProblem;
    public string ResourceLabel => HasInstallation ? HasLocalProblem ? "本机：有安装 · 部分资源待核对" : "本机：已登记安装" :
        PackagePaths.Count > 0 ? HasInstallationRecord ? "本机：有安装包 · 安装缺失／待核对" : "本机：仅有安装包" :
        HasUnknownResource ? "本机：资源未知／待核对" : HasMissingResource ? "本机：登记资源缺失" : "本机：未登记资源";
    public string ReportedPresence => Item?.State is { } state ? state.IsLocal ? "Web上报：已有" : "Web上报：未标记已有" : "Web上报：未读取";
    public string LaunchLabel => !HasInstallation ? "启动：无可用安装" : !LaunchConfigured ? "启动：未配置入口" :
        !CanLaunchObserved ? "启动：待核对（" + LocalObservation + "）" : _observedReady ? "启动：可复核启动" : "启动：已配置，使用前复核";
    public void SetResourceState(bool registered, bool missing, bool unknown, bool configured)
    { HasInstallationRecord = registered; HasMissingResource = missing; HasUnknownResource = unknown; LaunchConfigured = configured; ResourceChanged(); }
    private void ResourceChanged()
    {
        foreach (var name in new[] { nameof(HasInstallationRecord), nameof(HasMissingResource), nameof(HasUnknownResource), nameof(LaunchConfigured),
            nameof(HasLocalProblem), nameof(ResourceLabel), nameof(ReportedPresence), nameof(LaunchLabel), nameof(CanQuickLaunch) }) OnPropertyChanged(name);
    }
    public void SetObservation(IReadOnlyList<LocalInstallationObservation> rows, IReadOnlyList<LaunchProfile>? profiles = null)
    {
        LocalObservation = string.Join(" / ", rows.Select(r => ObservationText.Reason(r.Reason)).Distinct());
        CanLaunchObserved = rows.Count == 0 || rows.Any(r => r.Status == "present" && (r.LaunchState == "not_configured" ||
            (profiles is null || r.ReadyProfiles is null ? r.LaunchState == "ready" : profiles.Any(p => r.ReadyProfiles.Contains(p.Id)))));
        _observedReady = rows.Any(r => r.Status == "present" && r.LaunchState == "ready") && CanLaunchObserved;
        _observationProblem = rows.Any(r => r.Reason != "present");
        OnPropertyChanged(nameof(LocalObservation)); OnPropertyChanged(nameof(CanLaunchObserved));
        ResourceChanged();
    }
    public IReadOnlyList<string> PackagePaths { get; private set; } = [];
    public IReadOnlyList<string> PackageRecords { get; private set; } = [];
    public string PackageLocations => string.Join('\n', PackageRecords.Select(p => "安装包相对位置（仅本机）：" + p));
    public void SetOwnership(bool installed, IReadOnlyList<string> packages, IReadOnlyList<string>? records = null)
    {
        HasInstallation = installed; PackagePaths = packages; PackageRecords = records ?? packages; IsLocal = installed || packages.Count > 0;
        foreach (var name in new[] { nameof(HasInstallation), nameof(PackagePaths), nameof(PackageRecords), nameof(PackageLocations), nameof(IsLocal), nameof(LocalLabel) }) OnPropertyChanged(name);
        ResourceChanged();
    }
    public DateTimeOffset? LastOpenedAt { get; set; }
    public DateTimeOffset? AddedAt => Item?.Item.CreatedAt;
    public string? Released => Metadata?.Novel?.Released;
    public string LastOpenedLabel => LastOpenedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "尚未从本客户端打开";
    public IReadOnlyList<string> Companies => new[] { Item?.Item.Details.CompanyOverride }.OfType<string>()
        .Concat(Metadata?.Developers.Count > 0 ? Metadata.Developers.Select(p => p.Original ?? p.Name) : [Company])
        .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public string ShelfState => NeedsPlayReview ? "关联待核对" : _summaryPlays ? Item?.State?.LatestStatus is { } latest ? Status(latest) : Progress.HasRecord ? string.Join(" / ", Progress.Statuses.Select(Status)) : "未记录" : Plays.Count > 0 ? Status(Plays.OrderByDescending(p => p.UpdatedAt).First().Details.Status) : "未记录";
    public string CollectionLabel => Item is null ? "公共资料 · 未加入书架" : "个人书架";
    public string LocalLabel => HasInstallation ? PackagePaths.Count > 0 ? "▣ 本机安装 · 有安装包" : "▣ 本机安装" : PackagePaths.Count > 0 ? "▣ 本机有安装包" : PackageRecords.Count > 0 ? "安装包记录：缺失／待核对" : IsLocal ? "本机有副本" : "";
    public LibraryItemView? Item { get; private set; } = item;
    public MetadataSnapshot? Metadata { get; private set; } = metadata;
    public IReadOnlyList<PersonalPlayView> Plays => _plays;
    public bool NeedsPlayReview => _needsPlayReview || _summaryPlays && Item?.State?.Progress.NeedsReview == true || Plays.Any(p => !PersonalRules.States.Contains(p.Details.Status));
    public PersonalProgress Progress => _summaryPlays && Item?.State is { } state ? state.Progress : PersonalProgress.FromStatuses(Plays.Select(p => p.Details.Status), NeedsPlayReview);
    public bool IsUnplayed => Progress.State == "unplayed";
    public bool IsUnrecorded => Progress.State == "unrecorded";
    public string PlayAssociationNotice => NeedsPlayReview ? "关联待核对：请联网刷新或读取此条目的记录；未将不明记录归入本作品。" : "";
    public void ApplyPlays(IReadOnlyList<PersonalPlayView> values, bool needsReview = false)
    {
        _plays = values.Where(p => p.DeletedAt is null).ToArray(); _needsPlayReview = needsReview; _summaryPlays = false;
        foreach (var name in new[] { nameof(Plays), nameof(PlaysPending), nameof(HasPlayed), nameof(IsUnplayed), nameof(IsUnrecorded), nameof(Progress), nameof(NeedsPlayReview), nameof(ShelfState), nameof(PlaySummary), nameof(PlayAssociationNotice) }) OnPropertyChanged(name);
    }
    public string Id => Item?.Item.Id ?? Metadata?.VndbId ?? "";
    public string Title => Item?.Item.Details.TitleOverride ?? Metadata?.Novel?.Titles.FirstOrDefault(t => t.Lang == "ja" && t.Main)?.Title
        ?? Metadata?.Novel?.Titles.FirstOrDefault(t => t.Lang == "ja")?.Title ?? Metadata?.Novel?.AltTitle ?? Metadata?.Title ?? Item?.Item.Details.Title ?? "未命名";
    public string Company => Metadata?.Developers.Count > 0
        ? string.Join(" / ", Metadata.Developers.Select(p => p.Original ?? p.Name)) : Item?.Item.Details.CompanyHint ?? "会社待补全";
    public string GroupCompany => Item?.Item.Details.CompanyOverride ?? Metadata?.Developers.FirstOrDefault()?.Original
        ?? Metadata?.Developers.FirstOrDefault()?.Name ?? Item?.Item.Details.CompanyHint ?? "";
    public bool HasPlayed => Progress.HasPlayed;
    public void ApplyDetails(LibraryItemView value)
    {
        if (Item is null || value.Item.Id != Item.Item.Id || value.Item.DeletedAt is not null || value.Item.Details.NovelId != Item.Item.Details.NovelId || value.Item.Revision < Item.Item.Revision)
            throw new InvalidDataException("详情关联或修订已变化，请刷新书架。");
        Item = _summaryPlays && value.State is null ? value with { State = Item.State } : value; Metadata = value.Metadata; DetailsPending = false;
        foreach (var name in new[] { nameof(Item), nameof(Metadata), nameof(DetailsPending), nameof(Title), nameof(Company), nameof(Companies), nameof(GroupCompany), nameof(Released), nameof(Subtitle), nameof(Description), nameof(BookNotes), nameof(ReportedPresence) }) OnPropertyChanged(name);
    }
    public string Subtitle => string.Join(" · ", new[] { Metadata?.VndbId, Metadata?.Novel?.Released,
        Item is not null && LocalInstallationRegistration.IsPending(Item.Item.Id) ? "本机登记待同步" : null }.Where(s => !string.IsNullOrEmpty(s)));
    public string PlaySummary => _summaryPlays ? (NeedsPlayReview ? "关联待核对" : !Progress.HasRecord ? "未记录游玩" : string.Join(" · ", Progress.Statuses.Select(Status))) + $" / {Item?.PlayCount ?? 0} 条记录 · 按需读取" : (Plays.Count == 0 ? NeedsPlayReview ? "关联待核对" : "未记录游玩" :
        string.Join(" · ", Plays.Select(p => Status(p.Details.Status)).Distinct()) + " / " + Plays.Count + " 条记录") + (NeedsPlayReview && Plays.Count > 0 ? " · 部分关联待核对" : "");
    public string Description => Metadata?.Novel?.Description ?? (DetailsPending ? "作品详情尚未加载；读取详情后显示简介及完整资料。" : "暂无简介；可通过查询补全资料。");
    public IReadOnlyList<DetailField> BookNotes => new[] { new DetailField("书架备注一", Item?.Item.Details.Notes ?? ""), new("书架备注二", Item?.Item.Details.ExtraNotes ?? "") }.Where(f => f.Value.Length > 0).ToArray();
    public static string Status(string value) => value switch { "not_started" => "未游玩", "playing" => "游玩中", "played" => "已游玩", "completed" => "本次完成", "on_hold" => "搁置", "dropped" => "放弃", _ => "关联待核对" };
}
