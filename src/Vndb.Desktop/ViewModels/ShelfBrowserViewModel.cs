using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Desktop.Services;

namespace Vndb.Desktop.ViewModels;

/// <summary>In-memory browsing state only. Never queries, launches or changes personal records.</summary>
public sealed partial class ShelfBrowserViewModel(Func<ShelfPreferences, Task>? savePreferences = null) : ObservableObject
{
    private sealed record State(string Filter = "", string Company = "全部会社", string Status = "全部状态",
        string Sort = "添加时间", bool Descending = true, bool LocalOnly = false, bool List = false,
        int Page = 1, string? SelectedId = null, bool FiltersExpanded = true, string Resource = "全部本机资源", bool Favorites = false, string Label = "");
    private readonly Dictionary<int, State> _states = [];
    private IReadOnlyList<BookViewModel> _library = [], _query = [];
    private BookViewModel[] _filtered = [];
    private string _librarySource = "尚未加载", _querySource = "尚未查询";
    private int _scope, _columns = 3;
    private bool _changing, _rebuildingItems;
    public event Action<BookViewModel?>? SelectionChanged;
    public event Action? DisplayChanging;
    public event Action? DisplayChanged;
    private readonly Dictionary<string, double> _positions = [];
    public string ViewKey => $"{_scope}/{Page}/{ListMode}";
    public void RememberPosition(string key, double offset)
    { if (!double.IsFinite(offset)) return; _positions.Remove(key); _positions[key] = Math.Clamp(offset, 0, 10000000); if (_positions.Count > 128) _positions.Remove(_positions.Keys.First()); }
    public double Position(string key) => _positions.GetValueOrDefault(key);
    public void CaptureViewport() => DisplayChanging?.Invoke();
    public ObservableCollection<BookViewModel> Books { get; } = [];
    public ObservableCollection<BookRow> Rows { get; } = [];
    public ObservableCollection<string> CompanyFilters { get; } = ["全部会社"];
    public IReadOnlyList<string> ShelfSorts { get; } = ["个人优先", "原始结果顺序", "添加时间", "发售时间", "所属会社", "上次打开", "标题"];
    public IReadOnlyList<string> ShelfStates { get; } = ["全部状态", "未记录", "未游玩", "曾玩过", "游玩中", "已游玩", "本次完成", "搁置", "放弃", "关联待核对"];
    public IReadOnlyList<string> ResourceFilters { get; } = ["全部本机资源", "本机已安装", "仅有安装包", "缺失／待核对", "可复核启动"];
    public IReadOnlyList<string> QuickViews { get; } = ["全部作品", "本机已安装", "仅有安装包", "缺失／待核对", "本机已有且未玩", "游玩中", "未记录", "自定义组合"];
    [ObservableProperty] private bool _favoritesOnly;
    [ObservableProperty] private string _labelFilter = "";
    public IReadOnlyList<Vndb.Core.PersonalLabel> LabelFilters { get; private set; } = [new("", "全部集合／标签", "")];
    public Vndb.Core.PersonalLabel? SelectedLabelFilter
    {
        get => LabelFilters.FirstOrDefault(l => l.Id == LabelFilter);
        set { LabelFilter = value?.Id ?? ""; OnPropertyChanged(); }
    }
    public void SetPersonalLabels(IReadOnlyList<Vndb.Core.PersonalLabel> labels)
    {
        LabelFilters = new[] { new Vndb.Core.PersonalLabel("", "全部集合／标签", "") }.Concat(labels).ToArray();
        if (!LabelFilters.Any(l => l.Id == LabelFilter)) LabelFilters = LabelFilters.Append(new(LabelFilter, "已移除的分类", "")).ToArray();
        OnPropertyChanged(nameof(LabelFilters)); OnPropertyChanged(nameof(SelectedLabelFilter));
    }
    partial void OnFavoritesOnlyChanged(bool value) => FilterChanged();
    partial void OnLabelFilterChanged(string value) { OnPropertyChanged(nameof(SelectedLabelFilter)); FilterChanged(); }
    [ObservableProperty] private string _resourceFilter = "全部本机资源";
    public string QuickView
    {
        get => ResourceFilter != "全部本机资源" && ShelfState == "全部状态" && !LocalOnly && QuickViews.Contains(ResourceFilter) ? ResourceFilter :
            LocalOnly && ShelfState == "未游玩" && ResourceFilter == "全部本机资源" ? "本机已有且未玩" :
            !LocalOnly && ResourceFilter == "全部本机资源" && ShelfState is "游玩中" or "未记录" ? ShelfState :
            !LocalOnly && ResourceFilter == "全部本机资源" && ShelfState == "全部状态" ? "全部作品" : "自定义组合";
        set
        {
            if (!QuickViews.Contains(value) || value == "自定义组合") return;
            _changing = true;
            try { ResourceFilter = ResourceFilters.Contains(value) ? value : "全部本机资源"; LocalOnly = value == "本机已有且未玩";
                ShelfState = value == "本机已有且未玩" ? "未游玩" : value is "游玩中" or "未记录" ? value : "全部状态"; Page = 1; }
            finally { _changing = false; }
            Refresh(null);
        }
    }
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _companyFilter = "全部会社";
    [ObservableProperty] private string _shelfState = "全部状态";
    [ObservableProperty] private string _shelfSort = "添加时间";
    [ObservableProperty] private bool _sortDescending = true;
    [ObservableProperty] private bool _localOnly;
    [ObservableProperty] private bool _listMode;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _totalPages = 1;
    [ObservableProperty] private string _pageLabel = "";
    [ObservableProperty] private string _source = "尚未加载";
    [ObservableProperty] private bool _filtersExpanded = true;
    private BookViewModel? _selectedBook;
    public BookViewModel? SelectedBook
    {
        get => _selectedBook;
        set
        {
            // ListBox clears its selection while its observable items are being replaced.
            // That transient null must not clear the shared installation/launch draft.
            if (_rebuildingItems) return;
            var previous = _selectedBook;
            if (!SetProperty(ref _selectedBook, value)) return;
            if (previous is not null) previous.Selected = false;
            OnPropertyChanged(nameof(HasSelection)); SelectionUpdated(value);
        }
    }
    [ObservableProperty] private bool _detailsOpen;
    public bool HasSelection => SelectedBook is not null;
    public bool IsQuery => _scope == 2;
    public bool IsHome => _scope == 0;
    public bool ShelfEmpty => Books.Count == 0;
    public int Columns => _columns;
    public string FilterPlaceholder => IsQuery ? "仅筛选本次返回的标题或会社，不重新联网" : "筛选书架中的标题或会社";
    public string FilterHeading => IsQuery ? "筛选本次结果（不重新联网）" : "搜索与筛选";
    public string FilterSummary => string.Join(" · ", new[] { !string.IsNullOrEmpty(Filter) ? "搜索：" + Filter : null,
        CompanyFilter != "全部会社" ? CompanyFilter : null, ShelfState != "全部状态" ? ShelfState : null, LocalOnly ? "本机有副本" : null,
        ResourceFilter != "全部本机资源" ? ResourceFilter : null, FavoritesOnly ? "仅收藏" : null, LabelFilter.Length > 0 ? SelectedLabelFilter?.Name ?? "分类" : null }.Where(v => v is not null));
    public bool HasFilters => FilterSummary.Length > 0;
    public IReadOnlyList<ShelfFilterChip> Conditions => new[]
    {
        new ShelfFilterChip("text", string.IsNullOrWhiteSpace(Filter) ? "" : "搜索：" + Filter),
        new("company", CompanyFilter == "全部会社" ? "" : CompanyFilter), new("progress", ShelfState == "全部状态" ? "" : ShelfState),
        new("favorite", FavoritesOnly ? "仅收藏" : ""), new("label", LabelFilter.Length > 0 ? SelectedLabelFilter?.Name ?? "分类" : ""),
        new("resource", ResourceFilter == "全部本机资源" ? "" : ResourceFilter), new("owned", LocalOnly ? "本机有副本" : "")
    }.Where(c => c.Label.Length > 0).ToArray();
    [RelayCommand] private void RemoveFilter(string key)
    {
        switch (key) { case "favorite": FavoritesOnly = false; break; case "label": LabelFilter = ""; break; case "text": Filter = ""; break; case "company": CompanyFilter = "全部会社"; break;
            case "progress": ShelfState = "全部状态"; break; case "resource": ResourceFilter = "全部本机资源"; break; case "owned": LocalOnly = false; break; }
    }

