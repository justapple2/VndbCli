using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Core;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public sealed partial class RegistryCandidate(RegistryEntry entry) : ObservableObject
{
    public RegistryEntry Entry { get; } = entry;
    public string Label => $"{Entry.Location.Hive} ({Entry.Location.View}位)\\{Entry.Location.Key} [{Entry.Location.Name}]";
    public string Value => Entry.Value.Data;
    [ObservableProperty] private bool _selected;
}

public partial class MainViewModel
{
    public ObservableCollection<RegistryCandidate> RegistryCandidates { get; } = [];
    public ObservableCollection<string> RegistryRuns { get; } = [];
    public IReadOnlyList<int> RegistryViews { get; } = [32, 64];
    public IReadOnlyList<string> RegistryEncodings { get; } = ["自动 UTF-8／UTF-16", "日文 Shift-JIS", "简体中文 GBK"];
    [ObservableProperty] private string _registryEncoding = "自动 UTF-8／UTF-16";
    [ObservableProperty] private int _registryView = 32;
    [ObservableProperty] private bool _registryExpanded;
    [ObservableProperty] private string _registryTerm = "";
    [ObservableProperty] private string _registrySource = "";
    [ObservableProperty] private string _registryTarget = "";
    [ObservableProperty] private string _registryPreview = "选择作品后，明确输入旧目录和当前目录。先读取／查找，再勾选路径候选并预览。";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApplyRegistryCommand)), NotifyCanExecuteChangedFor(nameof(RestoreRegistryCommand))] private bool _registryApproved;
    [ObservableProperty] private string? _selectedRegistryRun;
    private RegistryRepairPlan? _registryPlan;
    private long _registryContext;
    [RelayCommand] private void RegistryBook(BookViewModel? book)
    {
        if (book?.Item is null || !book.HasInstallation) { Status = "仅安装包不能修复注册路径；请先登记并选择实际安装目录。"; return; }
        ConfigureBook(book); RegistryExpanded = true;
        Status = SelectedInstallation is null ? "请先在此页明确选择需要修复的安装目录，再输入旧目录查找。" : "已定位当前安装。输入迁移前旧目录，查找路径值、预览并备份后再确认修复。";
    }
    partial void OnRegistrySourceChanged(string value) => InvalidateRegistry();
    partial void OnRegistryTargetChanged(string value) => InvalidateRegistry();
    partial void OnRegistryTermChanged(string value) => InvalidateRegistry();
    public bool HasRegistryPreview => _registryPlan is not null || _registryRestore is not null;
    public string RegistryStep => _registryPlan is not null ? "已生成路径修复预览，核对后确认备份并应用。" : _registryRestore is not null ? "已生成原值恢复预览，核对后确认恢复。" : "尚无有效预览；先查找／读取，再选择路径生成预览。";
    private bool CanApplyRegistry() => RegistryApproved && _registryPlan is not null;
    private bool CanRestoreRegistry() => RegistryApproved && _registryRestore is not null && _registryRestore.Id == SelectedRegistryRun;
    private void RegistryProofChanged() { OnPropertyChanged(nameof(HasRegistryPreview)); OnPropertyChanged(nameof(RegistryStep)); ApplyRegistryCommand.NotifyCanExecuteChanged(); RestoreRegistryCommand.NotifyCanExecuteChanged(); }
    private void InvalidateRegistry() { _registryContext++; _registryPlan = null; _registryRestore = null; RegistryApproved = false; RegistryProofChanged(); }
    partial void OnRegistryViewChanged(int value) => ResetRegistryRead();
    partial void OnRegistryEncodingChanged(string value) => ResetRegistryRead();
    private void ResetRegistryRead() { InvalidateRegistry(); RegistryCandidates.Clear(); RegistryPreview = "读取条件已变化，请重新查找或读取REG文件。"; }
    private RegistryRepair RegistryService()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("注册表修复仅用于Windows本机。");
        return new(new WindowsGameRegistry(), Services().SettingsStore.DirectoryPath);
    }
    private void SetRegistryCandidates(RegistrySearch result)
    {
        InvalidateRegistry(); RegistryCandidates.Clear();
        foreach (var entry in result.Entries)
        {
            try
            {
                if (RegistryRepair.MapPath(entry.Value.Data, RegistrySource, RegistryTarget) is not null)
                {
                    var row = new RegistryCandidate(entry); row.PropertyChanged += (_, _) => InvalidateRegistry(); RegistryCandidates.Add(row);
                }
            }
            catch (ArgumentException) { }
        }
        RegistryPreview = $"找到 {RegistryCandidates.Count} 个与指定旧目录一致的路径字符串。未写入。\n" + string.Join("\n", result.Warnings);
    }
    [RelayCommand] private Task FindRegistryAsync() => GuardAsync(async ct =>
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        InvalidateRegistry(); var context = _registryContext;
        var term = RegistryTerm;
        var result = await Task.Run(() => OperatingSystem.IsWindows() ? new WindowsGameRegistry().Find(term, ct) : throw new PlatformNotSupportedException(), ct);
        if (context != _registryContext) throw new InvalidOperationException("读取期间注册信息条件已变化，请重新读取。");
        SetRegistryCandidates(result);
        Status = "只读查找完成，32／64位结果分开显示；只显示精确旧目录相关字符串。";
    });
    [RelayCommand] private Task ReadRegistryFileAsync() => GuardAsync(async ct =>
    {
        InvalidateRegistry(); var context = _registryContext; var encoding = RegistryEncoding; var view = RegistryView;
        if (await _dialogs.RegistryFileAsync() is not { } file) return;
        var result = RegistryRepair.ParseReg(await RegistryRepair.ReadTextAsync(file, encoding, ct), view);
        if (context != _registryContext) throw new InvalidOperationException("读取期间注册信息条件已变化，请重新读取。");
        SetRegistryCandidates(result);
        Status = "已读取REG文本；只支持厂商键的目录字符串，其余内容不会执行。";
    });
    [RelayCommand] private Task PreviewRegistryAsync() => GuardAsync(_ =>
    {
        InvalidateRegistry();
        _registryPlan = RegistryService().Preview(RegistryCandidates.Where(c => c.Selected).Select(c => c.Entry), RegistrySource, RegistryTarget);
        RegistryProofChanged();
        RegistryPreview = JsonSerializer.Serialize(_registryPlan, VndbJson.Options); Status = "修复预览已生成；请核对键、旧值、新值和位数后确认。"; return Task.CompletedTask;
    });
    [RelayCommand(CanExecute = nameof(CanApplyRegistry))] private Task ApplyRegistryAsync() => GuardAsync(async ct =>
    {
        if (!RegistryApproved || _registryPlan is null) throw new ArgumentException("请先预览并勾选确认。");
        var plan = _registryPlan; InvalidateRegistry();
        var service = RegistryService();
        try { var run = await Task.Run(() => service.ApplyAsync(plan, ct), ct); RegistryPreview = "已备份并核对路径值：" + run; Status = "注册路径已更新；是否可运行仍需用户自行验证。"; }
        finally { RefreshRegistryRuns(); SelectedRegistryRun = plan.Id; }
    });
    [RelayCommand] private void RefreshRegistryRuns()
    {
        RegistryRuns.Clear(); foreach (var id in RegistryService().Runs()) RegistryRuns.Add(id);
    }
    [RelayCommand] private Task PreviewRegistryRestoreAsync() => GuardAsync(async ct =>
    {
        InvalidateRegistry();
        var context = _registryContext;
        var plan = await RegistryService().ReadRunAsync(SelectedRegistryRun ?? throw new ArgumentException("请选择恢复记录。"), ct);
        if (context != _registryContext) throw new InvalidOperationException("读取期间恢复记录或条件已变化，请重新预览。");
        RegistryPreview = "将恢复原键值（其他程序已修改的值会停止处理）：\n" + JsonSerializer.Serialize(plan, VndbJson.Options);
        _registryRestore = plan;
        RegistryProofChanged();
    });
    private RegistryRepairPlan? _registryRestore;
    partial void OnSelectedRegistryRunChanged(string? value) => InvalidateRegistry();
    [RelayCommand(CanExecute = nameof(CanRestoreRegistry))] private Task RestoreRegistryAsync() => GuardAsync(async ct =>
    {
        if (!RegistryApproved || _registryRestore is null || _registryRestore.Id != SelectedRegistryRun) throw new ArgumentException("请先预览恢复并勾选确认。");
        var plan = _registryRestore; _registryRestore = null; RegistryApproved = false; RegistryProofChanged();
        await Task.Run(() => RegistryService().RestoreAsync(plan, ct), ct); Status = "原注册路径已恢复并核对。";
    });
}
