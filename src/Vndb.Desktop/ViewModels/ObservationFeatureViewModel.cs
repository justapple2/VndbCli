using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Desktop.Services;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public sealed record ObservationRootRow(ObservationRoot Root)
{
    public string Label => (Root.Kind == "games" ? "游戏根" : "安装包根") + $" · {Root.Depth} 层 · " + Root.Path;
}
public sealed record ObservationCandidateRow(ObservationCandidate Candidate, ObservationRoot Root)
{
    public string Label => (Candidate.Kind == "directory" ? "游戏目录候选 · " : "安装包候选 · ") + Candidate.Label;
}

public sealed partial class ObservationFeatureViewModel(Func<DesktopServices> services, IDesktopDialogs dialogs,
    Func<Func<CancellationToken, Task>, Task> run, Action<ObservationCandidateRow> useCandidate) : ObservableObject
{
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _approved;
    [ObservableProperty] private string _rootPath = "";
    [ObservableProperty] private string _kind = "游戏根";
    [ObservableProperty] private int _depth = 2;
    [ObservableProperty] private IReadOnlyList<ObservationRootRow> _roots = [];
    [ObservableProperty] private ObservationRootRow? _selectedRoot;
    [ObservableProperty] private IReadOnlyList<ObservationCandidateRow> _candidates = [];
    [ObservableProperty] private ObservationCandidateRow? _selectedCandidate;
    [ObservableProperty] private string _summary = "观测默认关闭；明确添加管理根后启用。";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ResourceSummary))] private IReadOnlyList<string> _resourceMessages = [];
    public string ResourceSummary => string.Join('\n', ResourceMessages);
    public IReadOnlyList<string> Kinds { get; } = ["游戏根", "安装包根"];
    private ObservationSettings _basis = new();
    private ObservationResult? _shown;
    public void Load(ObservationSettings settings)
    { _basis = settings; _shown = null; Candidates = []; SelectedCandidate = null; ResourceMessages = []; Enabled = settings.Enabled; Roots = (settings.Roots ?? []).Select(r => new ObservationRootRow(r)).ToArray(); SelectedRoot = null; Approved = false; }
    public void Poll(ObservationResult? result, string status)
    {
        Summary = status + (result is null ? "" : $"\n最近核对：{result.CheckedAt.ToLocalTime():g} · {result.Metrics.Probes}次元数据检查 · 游戏内容哈希0字节");
        if (result is null || ReferenceEquals(result, _shown)) return; _shown = result;
        var roots = (_basis.Roots ?? []).ToDictionary(r => r.Id);
        Candidates = result.Snapshot.Candidates.Where(c => c.State is "checking" or "pending" && roots.ContainsKey(c.RootId)).Take(100)
            .Select(c => new ObservationCandidateRow(c, roots[c.RootId])).ToArray();
        ResourceMessages = result.Snapshot.Installations.Where(i => roots.ContainsKey(i.RootId) && i.Reason != "present").Take(100).Select(i =>
            (result.Catalog?.Installations.FirstOrDefault(v => v.Installation.Id == i.Id)?.Installation.Details.Title ?? i.Id) + "：" + ObservationText.Reason(i.Reason)).ToArray();
    }
    [RelayCommand] private Task ChooseRootAsync() => run(async _ => { if (await dialogs.FolderAsync("选择只读观测管理根（游戏根／安装包根）") is { } path) RootPath = path; });
    [RelayCommand] private Task AddRootAsync() => run(async ct =>
    {
        var root = await Task.Run(() => services().ObservationSettingsStore.Capture(RootPath, Kind == "游戏根" ? "games" : "packages", Depth), ct);
        Roots = Roots.Append(new ObservationRootRow(root)).ToArray(); Approved = false;
    });
    [RelayCommand] private void RemoveRoot() { if (SelectedRoot is { } row) { Roots = Roots.Where(r => r.Root.Id != row.Root.Id).ToArray(); SelectedRoot = null; Approved = false; } }
    [RelayCommand] private Task SaveAsync() => run(async ct =>
    {
        if (!Approved) throw new ArgumentException("请先核对管理根并确认只读观测范围。");
        var value = _basis with { Enabled = Enabled, Roots = Roots.Select(r => r.Root).ToArray() };
        await services().ConfigureObservationAsync(value, ct); Load(value); Summary = Enabled ? "已启用后台浅层观测；候选仍需人工匹配。" : "监控已关闭；保留登记和观测快照，可手动核对。";
    });
    [RelayCommand] private Task CheckAsync() => run(async ct =>
    {
        var service = services(); var result = await service.CheckObservationAsync(ct);
        Poll(result, service.ObservationHost?.Status ?? "手动核对完成；监控未启用。");
    });
    [RelayCommand] private void UseCandidate()
    {
        if (SelectedCandidate is not { Candidate.State: "pending" } row) { Summary = "请选择已稳定的待匹配候选；文件名不证明准确作品或安装完整。"; return; }
        useCandidate(row);
    }
    [RelayCommand] private Task IgnoreAsync() => run(async ct =>
    {
        var service = services(); var id = SelectedCandidate?.Candidate.Id ?? throw new ArgumentException("请先选择候选。");
        await new LocalObservation(service.LocalLibrary, service.Identity?.Id ?? throw new InvalidOperationException("本机身份未加载。")).IgnoreAsync(id, ct);
        var result = await service.CheckObservationAsync(ct); Poll(result, service.ObservationHost?.Status ?? "候选已忽略；未删除目录或文件。");
    });
}
