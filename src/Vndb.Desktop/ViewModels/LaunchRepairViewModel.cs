using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public sealed record LaunchRepairContext(LocalInstallationView? Installation, string ComponentId, string ComponentTitle, LaunchProfile? Profile, bool DraftChanged, string Source);

public sealed partial class LaunchRepairViewModel(
    Func<LaunchRepairContext> selection, Func<LocalOrganizationWorkspace> workspaceFactory, Func<LaunchRepair> repairFactory, LocalLauncher launcher,
    Func<Func<CancellationToken, Task>, Task> run, Action<string?, string> setPaths,
    Func<LaunchProfile, CancellationToken, Task> profileUpdated, Action<LocalArchivePlan> presentPlan, Action<string> status) : ObservableObject
{
    private const string RepairPrompt = "选择本机安装和组件后，检查实际启动程序、路径和引擎。";
    [ObservableProperty] private string _summary = RepairPrompt;
    [ObservableProperty] private string _engineAlias = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApplyCommand))] private bool _approved;
    private LaunchDiagnosis? _diagnosis;
    private LaunchProfileRepair? _profileRepair;
    private InstallPathRepair? _configRepair;
    private InstallRepairRestore? _configRestore;
    private bool _restoreConfig;
    private long _repairContext;

    public bool HasPreview => _profileRepair is not null || _configRepair is not null;
    public bool HasAdvGameDiagnosis => _diagnosis?.Engine == GameEngine.AdvGame;
    public bool HasAdvHdDiagnosis => _diagnosis?.Engine == GameEngine.AdvHd;
    private bool CanApply() => HasPreview && Approved;

    private void RepairStateChanged()
    {
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(HasAdvGameDiagnosis));
        OnPropertyChanged(nameof(HasAdvHdDiagnosis));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private void ClearPlan()
    {
        _profileRepair = null;
        _configRepair = null;
        _configRestore = null;
        _restoreConfig = false;
        Approved = false;
        RepairStateChanged();
    }

    public void Invalidate()
    {
        _repairContext++;
        ClearPlan();
        _diagnosis = null;
        Summary = RepairPrompt;
        NextStep = "先检查当前安装；所有修复均需预览并确认。";
        OnPropertyChanged(nameof(Context));
        RepairStateChanged();
    }

    public string Context => selection() is { Installation: { } i } c ? $"{i.Installation.Details.Title} · {c.ComponentTitle}" : "尚未选择本机安装";
    [ObservableProperty] private string _nextStep = "先检查当前安装；所有修复均需预览并确认。";

    private (LocalInstallationView Installation, string Root, LaunchProfile? Profile) RepairSelection()
    {
        var chosen = selection();
        var installation = chosen.Installation ?? throw new ArgumentException("请先选择本机安装。");
        var root = LocalFiles.Full(installation.Installation.Details.RootPath);
        var component = installation.Components.SingleOrDefault(c => c.Id == chosen.ComponentId);
        if (installation.Components.Count > 1 && component is null) throw new ArgumentException("合集或多个版本请先选择具体组件。");
        if (component is null && installation.Components.Count == 1) component = installation.Components[0];
        var selected = chosen.Profile;
        if (selected is not null && (selected.InstallationId != installation.Installation.Id ||
            (selected.ComponentId is not null && selected.ComponentId != component?.Id))) selected = null;
        var componentRoot = component is null || component.Details.RelativePath == "." ? root :
            LocalFiles.Full(Path.Combine(root, component.Details.RelativePath));
        if (!LocalFiles.Same(root, componentRoot) && !LocalFiles.Within(root, componentRoot)) throw new InvalidDataException("组件目录超出已登记的安装范围。");
        return (installation, componentRoot, selected);
    }

    [RelayCommand] private Task InspectAsync() => run(async ct =>
    {
        var (installation, root, profile) = RepairSelection();
        Invalidate();
        var context = _repairContext;
        var workspace = workspaceFactory();
        var previous = await Task.Run(() => workspace.PreviousRootAsync(installation.Installation, ct), ct);
        var mapped = profile is not null && previous is not null ? LocalLauncher.PreviewPathRepair(profile, previous, installation.Installation.Details.RootPath)?.After : null;
        var diagnosis = await Task.Run(() => LaunchRepair.Inspect(root, mapped ?? profile), ct);
        if (context != _repairContext) return;
        setPaths(previous, installation.Installation.Details.RootPath);
        _diagnosis = diagnosis;
        var engine = diagnosis.Engine switch { GameEngine.AdvGame => "AdvGame Module", GameEngine.AdvHd => "AdvHD", _ => "未知或证据冲突" };
        Summary = $"安装：{installation.Installation.Details.Title} ({installation.Installation.Id})\n组件：{selection().ComponentTitle}\n引擎：{engine}\n组件目录：{diagnosis.Root}\n" +
            (mapped is null ? "" : "已按核验的移动回执定位实际游戏程序；启动配置尚未修改。\n") +
            (previous is null ? "未找到对应的已完成移动回执，可手动填写旧位置。" : "已核验归档回执中的旧目录。") +
            (diagnosis.Findings.Count == 0 ? "\n暂未发现明显路径问题。" : "\n" + string.Join("\n", diagnosis.Findings));
        RepairStateChanged();
        NextStep = diagnosis.Engine switch { GameEngine.AdvGame => "预览 install.inf 配置修复；需要回退时选择恢复备份。", GameEngine.AdvHd => "先核对路径编码及工作目录；需要短目录时进入整理预览。", _ => "核对已保存启动入口；未知引擎请手动处理配置，或展开厂商注册表路径。" };
        status("诊断已完成，尚未修改文件。");
    });

    [RelayCommand] private Task PreviewPathAsync() => run(_ =>
    {
        var (installation, _, profile) = RepairSelection();
        _repairContext++; ClearPlan();
        if (profile is null || selection().DraftChanged) throw new ArgumentException("请选择对应的已保存启动配置，并先处理未保存的编辑。");
        _profileRepair = LocalLauncher.PreviewPathRepair(profile, selection().Source, installation.Installation.Details.RootPath);
        Summary = _profileRepair is { } plan ? DescribeProfileRepair(plan) : "当前配置没有可映射的旧路径。";
        NextStep = HasPreview ? "预览已生成；核对路径并确认游戏已退出后应用。" : "如需修改入口，请回到启动配置手动编辑。";
        RepairStateChanged();
        return Task.CompletedTask;
    });

    [RelayCommand] private Task PreviewEngineConfigRepairAsync() => PreviewEngineConfigAsync(false);
    [RelayCommand] private Task PreviewEngineConfigRestoreAsync() => PreviewEngineConfigAsync(true);

    private Task PreviewEngineConfigAsync(bool restore) => run(async ct =>
    {
        var (installation, root, profile) = RepairSelection();
        var service = repairFactory();
        _repairContext++; ClearPlan();
        var context = _repairContext;
        var resolved = restore ? await workspaceFactory().PreviewInstallRestoreAsync(installation.Installation.Id, root, ct) : null;
        var plan = restore ? resolved?.Repair : await Task.Run(() => service.PreviewInstall(installation.Installation.Id, root, profile?.TrackedExecutable ?? profile?.Executable), ct);
        if (context != _repairContext) return;
        _configRepair = plan;
        _configRestore = resolved;
        _restoreConfig = restore;
        NextStep = plan is null ? "没有待应用的配置变更。" : "预览已生成；核对后明确应用。";
        Summary = plan is null ? (restore ? "此安装没有本机修复备份。" : "install.inf 的 Current 已指向实际目录。") :
            $"{(restore ? "恢复" : "修复")} {Path.Combine(root, "install.inf")}\n{(restore ? plan.NewPath : plan.OldPath)} → {(restore ? plan.OldPath : plan.NewPath)}" +
            (plan.MirrorsMediaPath ? "\n同时更新原本与 Current 相同的 CDDrive。" : "\n独立的 CDDrive 值保留。");
        RepairStateChanged();
    });

    [RelayCommand(CanExecute = nameof(CanApply))] private Task ApplyAsync() => run(async ct =>
    {
        if (!CanApply()) throw new InvalidOperationException("请先核对并确认当前预览。");
        var profile = _profileRepair;
        var config = _configRepair;
        var resolved = _configRestore;
        var restore = _restoreConfig;
        var beforeSelection = selection();
        if (profile is not null && selection().DraftChanged) throw new InvalidOperationException("请先处理未保存的启动配置编辑。");
        var context = _repairContext;
        ClearPlan();
        if (profile is not null)
        {
            var updated = await launcher.ApplyPathRepairAsync(profile, ct);
            await profileUpdated(updated, ct);
        }
        else if (config is not null)
        {
            var service = repairFactory();
            if (restore) await workspaceFactory().RestoreInstallAsync(resolved ?? throw new InvalidOperationException("请重新预览恢复目标。"), ct);
            else await service.ApplyInstallAsync(config, ct);
        }
        var current = selection();
        var sameSelection = beforeSelection.Installation?.Installation.Id == current.Installation?.Installation.Id &&
            beforeSelection.Installation?.Installation.Details.RootPath == current.Installation?.Installation.Details.RootPath &&
            beforeSelection.ComponentId == current.ComponentId && beforeSelection.Profile?.Id == current.Profile?.Id && !current.DraftChanged;
        if (context != _repairContext && (profile is null || !sameSelection)) { status("已应用并保存本机记录；当前选择已变化，请重新检查。"); return; }
        Summary = restore ? "已应用：原配置已恢复并核对。" : "已应用：路径已更新并核对，可手动启动验证。";
        NextStep = "修改已保存到本机，可使用已保存的启动方式手动验证。";
        status(Summary);
    });

    [RelayCommand] private Task PreviewAdvHdAliasAsync() => run(async ct =>
    {
        var (installation, root, profile) = RepairSelection();
        var context = _repairContext;
        var name = EngineAlias;
        var plan = await workspaceFactory().PreviewEngineAliasAsync(installation, root, profile?.TrackedExecutable ?? profile?.Executable, name, ct);
        if (context != _repairContext || name != EngineAlias) return;
        presentPlan(plan);
    });

    private static string DescribeProfileRepair(LaunchProfileRepair plan) =>
        $"启动程序：{plan.Before.Executable} → {plan.After.Executable}\n" +
        $"工作目录：{plan.Before.WorkingDirectory} → {plan.After.WorkingDirectory}\n" +
        $"实际游戏程序：{plan.Before.TrackedExecutable ?? "未设置"} → {plan.After.TrackedExecutable ?? "未设置"}\n" +
        $"参数：{LaunchArguments.Format(plan.Before.Arguments)} → {LaunchArguments.Format(plan.After.Arguments)}";
}
