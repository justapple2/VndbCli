using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public sealed partial class PersonalLabelToggle(PersonalLabel label, bool selected) : ObservableObject
{
    public PersonalLabel Label { get; } = label;
    [ObservableProperty] private bool _selected = selected;
}

public sealed partial class PersonalOrganizationViewModel(Func<PersonalOrganizationStore> store, Func<ISyncApi?> remote,
    Func<IReadOnlyList<BookViewModel>> page, Action<PersonalOrganizationState> changed, Action sync) : ObservableObject
{
    private PersonalOrganizationState _state = PersonalOrganizationRules.Empty;
    private BookViewModel? _book;
    private long _generation;
    private string? _loadedScope;
    private PersonalOrganizationChange? _batch;
    private string? _batchPage;
    public IReadOnlyList<string> Kinds { get; } = ["collection", "tag"];
    [ObservableProperty] private IReadOnlyList<PersonalLabel> _labels = [];
    [ObservableProperty] private IReadOnlyList<PersonalLabelToggle> _memberLabels = [];
    [ObservableProperty] private PersonalLabel? _selectedLabel;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _kind = "collection";
    [ObservableProperty] private bool _favorite;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _message = "收藏、集合与标签不改变物理目录或游玩状态。";
    [ObservableProperty] private string _batchPreview = "";
    [ObservableProperty] private bool _conflict;
    [ObservableProperty] private IReadOnlyList<FieldComparisonDisplay> _differences = [];
    [ObservableProperty] private string _comparisonSummary = "只读当前队首原操作；不会合并、撤回或重建操作ID。";
    [ObservableProperty] private string _outcome = "尚无待同步操作";
    public string SelectedTitle => _book?.Title ?? "请先选择个人书架作品";
    public bool CanEditMember => _book?.Item is not null;
    partial void OnSelectedLabelChanged(PersonalLabel? value) { if (value is not null) { Name = value.Name; Kind = value.Kind; } _batch = null; BatchPreview = ""; }
    public void Select(BookViewModel? book)
    {
        _book = book; OnPropertyChanged(nameof(SelectedTitle)); OnPropertyChanged(nameof(CanEditMember));
        var member = _state.Items.SingleOrDefault(i => i.ItemId == book?.Item?.Item.Id); Favorite = member?.Favorite == true;
        MemberLabels = _state.Labels.Select(l => new PersonalLabelToggle(l, member?.LabelIds.Contains(l.Id) == true)).ToArray();
    }
    public async Task LoadAsync(bool refresh, CancellationToken ct)
    {
        var service = store();
        if (_loadedScope != service.ScopeKey) Invalidate();
        var generation = ++_generation; var doc = await service.ReadAsync(ct);
        if (refresh && remote() is { } web)
            try { doc = await service.RefreshAsync(web, ct: ct); }
            catch (Exception e) when (e is ClientApiException or CatalogUnavailableException or HttpRequestException) { Message = "联网未完成，保留本机分类与原稿。"; }
        if (generation == _generation && service.ScopeKey == store().ScopeKey) Apply(doc, service.ScopeKey);
    }
    public void Invalidate()
    {
        _generation++; _loadedScope = null; _state = PersonalOrganizationRules.Empty;
        Labels = []; SelectedLabel = null; Name = ""; Kind = "collection"; _batch = null; _batchPage = null; BatchPreview = "";
        Conflict = false; Differences = []; Outcome = "尚未读取当前服务分类"; ComparisonSummary = "请先读取当前服务分类；原作用域原稿保留。";
        Select(_book); changed(_state); Message = "连接或资料作用域已改变，请先读取个人分类。";
    }
    private void Apply(PersonalOrganizationDocument doc, string scope)
    {
        _state = PersonalOrganizationStore.View(doc); _loadedScope = scope; Conflict = doc.Conflict;
        var id = SelectedLabel?.Id; Labels = _state.Labels; SelectedLabel = Labels.SingleOrDefault(l => l.Id == id);
        Select(_book); changed(_state); _batch = null; BatchPreview = "";
        Message = doc.Conflict ? "分类修订冲突；原稿保留。可保留原稿并读取Web，再重新编辑。" : doc.Pending.Count > 0 ? $"{doc.Pending.Count} 条分类修改待同步；离线仍可筛选。" : "分类已读取；不改变文件位置与游玩状态。";
        Outcome = doc.Conflict ? "明确冲突／拒绝" : doc.Pending.Count == 0 ? "已确认快照" : doc.Attempted ? "结果未确认（按原ID重试）" : "待同步";
        Differences = doc.Pending.Count == 0 ? [] : FieldComparisonDisplay.Rows(PersonalComparison.Organization(doc.Confirmed, null, doc.Pending[0].Change));
        ComparisonSummary = doc.Pending.Count > 0 ? $"队首原操作 {doc.Pending[0].Id}，基于修订 {doc.Pending[0].Change.ExpectedRevision}；后续 {doc.Pending.Count - 1} 条保持原顺序。" : "没有待同步分类原稿。";
    }
    private async Task QueueAsync(PersonalOrganizationChange change, CancellationToken ct)
    {
        var service = store();
        if (_loadedScope != service.ScopeKey) throw new InvalidOperationException("请先读取当前服务的个人分类，再编辑；原作用域原稿保留。");
        var generation = ++_generation; await service.EnqueueAsync(change, ct); var doc = await service.ReadAsync(ct);
        if (generation == _generation && service.ScopeKey == store().ScopeKey) { Apply(doc, service.ScopeKey); sync(); }
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (Busy) return; Busy = true;
        try { await action(CancellationToken.None); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or ClientApiException or CatalogUnavailableException or UnauthorizedAccessException or System.Text.Json.JsonException) { Message = e.Message; }
        finally { Busy = false; }
    }
    [RelayCommand] private Task ReloadAsync() => RunAsync(ct => LoadAsync(true, ct));
    [RelayCommand] private Task SyncAsync() => RunAsync(async ct =>
    {
        var web = remote() ?? throw new InvalidOperationException("尚未连接Web；原稿保留。");
        await store().SyncAsync(web, ct); await LoadAsync(true, ct);
    });
    [RelayCommand] private Task ResolveConflictAsync() => RunAsync(async ct =>
    {
        var web = remote() ?? throw new InvalidOperationException("请先连接Web。");
        var generation = ++_generation; var service = store(); var doc = await service.RefreshAsync(web, true, ct);
        if (generation == _generation && service.ScopeKey == store().ScopeKey) Apply(doc, service.ScopeKey);
    });
    [RelayCommand] private Task ReadComparisonAsync() => RunAsync(async ct =>
    {
        var generation = ++_generation; var service = store(); var inspection = await service.InspectAsync(remote(), true, ct);
        if (generation != _generation || service.ScopeKey != store().ScopeKey) return;
        if (inspection.ReadFailed) { ComparisonSummary = "Web只读对照未完成；保留原稿和此前已显示对照，不改变同步状态。"; return; }
        Differences = FieldComparisonDisplay.Rows(inspection.Differences);
        ComparisonSummary = inspection.Document.Pending.Count == 0 ? "没有待同步分类原稿。" : $"队首原操作基于修订 {inspection.Document.Pending[0].Change.ExpectedRevision}；Web修订 {inspection.Current?.Revision}，观测 {inspection.ObservedAt?.ToLocalTime():g}。";
    });
    [RelayCommand] private Task CreateLabelAsync() => RunAsync(ct => QueueAsync(new(_state.Revision, new(Guid.NewGuid().ToString("D"), Name.Trim(), Kind)), ct));
    [RelayCommand] private Task RenameLabelAsync() => RunAsync(ct => QueueAsync(new(_state.Revision, (SelectedLabel ?? throw new ArgumentException("请选择分类。")) with { Name = Name.Trim() }), ct));
    [RelayCommand] private Task RemoveLabelAsync() => RunAsync(ct => QueueAsync(new(_state.Revision, RemoveLabel: SelectedLabel?.Id ?? throw new ArgumentException("请选择分类。")), ct));
    [RelayCommand] private Task SaveMemberAsync() => RunAsync(ct => QueueAsync(new(_state.Revision, Items:
        [new(_book?.Item?.Item.Id ?? throw new ArgumentException("仅支持准确个人条目。"), Favorite, MemberLabels.Where(l => l.Selected).Select(l => l.Label.Id).ToArray())]), ct));
    [RelayCommand] private Task ToggleFavoriteAsync(BookViewModel book) => RunAsync(ct =>
    {
        var id = book.Item?.Item.Id ?? throw new ArgumentException("请先加入个人书架并选择准确版本。");
        var old = _state.Items.SingleOrDefault(i => i.ItemId == id);
        return QueueAsync(new(_state.Revision, Items: [new(id, old?.Favorite != true, old?.LabelIds ?? [])]), ct);
    });
    [RelayCommand] private void PreviewPage()
    {
        if (_loadedScope != store().ScopeKey) { Message = "请先读取当前服务的个人分类。"; return; }
        var label = SelectedLabel; if (label is null) { Message = "请选择要归入的集合或标签。"; return; }
        var rows = page().Where(b => b.Item is not null).Select(b =>
        {
            var id = b.Item!.Item.Id; var old = _state.Items.SingleOrDefault(i => i.ItemId == id);
            return new PersonalMembership(id, old?.Favorite == true, (old?.LabelIds ?? []).Append(label.Id).Distinct().ToArray());
        }).ToArray();
        _batchPage = string.Join("|", page().Select(b => b.Id));
        _batch = rows.Length == 0 ? null : new(_state.Revision, Items: rows);
        BatchPreview = $"将本页 {rows.Length} 个准确个人条目归入「{label.Name}」；保留各项原收藏与其他分类，不移动文件。";
    }
    [RelayCommand] private Task ApplyPageAsync() => RunAsync(ct =>
    {
        if (_batchPage != string.Join("|", page().Select(b => b.Id))) throw new InvalidOperationException("当前页已改变，请重新预览归类。");
        return QueueAsync(_batch ?? throw new ArgumentException("请先预览本页归类。"), ct);
    });
}
