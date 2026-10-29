using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Desktop.Services;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public sealed record RegistrationRow(RegistrationComponent Component)
{
    public string Title => Component.Work.Title;
    public string Location => Component.RelativePath + " · " + LaunchComponentChoice.KindLabel(Component.Kind) + (Component.Label is { Length: > 0 } label ? " · " + label : "");
}

/// <summary>The registration form owns drafts only; Local owns validation, identity and persistence.</summary>
public partial class RegistrationFeatureViewModel(Func<LocalInstallationRegistration> registration,
    Func<string?> origin, Func<LocalRegistrationSync> journal, IDesktopDialogs dialogs,
    Func<Func<CancellationToken, Task>, Task> run, Action<string> status,
    Func<RegistrationResult, CancellationToken, Task> saved) : ObservableObject
{
    private RegistrationSelection? _choice;
    private RegistrationPreview? _preview;
    private long _context;
    [ObservableProperty] private string _rootPath = "";
    [ObservableProperty] private string _containerTitle = "";
    [ObservableProperty] private string _company = "";
    [ObservableProperty] private string _workTitle = "";
    [ObservableProperty] private string _relativePath = ".";
    [ObservableProperty] private string _componentKind = "game";
    [ObservableProperty] private string _componentLabel = "";
    [ObservableProperty] private string _selectionLabel = "可从书架／查询结果选择作品，或填写未收录作品名称。";
    [ObservableProperty] private string _previewSummary = "添加作品组件后预览。登记保留现有游戏目录和内部结构。";
    [ObservableProperty] private string _validationMessage = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ConfirmCommand))] private bool _approved;
    [ObservableProperty] private RegistrationRow? _selectedComponent;
    public ObservableCollection<RegistrationRow> Components { get; } = [];
    public IReadOnlyList<string> ComponentKinds { get; } = ["game", "bonus", "edition", "other"];
    public RegistrationResult? Result { get; private set; }
    public RegistrationFeatureViewModel Select(RegistrationSelection selection)
    {
        _choice = selection; WorkTitle = selection.Title;
        SelectionLabel = selection.Metadata?.VndbId ?? selection.Item?.Item.Details.NovelId ?? "手工作品 · 未关联VNDB";
        if (ContainerTitle.Length == 0) ContainerTitle = selection.Title;
        if (Company.Length == 0) Company = selection.Company ?? "";
        return this;
    }
    public void Invalidate()
    {
        _context++; _preview = null; Approved = false; ValidationMessage = ""; ConfirmCommand.NotifyCanExecuteChanged();
        PreviewSummary = "登记条件已变化，请重新预览。";
    }
    partial void OnRootPathChanged(string value) => Invalidate();
    partial void OnContainerTitleChanged(string value) => Invalidate();
    partial void OnCompanyChanged(string value) => Invalidate();
    [RelayCommand] private Task ChooseRootAsync() => run(async _ => { if (await dialogs.FolderAsync("选择完整游戏／合集根目录") is { } path) RootPath = path; });
    [RelayCommand] private void ManualWork() { _choice = null; WorkTitle = ""; SelectionLabel = "手工作品 · 未关联VNDB"; }
    [RelayCommand] private void AddComponent()
    {
        if (string.IsNullOrWhiteSpace(WorkTitle)) { ValidationMessage = "请先选择作品或填写未收录作品名称。"; status(ValidationMessage); return; }
        var choice = _choice is null ? new RegistrationSelection(WorkTitle, Company: Company) : _choice with { Title = WorkTitle };
        Components.Add(new(new(choice, RelativePath, ComponentKind, string.IsNullOrWhiteSpace(ComponentLabel) ? null : ComponentLabel)));
        Invalidate(); status("组件已加入草稿；请继续添加合集或特典，完成后预览。");
    }
    [RelayCommand] private void RemoveComponent() { if (SelectedComponent is { } row) { Components.Remove(row); Invalidate(); } }
    private Task RunFormAsync(Func<CancellationToken, Task> action) => run(async ct =>
    {
        ValidationMessage = "";
        try { await action(ct); }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException)
        { ValidationMessage = error.Message; throw; }
    });
    [RelayCommand] private Task PreviewAsync() => RunFormAsync(async ct =>
    {
        Invalidate(); var context = _context;
        var request = new InstallationRegistration(RootPath, ContainerTitle, Components.Select(r => r.Component).ToArray(), string.IsNullOrWhiteSpace(Company) ? null : Company, origin());
        var preview = await Task.Run(() => registration().PreviewAsync(request, ct), ct);
        if (context != _context) throw new InvalidOperationException("预览期间登记条件已变化，请重新预览。");
        _preview = preview; ConfirmCommand.NotifyCanExecuteChanged();
        PreviewSummary = $"完整目录\n{preview.Request.RootPath}\n\n名称：{preview.Request.Title}\n会社分组：{preview.Request.Company ?? "待确认"}\n\n" +
            string.Join('\n', preview.Installation.Components.Select(c => $"{preview.Items.FirstOrDefault(i => i.Item.Id == c.Details.ItemId)?.Item.Details.Title ?? "原有组件"}  →  {c.Details.RelativePath}  [{LaunchComponentChoice.KindLabel(c.Details.Kind)}]")) +
            $"\n\n{(preview.Reused ? "复用已有安装及组件。" : "保存本机安装与组件关联。")}\n文件不会移动、改名或启动。未同步作品会保留公开登记JSON，后续同步或导入Web。";
        status("登记预览已生成，请核对完整目录和全部作品组件。");
    });
    private bool CanConfirm() => Approved && _preview is not null;
    [RelayCommand(CanExecute = nameof(CanConfirm))] private Task ConfirmAsync() => RunFormAsync(async ct =>
    {
        var preview = _preview ?? throw new ArgumentException("请先预览本次登记。");
        if (!Approved) throw new ArgumentException("请先核对并确认登记。");
        Invalidate();
        Result = await Task.Run(() => registration().CommitAsync(preview, LocalArchiveFormat.Hash(preview), ct), ct);
        await saved(Result, ct);
        PreviewSummary = "已登记到本机JSON。可进入本机启动配置，明确选择EXE；目录整理仍需另行预览和批准。";
        status(Result.Reused ? "已复用原安装及组件，未重复创建。" : "本机关联已保存；文件保持原位，公开资料等待同步。");
    });
    [RelayCommand] private Task ExportAsync() => RunFormAsync(async ct =>
    {
        var items = Result?.Items.Where(i => LocalInstallationRegistration.IsPending(i.Item.Id)).ToArray() ?? [];
        if (items.Length == 0) throw new ArgumentException("没有本次待同步登记资料；请先确认登记。");
        if (await dialogs.FolderAsync("选择公开登记JSON导出目录") is not { } directory) return;
        var count = await journal().ExportToDirectoryAsync(items.Select(i => i.Item.Id), directory, ct);
        status($"已导出{count}份公开登记JSON，可通过Web导入；原稿保留。");
    });
}
