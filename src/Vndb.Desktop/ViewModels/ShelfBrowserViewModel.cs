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
        int Page = 1, string? SelectedId = null, bool FiltersExpanded = true);
    private readonly Dictionary<int, State> _states = [];
    private IReadOnlyList<BookViewModel> _library = [], _query = [];
    private BookViewModel[] _filtered = [];
    private string _librarySource = "尚未加载", _querySource = "尚未查询";
    private int _scope, _columns = 3;
    private bool _changing, _libraryLoaded, _rebuildingItems;
    public event Action<BookViewModel?>? SelectionChanged;
    public ObservableCollection<BookViewModel> Books { get; } = [];
    public ObservableCollection<BookRow> Rows { get; } = [];
    public ObservableCollection<string> CompanyFilters { get; } = ["全部会社"];
    public IReadOnlyList<string> ShelfSorts { get; } = ["个人优先", "原始结果顺序", "添加时间", "发售时间", "所属会社", "上次打开", "标题"];
    public IReadOnlyList<string> ShelfStates { get; } = ["全部状态", "未记录", "未游玩", "曾玩过", "游玩中", "已游玩", "本次完成", "搁置", "放弃", "关联待核对"];
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
        CompanyFilter != "全部会社" ? CompanyFilter : null, ShelfState != "全部状态" ? ShelfState : null, LocalOnly ? "本机有副本" : null }.Where(v => v is not null));
    public bool HasFilters => FilterSummary.Length > 0;

    private State Capture() => new(Filter, CompanyFilter, ShelfState, ShelfSort, SortDescending, LocalOnly,
        ListMode, Page, SelectedBook?.Id, FiltersExpanded);
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
            ListMode = state.List; Page = state.Page;
            FiltersExpanded = state.FiltersExpanded; DetailsOpen = false;
        }
        finally { _changing = false; }
        Refresh(state.SelectedId);
    }
    public void ApplyHomePreferences(ShelfPreferences value)
    {
        var status = value.State == "已通关" ? "本次完成" : value.State;
        var state = new State(Company: value.Company, Status: ShelfStates.Contains(status) ? status : "全部状态",
            Sort: ShelfSorts.Contains(value.Sort) ? value.Sort : "添加时间", Descending: value.Descending,
            LocalOnly: value.LocalOnly, List: value.ListMode);
        _states[0] = state; if (_scope == 0) Restore(state);
    }
    public void SetLibrary(IReadOnlyList<BookViewModel> values, string? source = null)
    { _library = values; _libraryLoaded = true; if (source is not null) _librarySource = source; if (!IsQuery) Refresh(); }
    public void SetQuery(IReadOnlyList<BookViewModel> values, string source)
    {
        _query = values; _querySource = source;
        // A new search must not inherit narrow filters from the preceding result set.
        var previous = IsQuery ? Capture() : _states.GetValueOrDefault(2) ?? new(Sort: "原始结果顺序", FiltersExpanded: false);
        var next = previous with { Filter = "", Company = "全部会社", Status = "全部状态", LocalOnly = false, Page = 1, SelectedId = null };
        _states[2] = next; if (IsQuery) Restore(next);
    }
    public void Clear()
    {
        _states.Clear(); _library = []; _query = []; _libraryLoaded = false;
        _librarySource = "尚未加载"; _querySource = "尚未查询";
        Restore(new(FiltersExpanded: !IsQuery));
    }
    public void Refresh() => Refresh(SelectedBook?.Id);
    private void Refresh(string? selectedId)
    {
        if (_changing) return;
        _changing = true;
        try
        {
            var source = IsQuery ? _query : _library;
            var company = CompanyFilter;
            CompanyFilters.Clear(); CompanyFilters.Add("全部会社");
            foreach (var name in source.SelectMany(b => b.Companies).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)) CompanyFilters.Add(name);
            if (!_libraryLoaded && !IsQuery && !CompanyFilters.Contains(company)) CompanyFilters.Add(company);
            CompanyFilter = CompanyFilters.Contains(company) ? company : "全部会社";
            _filtered = Sort(source.Where(b => (_scope != 1 || b.HasPlayed) && (!LocalOnly || b.IsLocal))
                .Where(b => CompanyFilter == "全部会社" || b.Companies.Contains(CompanyFilter))
                .Where(b => ShelfState switch { "全部状态" => true, "未游玩" => b.IsUnplayed, "未记录" => b.IsUnrecorded,
                    "曾玩过" => b.HasPlayed, "关联待核对" => b.NeedsPlayReview, _ => b.ShelfState == ShelfState })
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
    { count = Math.Clamp(count, 1, 5); if (count == _columns) return; _columns = count; RebuildRows(); }
    private void RebuildRows() { Rows.Clear(); foreach (var row in Books.Chunk(_columns)) Rows.Add(new(row, _columns)); }
    private void FilterChanged() { if (_changing) return; Page = 1; Refresh(); }
    partial void OnFilterChanged(string value) => FilterChanged();
    partial void OnCompanyFilterChanged(string value) => FilterChanged();
    partial void OnShelfStateChanged(string value) => FilterChanged();
    partial void OnShelfSortChanged(string value) => FilterChanged();
    partial void OnSortDescendingChanged(bool value) => FilterChanged();
    partial void OnLocalOnlyChanged(bool value) => FilterChanged();
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
        try { Filter = ""; CompanyFilter = "全部会社"; ShelfState = "全部状态"; LocalOnly = false; Page = 1; }
        finally { _changing = false; }
        Refresh();
    }
    private bool CanSavePreferences() => IsHome && savePreferences is not null;
    [RelayCommand(CanExecute = nameof(CanSavePreferences))]
    private Task SavePreferencesAsync() => savePreferences!(new(ShelfSort, SortDescending, CompanyFilter, ShelfState, LocalOnly, ListMode));
}

public sealed record BookRow(IReadOnlyList<BookViewModel> Books, int Columns);
