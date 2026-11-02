using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Contracts;
using Vndb.Desktop.Services;
using Vndb.Local;
using Vndb.Core;

namespace Vndb.Desktop.ViewModels;

public sealed record CatalogChoice(string Code, string Label);
public sealed partial class CatalogBookViewModel(PublicCatalogEntry entry) : ObservableObject
{
    public PublicCatalogEntry Entry { get; } = entry;
    [ObservableProperty] private bool _isSelected;
    public MetadataSnapshot Metadata => Entry.Metadata;
    public string Id => Metadata.VndbId;
    private PresentedTitle _title = NovelPresentation.Title(entry.Metadata.Novel, entry.Metadata.Title);
    public string Title => _title.Title;
    public string JapaneseSubtitle => _title.JapaneseSubtitle;
    public bool HasJapaneseSubtitle => JapaneseSubtitle.Length > 0;
    public void SetTitlePreference(string value, bool showJapanese)
    { _title = NovelPresentation.Title(Metadata.Novel, Metadata.Title, value, showJapanese); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(JapaneseSubtitle)); OnPropertyChanged(nameof(HasJapaneseSubtitle)); }
    public string Company => Metadata.Developers.Count == 0 ? "会社待补全" : string.Join(" / ", Metadata.Developers.Select(p => p.Original ?? p.Name));
    public string Personal => Entry.Personal.Label;
    public string Presence => Entry.Personal.IsLocal ? "▣ 本地已有（客户端上报）" : "本地存在：未上报";
    public string Source => $"{PublicCatalogRules.SourceLabel(Metadata.Provenance)} · 观测 {Metadata.FetchedAt:yyyy-MM-dd} · {(Metadata.Completeness == "partial_text" ? "文字部分覆盖" : "文字资料")} · {(Entry.ReleasesKnown ? "有发行目录" : "发行目录未知")}";
}
public sealed record CatalogRow(IReadOnlyList<CatalogBookViewModel> Books, int Columns);