    private State Capture() => new(Filter, CompanyFilter, ShelfState, ShelfSort, SortDescending, LocalOnly,
        ListMode, Page, SelectedBook?.Id, FiltersExpanded, ResourceFilter, FavoritesOnly, LabelFilter);
    public void Activate(int section)
    {
        if (section is < 0 or > 2) return;
        if (section == _scope) { DetailsOpen = false; Refresh(); SelectionChanged?.Invoke(SelectedBook); return; }
        _states[_scope] = Capture(); _scope = section;
        Restore(_states.GetValueOrDefault(section) ?? new(Sort: section == 2 ? "原始结果顺序" : "添加时间", FiltersExpanded: section != 2));
        SelectionChanged?.Invoke(SelectedBook);
        OnPropertyChanged(nameof(IsQuery)); OnPropertyChanged(nameof(IsHome));
        OnPropertyChanged(nameof(FilterPlaceholder)); OnPropertyChanged(nameof(FilterHeading));
        SavePreferencesCommand.NotifyCanExecuteChanged();
    }
    private void Restore(State state)
    {
        _changing = true;
        try
        {
            Filter = state.Filter; CompanyFilter = state.Company; ShelfState = state.Status;
            ShelfSort = state.Sort; SortDescending = state.Descending; LocalOnly = state.LocalOnly;
            ListMode = state.List; Page = state.Page; ResourceFilter = state.Resource;
            FiltersExpanded = state.FiltersExpanded; DetailsOpen = false; FavoritesOnly = state.Favorites; LabelFilter = state.Label;
        }
        finally { _changing = false; }
        Refresh(state.SelectedId);
    }
    public void ApplyHomePreferences(ShelfPreferences value)
    {
        var status = value.State == "已通关" ? "本次完成" : value.State;
        var state = new State(Company: value.Company, Status: ShelfStates.Contains(status) ? status : "全部状态",
            Sort: ShelfSorts.Contains(value.Sort) ? value.Sort : "添加时间", Descending: value.Descending,
            LocalOnly: value.LocalOnly, List: value.ListMode, Resource: ResourceFilters.Contains(value.Resource) ? value.Resource : "全部本机资源", Favorites: value.FavoritesOnly, Label: Vndb.Core.PersonalOrganizationRules.ValidId(value.LabelId) ? value.LabelId : "");
        _states[0] = state; if (_scope == 0) Restore(state);
    }
    public void SetLibrary(IReadOnlyList<BookViewModel> values, string? source = null)
    { _library = values; if (source is not null) _librarySource = source; if (!IsQuery) Refresh(); }
    public void SetQuery(IReadOnlyList<BookViewModel> values, string source)
    {
        _query = values; _querySource = source;
        // A new search must not inherit narrow filters from the preceding result set.
        var previous = IsQuery ? Capture() : _states.GetValueOrDefault(2) ?? new(Sort: "原始结果顺序", FiltersExpanded: false);
        var next = previous with { Filter = "", Company = "全部会社", Status = "全部状态", LocalOnly = false, Resource = "全部本机资源", Favorites = false, Label = "", Page = 1, SelectedId = null };
        _states[2] = next; if (IsQuery) Restore(next);
    }
    public void Clear()
    {
        _states.Clear(); _library = []; _query = [];
        _librarySource = "尚未加载"; _querySource = "尚未查询";
        Restore(new(FiltersExpanded: !IsQuery));
        _positions.Clear();
    }
    public void Refresh() => Refresh(SelectedBook?.Id);
    private void Refresh(string? selectedId)
    {
        if (_changing) return;
        DisplayChanging?.Invoke();
        _changing = true;
        try
        {
            var source = IsQuery ? _query : _library;
            var company = CompanyFilter;
            CompanyFilters.Clear(); CompanyFilters.Add("全部会社");
            foreach (var name in source.SelectMany(b => b.Companies).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)) CompanyFilters.Add(name);
            if (!CompanyFilters.Contains(company)) CompanyFilters.Add(company);
            CompanyFilter = CompanyFilters.Contains(company) ? company : "全部会社";
            _filtered = Sort(source.Where(b => (_scope != 1 || b.HasPlayed) && (!LocalOnly || b.IsLocal))
                .Where(b => (!FavoritesOnly || b.IsFavorite) && (LabelFilter.Length == 0 || b.PersonalLabelIds.Contains(LabelFilter)))
                .Where(b => CompanyFilter == "全部会社" || b.Companies.Contains(CompanyFilter, StringComparer.OrdinalIgnoreCase))
                .Where(b => ResourceFilter switch { "本机已安装" => b.HasInstallation, "仅有安装包" => !b.HasInstallation && b.PackagePaths.Count > 0,
                    "缺失／待核对" => b.HasLocalProblem, "可复核启动" => b.CanQuickLaunch, _ => true })
                .Where(b => ShelfState switch { "全部状态" => true, "未游玩" => b.Progress.Matches("unplayed"), "未记录" => b.Progress.Matches("unrecorded"),
                    "曾玩过" => b.Progress.Matches("played"), "关联待核对" => b.Progress.Matches("review"),
                    _ => b.Progress.Matches(status: Vndb.Core.PersonalRules.States.FirstOrDefault(s => BookViewModel.Status(s) == ShelfState) ?? "invalid") })
                .Where(b => string.IsNullOrWhiteSpace(Filter) || (b.Title + "\n" + b.Company).Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase))).ToArray();
            TotalPages = Math.Max(1, (_filtered.Length + 59) / 60);
            var selected = _filtered.FirstOrDefault(b => b.Id == selectedId);
            if (selected is not null) Page = Array.IndexOf(_filtered, selected) / 60 + 1;
            Page = Math.Clamp(Page, 1, TotalPages);
            _rebuildingItems = true;
            try { Books.Clear(); foreach (var book in _filtered.Skip((Page - 1) * 60).Take(60)) Books.Add(book); }
            finally { _rebuildingItems = false; }
            SelectedBook = selected;
            OnPropertyChanged(nameof(SelectedBook));
            if (selected is null) DetailsOpen = false;
            Source = IsQuery ? _querySource : _librarySource;
            PageLabel = $"{_filtered.Length} 部 · 第 {Page} / {TotalPages} 页";
            RebuildRows();
        }
        finally { _changing = false; }
        OnPropertyChanged(nameof(ShelfEmpty)); OnPropertyChanged(nameof(FilterSummary)); OnPropertyChanged(nameof(HasFilters));
        OnPropertyChanged(nameof(QuickView)); OnPropertyChanged(nameof(Conditions)); DisplayChanged?.Invoke();
        PreviousCommand.NotifyCanExecuteChanged(); NextCommand.NotifyCanExecuteChanged();
    }
    private IEnumerable<BookViewModel> Sort(IEnumerable<BookViewModel> source)
    {
        if (ShelfSort == "原始结果顺序") return source;
        if (ShelfSort == "个人优先") return source.OrderBy(b => b.Progress.State == "played" ? 0 : b.IsLocal ? 1 : 2)
            .ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Id, StringComparer.Ordinal);
        string? Key(BookViewModel b) => ShelfSort switch
        {
            "添加时间" => b.AddedAt?.ToUniversalTime().ToString("O"),
            "发售时间" => b.Released is { Length: >= 4 } d && char.IsAsciiDigit(d[0]) ? d : null,
            "上次打开" => b.LastOpenedAt?.ToUniversalTime().ToString("O"),
            "所属会社" => b.GroupCompany, _ => b.Title
        };
        var known = source.OrderBy(b => string.IsNullOrEmpty(Key(b)));
        var sorted = SortDescending ? known.ThenByDescending(Key, StringComparer.OrdinalIgnoreCase) : known.ThenBy(Key, StringComparer.OrdinalIgnoreCase);
        return sorted.ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Id, StringComparer.Ordinal);
    }
    public void SetColumns(int count)
    { count = Math.Clamp(count, 1, 5); if (count == _columns) return; DisplayChanging?.Invoke(); _columns = count; RebuildRows(); DisplayChanged?.Invoke(); }
    private void RebuildRows() { Rows.Clear(); foreach (var row in Books.Chunk(_columns)) Rows.Add(new(row, _columns)); }
    private void FilterChanged() { if (_changing) return; Page = 1; Refresh(null); }
    partial void OnFilterChanged(string value) => FilterChanged();
    partial void OnCompanyFilterChanged(string value) => FilterChanged();
    partial void OnShelfStateChanged(string value) => FilterChanged();
    partial void OnShelfSortChanged(string value) => Refresh();
    partial void OnSortDescendingChanged(bool value) => Refresh();
    partial void OnLocalOnlyChanged(bool value) => FilterChanged();
    partial void OnResourceFilterChanged(string value) => FilterChanged();
    partial void OnListModeChanging(bool value) { if (!_changing) DisplayChanging?.Invoke(); }
    partial void OnListModeChanged(bool value) { if (!_changing) DisplayChanged?.Invoke(); }
    private void SelectionUpdated(BookViewModel? value)
    {
        if (value is not null) value.Selected = true;
        SelectionChanged?.Invoke(value); OpenDetailsCommand.NotifyCanExecuteChanged();
    }
    public void SelectIfVisible(BookViewModel? value)
    { if (value is null) SelectedBook = null; else if (Books.Any(b => ReferenceEquals(b, value))) SelectedBook = value; }
    [RelayCommand] private void Select(BookViewModel book) => SelectIfVisible(book);
    public void MoveSelection(int delta)
    {
        if (_filtered.Length == 0) return;
        var index = SelectedBook is null ? -1 : Array.IndexOf(_filtered, SelectedBook);
        var target = Math.Clamp(index < 0 ? 0 : index + delta, 0, _filtered.Length - 1);
        if (target / 60 + 1 == Page) SelectedBook = _filtered[target];
        else Refresh(_filtered[target].Id);
    }
    private bool CanPrevious() => Page > 1;
    private bool CanNext() => Page < TotalPages;
    [RelayCommand(CanExecute = nameof(CanPrevious))] private void Previous() => ChangePage(-1);
    [RelayCommand(CanExecute = nameof(CanNext))] private void Next() => ChangePage(1);
    private void ChangePage(int delta) { Page = Math.Clamp(Page + delta, 1, TotalPages); Refresh(null); }
    [RelayCommand] private void ToggleView() => ListMode = !ListMode;
    private bool CanOpenDetails() => HasSelection;
    [RelayCommand(CanExecute = nameof(CanOpenDetails))] private void OpenDetails() => DetailsOpen = true;
    [RelayCommand] private void ShowDetails(BookViewModel book)
    { if (!Books.Any(b => ReferenceEquals(b, book))) return; SelectedBook = book; DetailsOpen = true; }
    [RelayCommand] private void CloseDetails() => DetailsOpen = false;
    [RelayCommand] private void ResetFilters()
    {
        _changing = true;
        try { Filter = ""; CompanyFilter = "全部会社"; ShelfState = "全部状态"; LocalOnly = false; ResourceFilter = "全部本机资源"; FavoritesOnly = false; LabelFilter = ""; Page = 1; }
        finally { _changing = false; }
        Refresh(null);
    }
    private bool CanSavePreferences() => IsHome && savePreferences is not null;
    [RelayCommand(CanExecute = nameof(CanSavePreferences))]
    private Task SavePreferencesAsync() => savePreferences!(new(ShelfSort, SortDescending, CompanyFilter, ShelfState, LocalOnly, ListMode, ResourceFilter, FavoritesOnly, LabelFilter));
}

public sealed record BookRow(IReadOnlyList<BookViewModel> Books, int Columns);
public sealed record ShelfFilterChip(string Key, string Label);
