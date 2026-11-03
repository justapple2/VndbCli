using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public sealed partial class ResourceArchiveViewModel(Func<LocalResourceArchive> store, Func<Task<string?>> chooseRoot) : ObservableObject
{
    private BookViewModel? _book;
    private long _generation;
    public IReadOnlyList<string> Kinds => LocalResourceArchive.Kinds;
    public IReadOnlyList<string> Verifications => LocalResourceArchive.Verifications;
    [ObservableProperty] private IReadOnlyList<LocalResource> _items = [];
    [ObservableProperty] private LocalResource? _selected;
    [ObservableProperty] private string _kind = "patch";
    [ObservableProperty] private string _rootPath = "";
    [ObservableProperty] private string _relativePath = "";
    [ObservableProperty] private string _language = "";
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string _releaseId = "";
    [ObservableProperty] private string _sourceUrl = "";
    [ObservableProperty] private string _verification = "unreviewed";
    [ObservableProperty] private bool _manuallyApplied;
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _installationId = "";
    [ObservableProperty] private string _packageId = "";
    [ObservableProperty] private string _message = "仅本机JSON保存位置与应用状态；不会上传、执行或解压资源。";
    [ObservableProperty] private bool _busy;
    public string SelectedTitle => _book?.Title ?? "请先选择个人书架作品";
    public void Select(BookViewModel? book) { if (_book?.Id == book?.Id && ReferenceEquals(_book, book)) return; _generation++; _book = book; Items = []; New(); OnPropertyChanged(nameof(SelectedTitle)); }
    partial void OnSelectedChanged(LocalResource? value)
    {
        if (value is null) return;
        var d = value.Details; Kind = d.Kind; RootPath = d.RootPath; RelativePath = d.RelativePath; Language = d.Language ?? ""; Version = d.Version ?? "";
        ReleaseId = d.ReleaseId ?? ""; SourceUrl = d.SourceUrl ?? ""; Verification = d.Verification; ManuallyApplied = d.ManuallyApplied; Notes = d.Notes ?? "";
        InstallationId = d.InstallationId ?? ""; PackageId = d.PackageId ?? "";
        Message = "已载入档案；点击检查所选资源核对当前文件。" + (value.Sha256 is null ? "" : " 已保存SHA256，仍需核对当前元数据。");
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (Busy) return; Busy = true;
        try { await action(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { Message = e.Message; }
        finally { Busy = false; }
    }
    [RelayCommand] private Task LoadAsync() => RunAsync(async () => { var generation = _generation; var id = _book?.Item?.Item.Id ?? throw new ArgumentException("请选择个人条目。"); var rows = await store().ReadAsync(id); if (generation == _generation) Items = rows; });
    [RelayCommand] private Task CheckAsync() => RunAsync(async () =>
    {
        var generation = _generation; var row = Selected ?? throw new ArgumentException("请选择已保存资源。");
        var facts = await Task.Run(() => LocalResourceArchive.Inspect(row));
        if (generation == _generation && Selected?.Id == row.Id) Message = facts.Message + (row.Sha256 is null ? "" : facts.HashCurrent ? " SHA256与当前元数据对应。" : " SHA256已过期，需按需重算。");
    });
    [RelayCommand] private Task ChooseRootAsync() => RunAsync(async () => { if (await chooseRoot() is { } root) RootPath = root; });
    [RelayCommand] private void New() { _generation++; Selected = null; Kind = "patch"; RootPath = ""; RelativePath = ""; Language = ""; Version = ""; ReleaseId = ""; SourceUrl = ""; Verification = "unreviewed"; ManuallyApplied = false; Notes = ""; InstallationId = ""; PackageId = ""; }
    private static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    [RelayCommand] private Task SaveAsync() => RunAsync(async () =>
    {
        var generation = _generation; var book = _book?.Item ?? throw new ArgumentException("请选择准确个人条目。");
        var data = new LocalResourceData(book.Item.Id, Kind, RootPath, RelativePath, Optional(InstallationId), Optional(PackageId), Optional(Language), Optional(Version), Optional(ReleaseId), Optional(SourceUrl), Verification, ManuallyApplied, Optional(Notes));
        var archive = store(); var id = Selected?.Id; var revision = Selected?.Revision;
        var saved = await Task.Run(() => archive.SaveAsync(data, id, revision, selectedItem: book));
        var rows = await archive.ReadAsync(book.Item.Id); if (generation != _generation) return;
        Items = rows; Selected = Items.Single(r => r.Id == saved.Id);
        Message = "资源档案已保存；未修改文件、存在标记或启动配置。";
    });
    [RelayCommand] private Task HashAsync() => RunAsync(async () =>
    {
        var generation = _generation; var row = Selected ?? throw new ArgumentException("请先选择已保存资源。大文件校验可使用取消命令。");
        using var cancel = new CancellationTokenSource(); _hashCancellation = cancel;
        try { var archive = store(); var saved = await Task.Run(() => archive.HashAsync(row.Id, row.Revision, cancel.Token), cancel.Token); var rows = await archive.ReadAsync(row.Details.ItemId); if (generation == _generation) { Items = rows; Selected = Items.Single(r => r.Id == saved.Id); } }
        catch (OperationCanceledException) { Message = "校验已取消；原档案保留。"; }
        finally { _hashCancellation = null; }
    });
    private CancellationTokenSource? _hashCancellation;
    [RelayCommand] private void CancelHash() => _hashCancellation?.Cancel();
}
