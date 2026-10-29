using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Local;
using Vndb.Desktop.Services;

namespace Vndb.Desktop.ViewModels;

/// <summary>Filename suggestions and explicit matching drafts; Local owns files and persistence.</summary>
public sealed partial class PackagesFeatureViewModel(Func<OwnedPackages> service, Func<string?> origin,
    Func<Func<CancellationToken, Task>, Task> run, Action<string> status, Action<string> query,
    Func<CancellationToken, Task> saved, IDesktopDialogs? dialogs = null) : ObservableObject
{
    private RegistrationSelection? _choice;
    private PackageRegistrationPreview? _preview;
    private long _context;
    [ObservableProperty] private string _scanRoot = "";
    [ObservableProperty] private IReadOnlyList<PackageCandidate> _candidates = [];
    [ObservableProperty] private PackageCandidate? _selected;
    [ObservableProperty] private string _matchLabel = "尚未选择作品；从查询结果或书架的菜单选择‘登记安装包’。";
    [ObservableProperty] private string _summary = "仅扫描文件名，不解压。分卷只列首包，不验证文件完整性；补丁、特典与本篇须分别核对。";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ConfirmCommand))] private bool _approved;
    partial void OnScanRootChanged(string value) { Invalidate(); Candidates = []; Selected = null; }
    partial void OnSelectedChanged(PackageCandidate? value) { _choice = null; MatchLabel = "尚未选择作品；从查询结果或书架的菜单选择‘登记安装包’。"; Invalidate(); }
    private void Invalidate() { _context++; _preview = null; Approved = false; ConfirmCommand.NotifyCanExecuteChanged(); Summary = "条件已变化，请重新预览。"; }
    public void Select(RegistrationSelection choice)
    {
        Invalidate(); _choice = choice; MatchLabel = choice.Title + " · " + (choice.Item?.Item.Id ?? choice.Metadata?.VndbId);
    }
    public void Clear() { Invalidate(); _choice = null; MatchLabel = "连接已变化，请重新选择作品。"; }
    [RelayCommand] private Task ChooseRootAsync() => run(async _ =>
    {
        if (dialogs is not null && await dialogs.FolderAsync("选择安装包检索根（只读扫描）") is { } path) ScanRoot = path;
    });
    [RelayCommand] private Task ScanAsync() => run(async ct =>
    {
        Invalidate(); var context = _context; var root = ScanRoot;
        var scan = await Task.Run(() => OwnedPackages.Scan(root, ct: ct), ct);
        if (context != _context) return;
        Candidates = scan.Candidates; Selected = null;
        Summary = $"发现 {scan.Candidates.Count} 个文件名候选，{scan.Issues.Count} 项跳过提示。选中一个后查询／选择作品，再预览登记。";
        status("安装包扫描完成；没有自动建立作品关联或解压文件。");
    });
    [RelayCommand] private void QuerySelected()
    {
        if (Selected is not { } candidate) { status("请先扫描并选择安装包。"); return; }
        query(candidate.QueryTitle); status("已用文件名填写查询；可修改标题后点击查询，再选择具体作品并登记安装包。");
    }
    [RelayCommand] private Task PreviewAsync() => run(async ct =>
    {
        var candidate = Selected ?? throw new ArgumentException("请先选择扫描候选。");
        var choice = _choice ?? throw new ArgumentException("请从查询结果或书架菜单选择‘登记安装包’，明确关联作品。");
        Invalidate(); var context = _context;
        var source = service(); var server = origin();
        var preview = await Task.Run(() => source.PreviewAsync(candidate.ScanRoot, candidate.RelativePath, choice, server, ct), ct);
        if (context != _context) return;
        _preview = preview; ConfirmCommand.NotifyCanExecuteChanged();
        Summary = $"作品：{choice.Title}\n检索根（仅本机）：{preview.Package.ScanRoot}\n相对位置：{preview.Package.RelativePath}\n\n仅标记本地已有，不代表已安装或文件完整。Web仅同步公开作品资料和存在布尔值，不接收以上路径。";
    });
    private bool CanConfirm() => Approved && _preview is not null;
    [RelayCommand(CanExecute = nameof(CanConfirm))] private Task ConfirmAsync() => run(async ct =>
    {
        var preview = _preview ?? throw new ArgumentException("请先预览。");
        if (!Approved) throw new ArgumentException("请核对并确认。");
        if (preview.Origin != origin()) throw new ArgumentException("Web连接已变化，请重新选择作品并预览。");
        Invalidate(); var source = service(); await Task.Run(() => source.CommitAsync(preview, LocalArchiveFormat.Hash(preview), ct), ct);
        await saved(ct);
        Summary = "安装包已登记在本机JSON。公开作品登记及存在布尔值使用原同步流程；安装包不会出现在启动安装列表。";
        status(Summary);
    });
    [RelayCommand] private Task VerifyAsync() => run(async ct =>
    {
        var source = service(); var local = await Task.Run(() => source.VerifyAsync(ct), ct); await saved(ct);
        var packages = local.Packages ?? [];
        Summary = $"安装包观测：{packages.Count(p => p.Status == "present")} 个存在，{packages.Count(p => p.Status == "missing")} 个缺失，{packages.Count(p => p.Status == "unknown")} 个未知。断开根目录或文件变化须人工核对；未删除任何文件。";
        status(Summary);
    });
}
