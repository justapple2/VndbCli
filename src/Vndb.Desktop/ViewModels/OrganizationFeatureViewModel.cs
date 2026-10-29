using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.Services;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

/// <summary>Organization presentation state. All filesystem decisions run in Local.</summary>
public partial class OrganizationFeatureViewModel(Func<LocalOrganizationWorkspace> workspace,
    Func<LocalInstallationView?> selection, Func<IReadOnlyList<LocalInstallationView>> installations,
    IDesktopDialogs dialogs, Func<Func<CancellationToken, Task>, Task> run, Action<string> status) : ObservableObject
{
    private LocalArchivePlan? _plan;
    private LocalRestorePlan? _restore;
    private long _context;
    private long _progressEpoch;
    [ObservableProperty] private string _sourceScope = "";
    [ObservableProperty] private string _targetRoot = "";
    [ObservableProperty] private string _company = "";
    [ObservableProperty] private string _japaneseTitle = "";
    [ObservableProperty] private string _preview = "仅预览不会移动文件。请明确选择本机完整安装。";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ExecuteCommand)), NotifyCanExecuteChangedFor(nameof(RestoreCommand))] private string _approval = "";
    [ObservableProperty] private string _planHash = "";
    [ObservableProperty] private string _runDirectory = "";
    [ObservableProperty] private DirectoryCandidate? _selectedCandidate;
    [ObservableProperty] private string _summary = "先选定完整安装与目录范围，再生成只读预览。";
    public string PreviewKind => _plan is not null ? "整理预览" : _restore is not null ? "复原预览" : "尚无有效预览";
    private bool CanExecutePlan() => _plan is not null && LocalArchiveFormat.IsHash(Approval);
    private bool CanRestorePlan() => _restore is not null && LocalArchiveFormat.IsHash(Approval);
    private void ProofChanged() { OnPropertyChanged(nameof(PreviewKind)); ExecuteCommand.NotifyCanExecuteChanged(); RestoreCommand.NotifyCanExecuteChanged(); }
    public ObservableCollection<DirectoryCandidate> Candidates { get; } = [];
    partial void OnSourceScopeChanged(string value) => Invalidate();
    partial void OnTargetRootChanged(string value) => Invalidate();
    partial void OnCompanyChanged(string value) => Invalidate();
    partial void OnJapaneseTitleChanged(string value) => Invalidate();
    partial void OnRunDirectoryChanged(string value) => Invalidate();
    public void Invalidate() { _context++; _plan = null; _restore = null; PlanHash = ""; Approval = ""; Summary = "条件已变化，请重新预览；旧批准已失效。"; ProofChanged(); }

    private Task RunAsync(Func<CancellationToken, Task> action) => run(async ct =>
    {
        _progressEpoch++;
        try { await action(ct); }
        finally { _progressEpoch++; }
    });
    [RelayCommand] private Task ScanAsync() => RunAsync(async ct =>
    {
        Invalidate();
        var scan = await workspace().ScanAsync(SourceScope, installations(), ct);
        Candidates.Clear(); foreach (var candidate in scan.Candidates) Candidates.Add(candidate);
        Preview = JsonSerializer.Serialize(scan, VndbJson.Options);
        Summary = $"只读扫描：{scan.Candidates.Count} 个候选、{scan.Issues.Count} 项提示。选择候选并登记后，再生成整理计划。";
        status($"只读扫描完成：{scan.Candidates.Count} 候选、{scan.Issues.Count} 项提示；选择候选后可登记本机关联。");
    });
    [RelayCommand] private Task PreviewAsync() => RunAsync(async ct =>
    {
        Invalidate(); var context = _context; var local = workspace();
        var installation = selection() ?? throw new ArgumentException("请先选择本机JSON已登记的完整安装。");
        var plan = await local.PreviewAsync(installation, SourceScope, TargetRoot, Company, JapaneseTitle, ct);
        if (context != _context) throw new InvalidOperationException("预览期间选择或条件已改变，请重新生成计划。");
        RunDirectory = local.RunDirectory(plan.Id);
        _plan = plan; PlanHash = LocalArchiveFormat.Hash(plan); Preview = JsonSerializer.Serialize(plan, VndbJson.Options);
        Summary = $"{plan.Company} / {plan.Title}\n\n来源：{plan.SourcePath}\n目标：{plan.DestinationPath}\n\n完整容器：{plan.Before.Files} 个文件 · {plan.Components.Count} 个组件\n依赖线索：{plan.Dependencies.Count} 项（请展开完整计划核对）"; ProofChanged();
        status("计划只读预览完成。核对来源、目标、全部组件与依赖线索，再输入SHA256批准。");
    });
    [RelayCommand(CanExecute = nameof(CanExecutePlan))] private Task ExecuteAsync() => RunAsync(async ct =>
    {
        var plan = _plan ?? throw new ArgumentException("请先生成计划。");
        var runner = workspace().Runner(Progress().Report); var approval = Approval; var directory = RunDirectory;
        Approval = "";
        var result = await Task.Run(() => runner.ExecuteAsync(plan, approval, directory, ct), ct);
        status("整理状态：" + result.State + "；启动配置路径如有改变须重新选择保存。");
        Preview = JsonSerializer.Serialize(result, VndbJson.Options);
        Summary = "整理状态：" + result.State + "。详细证据保存在本机回执中。";
    });
    [RelayCommand] private Task ResumeAsync() => RunAsync(async ct =>
    {
        var runner = workspace().Runner(Progress().Report); var directory = RunDirectory;
        var result = await Task.Run(() => runner.ResumeAsync(directory, ct), ct); status("续跑／回执核对：" + result.State);
    });
    [RelayCommand] private Task CancelPlanAsync() => RunAsync(async ct =>
    {
        var runner = workspace().Runner(); var directory = RunDirectory; var approval = Approval;
        var result = await Task.Run(() => runner.CancelAsync(directory, approval, ct), ct); status("撤销：" + result.State);
    });
    [RelayCommand] private Task PreviewRestoreAsync() => RunAsync(async ct =>
    {
        Invalidate(); var context = _context; var runner = workspace().Runner(); var directory = RunDirectory;
        var restore = await Task.Run(() => runner.PreviewRestoreAsync(directory, ct), ct);
        if (context != _context) throw new InvalidOperationException("预览期间选择或条件已改变，请重新预览复原。");
        _restore = restore; PlanHash = LocalArchiveFormat.Hash(restore); Preview = JsonSerializer.Serialize(restore, VndbJson.Options);
        Summary = $"复原来源：{restore.SourcePath}\n复原目标：{restore.DestinationPath}\n\n当前容器：{restore.Before.Files} 个文件（含整理后新增文件）。\n请核对完整复原计划后输入本次摘要。"; ProofChanged();
        status("复原预览完成，包含整理后新增文件。输入本次摘要后执行。");
    });
    [RelayCommand(CanExecute = nameof(CanRestorePlan))] private Task RestoreAsync() => RunAsync(async ct =>
    {
        var restore = _restore ?? throw new ArgumentException("请先预览复原。");
        var runner = workspace().Runner(Progress().Report); var approval = Approval; var directory = RunDirectory;
        Approval = "";
        var result = await Task.Run(() => runner.RestoreAsync(restore, approval, directory, ct), ct); status("复原：" + result.State);
    });
    [RelayCommand] private Task ChooseRunAsync() => RunAsync(async _ =>
    { if (await dialogs.FolderAsync("选择本机整理回执目录") is { } folder) RunDirectory = folder; });
    private IProgress<string> Progress()
    {
        var epoch = _progressEpoch;
        return new Progress<string>(stage =>
        {
            if (epoch != _progressEpoch) return;
            status(stage switch
            {
                "after_intent" => "本机计划意图已落盘。",
                "after_restore_intent" => "复原意图已落盘。",
                "before_rename" => "正在核对并直接移动完整安装…",
                "after_rename" => "目录已移动，正在保存文件证据。",
                "before_sync" => "正在核对本机位置与证据回执…",
                "after_commit" => "本机安装记录已更新，正在保存最终回执。",
                _ => "正在整理：" + stage
            });
        });
    }
}