/// <summary>Explicit server paging; no filtering of just the visible page or implicit network work.</summary>
public sealed partial class PublicCatalogViewModel(IDesktopLibrary library,
    Func<Func<CancellationToken, Task>, Task> run, Action<string> status, Action<MetadataResponse> showDetails, Func<LocalQueryScope?>? historyScope = null) : ObservableObject
{
    private string _titlePreference = "japanese";
    private bool _japaneseSubtitle = true;
    public void SetTitlePreference(string value, bool showJapanese)
    { _titlePreference = value; _japaneseSubtitle = showJapanese; foreach (var book in Books) book.SetTitlePreference(value, showJapanese); }
    private SearchHistoryViewModel? _history;
    public SearchHistoryViewModel History => _history ??= new(historyScope ?? (() => null), "catalog", text => Query = text, run);
    [ObservableProperty] private string _producerQuery = "";
    [ObservableProperty] private IReadOnlyList<CatalogProducerChoice> _producers = [];
    [ObservableProperty] private CatalogProducerChoice? _producer;
    private long _producerGeneration;
    public event Action? DisplayChanging;
    public event Action? DisplayChanged;
    private readonly Dictionary<string, double> _positions = [];
    public string ViewKey => (_applied is null ? "empty" : LocalArchiveFormat.Hash(_applied)) + "/" + ListMode;
    public void RememberPosition(string key, double offset)
    { if (!double.IsFinite(offset)) return; _positions.Remove(key); _positions[key] = Math.Clamp(offset, 0, 10000000); if (_positions.Count > 128) _positions.Remove(_positions.Keys.First()); }
    public double Position(string key) => _positions.GetValueOrDefault(key);
    public void CaptureViewport() => DisplayChanging?.Invoke();
    partial void OnListModeChanging(bool value) => DisplayChanging?.Invoke();
    partial void OnListModeChanged(bool value) => DisplayChanged?.Invoke();
    partial void OnProducerChanged(CatalogProducerChoice? value) { if (value is not null) ProducerId = value.Id; }
    [RelayCommand] private Task FindProducersAsync() => run(async ct =>
    {
        var generation = ++_producerGeneration; var query = ProducerQuery;
        var rows = await library.CatalogProducersAsync(query, CacheOnly, ct); ct.ThrowIfCancellationRequested();
        if (generation != _producerGeneration) return;
        var previous = Producer; Producers = rows; Producer = rows.FirstOrDefault(p => p.Id == ProducerId) ?? previous;
        status("会社选项已读取；选择只修改筛选草稿，点击浏览后生效。最多60项，可输入名称缩小范围。");
    });
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _producerId = "";
    [ObservableProperty] private string _platform = "";
    [ObservableProperty] private bool _includeMtl;
    [ObservableProperty] private bool _cacheOnly;
    [ObservableProperty] private bool _listMode;
    [ObservableProperty] private bool _filtersExpanded;
    [ObservableProperty] private CatalogChoice _language = new("", "全部发行语言");
    [ObservableProperty] private CatalogChoice _releaseType = new("", "全部版本类型");
    [ObservableProperty] private CatalogChoice _sort = new("personal", "个人优先：曾玩过 → 已有 → 全库");
    [ObservableProperty] private IReadOnlyList<CatalogBookViewModel> _books = [];
    [ObservableProperty] private IReadOnlyList<CatalogRow> _rows = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectedTitle)), NotifyCanExecuteChangedFor(nameof(DetailsCommand))] private CatalogBookViewModel? _selected;
    public string SelectedTitle => Selected?.Title ?? "选择作品后可读取详情";
    [ObservableProperty] private string _summary = "点击浏览，从Web已收录目录筛选；不会联网补全。";
    [ObservableProperty] private string _coverage = "NAS不可达时仅有此前保存的相同筛选／页码JSON，不是完整离线目录。";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FilterStatus))] private bool _isLoading;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmpty))] private bool _hasLoaded;
    private PublicCatalogQuery? _applied;
    private long _generation;
    private int _columns = 3;
    private bool _more;
    public IReadOnlyList<CatalogChoice> Languages { get; } = [new("", "全部发行语言"), new("ja", "日语"), new("zh-Hans", "简体中文"), new("zh-Hant", "繁体中文"), new("en", "英语"), new("ko", "韩语")];
    public IReadOnlyList<CatalogChoice> ReleaseTypes { get; } = [new("", "全部版本类型"), new("complete", "完整"), new("trial", "试玩"), new("partial", "部分"), new("patch", "补丁")];
    public IReadOnlyList<CatalogChoice> Sorts { get; } = [new("personal", "个人优先：曾玩过 → 已有 → 全库"), new("updated", "资料观测时间"), new("title", "标题"), new("released_desc", "发售日期新→旧"), new("released_asc", "发售日期旧→新")];
    public bool IsEmpty => HasLoaded && Books.Count == 0;
    public bool HasPendingFilters => Draft() != (_applied is { } q ? q with { Page = 1 } : new PublicCatalogQuery());
    public string FilterStatus => IsLoading ? "正在读取，原结果保留…" :
        (HasPendingFilters ? "筛选已修改，点击“应用筛选并浏览”后生效。" : "") +
        (_applied is { } q ? "当前结果条件：" + AppliedFilters(q) : "尚未浏览；更改条件不会自动联网。");
    private string AppliedFilters(PublicCatalogQuery q)
    {
        var parts = new List<string>();
        if (q.Query.Length > 0) parts.Add(q.Query);
        if (q.ProducerId is not null) parts.Add(Producers.FirstOrDefault(p => p.Id == q.ProducerId)?.Label ?? q.ProducerId);
        if (q.Language.Length > 0) parts.Add(Languages.FirstOrDefault(l => l.Code == q.Language)?.Label ?? q.Language);
        if (q.ReleaseType.Length > 0) parts.Add(ReleaseTypes.First(t => t.Code == q.ReleaseType).Label);
        if (q.Platform.Length > 0) parts.Add(q.Platform);
        if (q.IncludeMtl) parts.Add("包含机翻");
        if (parts.Count == 0) parts.Add("全部作品");
        parts.Add("排序：" + Sorts.First(s => s.Code == q.Sort).Label);
        return string.Join(" · ", parts);
    }
    private bool CanBrowse => !IsLoading;
    private bool CanPrevious => !IsLoading && _applied is { Page: > 1 };
    private bool CanNext => !IsLoading && _applied is not null && _more;
    private bool CanDetails => !IsLoading && Selected is not null;
    private PublicCatalogQuery Draft() => new(Query.Trim(), string.IsNullOrWhiteSpace(ProducerId) ? null : ProducerId.Trim(), Language.Code, ReleaseType.Code, Platform.Trim(), IncludeMtl, Sort.Code);
    private void DraftChanged() { OnPropertyChanged(nameof(HasPendingFilters)); OnPropertyChanged(nameof(FilterStatus)); }
    partial void OnQueryChanged(string value) => DraftChanged();
    partial void OnProducerIdChanged(string value) { if (Producer?.Id != value) Producer = null; DraftChanged(); }
    partial void OnPlatformChanged(string value) => DraftChanged();
    partial void OnIncludeMtlChanged(bool value) => DraftChanged();
    partial void OnLanguageChanged(CatalogChoice value) => DraftChanged();
    partial void OnReleaseTypeChanged(CatalogChoice value) => DraftChanged();
    partial void OnSortChanged(CatalogChoice value) => DraftChanged();
    partial void OnBooksChanged(IReadOnlyList<CatalogBookViewModel> value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnSelectedChanged(CatalogBookViewModel? oldValue, CatalogBookViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }
    partial void OnIsLoadingChanged(bool value) => NavigationChanged();
    private void NavigationChanged()
    {
        BrowseCommand.NotifyCanExecuteChanged(); PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged(); DetailsCommand.NotifyCanExecuteChanged(); ResetFiltersCommand.NotifyCanExecuteChanged();
        DraftChanged();
    }
    public void Clear() { _generation++; _producerGeneration++; Producers = []; Producer = null; ProducerId = ""; ProducerQuery = ""; _applied = null; _more = false; Books = []; Rows = []; Selected = null; HasLoaded = false; IsLoading = false; Summary = "连接已切换，请重新浏览。"; Coverage = "尚未读取此Web的目录覆盖范围。"; _positions.Clear(); NavigationChanged(); }
    public void Resize(double width) { var columns = Math.Clamp((int)(width / 210), 1, 6); if (_columns == columns) return; DisplayChanging?.Invoke(); _columns = columns; RebuildRows(); DisplayChanged?.Invoke(); }
    private void RebuildRows() => Rows = Books.Chunk(_columns).Select(b => new CatalogRow(b, _columns)).ToArray();
    [RelayCommand(CanExecute = nameof(CanBrowse))] private Task BrowseAsync() => run(async ct =>
    {
        var generation = _generation;
        var query = PublicCatalogRules.Validate(Draft());
        if (query.Query.Length > 0) await History.RecordAsync(query.Query, ct);
        ct.ThrowIfCancellationRequested(); if (generation != _generation) return;
        await LoadAsync(query, ct);
    });
    [RelayCommand(CanExecute = nameof(CanPrevious))] private Task PreviousAsync() => run(ct => _applied is { Page: > 1 } q ? LoadAsync(q with { Page = q.Page - 1 }, ct) : Task.CompletedTask);
    [RelayCommand(CanExecute = nameof(CanNext))] private Task NextAsync() => run(ct => _applied is { } q && _more ? LoadAsync(q with { Page = q.Page + 1 }, ct) : Task.CompletedTask);
    [RelayCommand(CanExecute = nameof(CanBrowse))] private void ResetFilters()
    {
        Query = ""; ProducerId = ""; Producer = null; Platform = ""; IncludeMtl = false;
        Language = Languages[0]; ReleaseType = ReleaseTypes[0]; Sort = Sorts[0];
    }
    private async Task LoadAsync(PublicCatalogQuery query, CancellationToken ct)
    {
        var generation = ++_generation; IsLoading = true;
        try
        {
            var page = await library.BrowseCatalogAsync(query, CacheOnly, ct);
            ct.ThrowIfCancellationRequested();
            if (generation != _generation) return;
            DisplayChanging?.Invoke();
            _applied = query; _more = page.More;
            var selectedId = Selected?.Id;
            Books = page.Items.Select(i => { var book = new CatalogBookViewModel(i); book.SetTitlePreference(_titlePreference, _japaneseSubtitle); return book; }).ToArray(); RebuildRows();
            Selected = Books.FirstOrDefault(b => b.Id == selectedId);
            Summary = $"{(page.Source == "client_catalog_cache" ? "本机历史页JSON" : "Web已收录目录")} · {page.Total} 部 · 第 {page.Page} 页 · 读取 {page.ObservedAt.ToLocalTime():g}";
            Coverage = PublicCatalogRules.CoverageLabel(page.Coverage);
            HasLoaded = true;
            DisplayChanged?.Invoke();
            status("目录已读取；全库筛选排序后分页。翻页沿用已应用条件，修改筛选后请点击浏览。" + (page.Source == "client_catalog_cache" ? "当前是历史页；状态可能过期，没有自动重试。" : ""));
        }
        finally { if (generation == _generation) { IsLoading = false; NavigationChanged(); } }
    }
    [RelayCommand(CanExecute = nameof(CanDetails))] private Task DetailsAsync() => run(async ct =>
    {
        var generation = _generation;
        var id = Selected?.Id ?? throw new ArgumentException("请先选择目录中的作品。");
        var result = await library.QueryAsync(id, CacheOnly ? "cache" : "auto", ct);
        ct.ThrowIfCancellationRequested();
        if (generation != _generation || Selected?.Id != id) return;
        showDetails(result); status("已进入查询结果，可打开详情；个人记录仍在我的书架中维护。");
    });
}
