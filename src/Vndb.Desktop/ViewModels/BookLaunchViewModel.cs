using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Desktop.Services;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public partial class MainViewModel
{
    public IReadOnlyList<string> LaunchMethods { get; } = ["直接启动", "LE 默认配置"];
    [ObservableProperty] private string _launchMode = "直接启动";
    partial void OnLaunchModeChanged(string value) => LaunchPreview = "启动方式已变化，请重新预览已保存命令。";
    private IReadOnlyList<LaunchHistory> _history = [];
    private async Task LoadShelfPreferencesAsync(CancellationToken ct)
    {
        if (_services is null) return;
        var store = new ShelfPreferencesStore(_services.SettingsStore.DirectoryPath);
        Shelf.ApplyHomePreferences(await store.ReadAsync(ct)); _history = await store.HistoryAsync(ct);
    }
    private void ApplyHistory()
    {
        IndexResources();
        var lastOpened = _history.Where(h => h.ComponentId is not null).GroupBy(h => h.ComponentId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(h => h.OpenedAt), StringComparer.Ordinal);
        foreach (var book in _books)
        {
            var installations = book.Item is { } item ? _resourceInstallations![item.Item.Id] : [];
            book.LastOpenedAt = installations.SelectMany(i => BookComponents(i, book)).Select(c => lastOpened.TryGetValue(c.Id, out var at) ? (DateTimeOffset?)at : null).Max();
        }
    }
    private LaunchProfile? ResolveBookProfile(BookViewModel book)
    {
        if (book.Item is null) throw new ArgumentException("请先在个人书架选定具体条目，再配置或启动；不会按VNDB ID猜测版本。");
        var chosen = SelectedBook == book ? SelectedInstallation : null;
        SelectedBook = book;
        var installations = _installations.Where(i => i.Installation.RemovedAt is null && i.Installation.Status == "present" && MatchesBook(i, book)).ToArray();
        var installation = installations.Length == 1 ? installations[0] : installations.SingleOrDefault(i => i.Installation.Id == chosen?.Installation.Id);
        if (installation is null) throw new ArgumentException("请在作品详情明确选择本机安装，再进入配置；不会猜测目录。");
        SelectedInstallation = installation;
        var components = BookComponents(installation, book).Select(c => c.Id).ToHashSet();
        var choices = Profiles.Where(p => p.InstallationId == installation.Installation.Id && p.ComponentId is not null && components.Contains(p.ComponentId)).ToArray();
        // Old direct/LE pairs are the same command. Distinct EXEs/arguments/working dirs require selection.
        var commands = LaunchConfiguration.CommandChoices(choices);
        if (commands.Count == 1) { SelectedProfile = commands[0]; return SelectedProfile; }
        ConfigureBook(book);
        Status = choices.Length == 0 ? "请为此作品配置自定义EXE、参数与工作目录。" : "此作品有多个不同启动命令，请在本机启动页明确选择。";
        return null;
    }
    [RelayCommand] private void ConfigureBook(BookViewModel? book)
    {
        if (book is null) { Status = "请先选择作品。"; return; }
        if (book.Item is null) { SelectedBook = book; Registration.Select(new(book.ArchiveTitle, book.Item, book.Metadata, book.GroupCompany)); Section = 7; Status = "请先登记或关联具体书架条目，再进入本机启动配置。"; return; }
        SelectedBook = book;
        // Never leave another game's editor selected when opening an unconfigured book.
        if (SelectedInstallation is null) { NewLaunch(); LaunchTitle = book.Title; }
        else
        {
            SelectedProfile = null; Executable = ""; SetArguments([]); ActualExecutable = "";
            WorkingDirectory = SelectedInstallation.Installation.Details.RootPath;
            var components = BookComponents(SelectedInstallation, book).ToArray();
            ComponentId = components.Length == 1 ? components[0].Id : "";
            var profiles = Profiles.Where(p => p.InstallationId == SelectedInstallation.Installation.Id && p.ComponentId == ComponentId).ToArray();
            if (profiles.Length == 1) SelectedProfile = profiles[0]; else LaunchTitle = book.Title;
        }
        Section = 3; ProfilesForSelectedBook = SelectedInstallation is not null; LaunchFilter = "";
        Status = "本机启动配置：可自定义EXE、参数、工作目录及检查注册信息。";
    }
    [RelayCommand] private Task DirectBookAsync(BookViewModel book) => LaunchBookAsync(book, LaunchMethod.Direct);
    [RelayCommand] private Task LocaleBookAsync(BookViewModel book) => LaunchBookAsync(book, LaunchMethod.GlobalLocale);
    private async Task LaunchBookAsync(BookViewModel book, LaunchMethod method)
    {
        try { if (ResolveBookProfile(book) is { } profile) await LaunchProfileAsync(profile, method); }
        catch (Exception e) when (Recoverable(e)) { Status = "启动失败：" + e.Message; }
    }
    private async Task LaunchProfileAsync(LaunchProfile profile, LaunchMethod method)
    {
        if (_services is not null) await _services.EnsureObservedLaunchAsync(profile, CancellationToken.None);
        var pid = await _launcher.LaunchAsync(profile.Id, method: method, globalLocale: _services?.Settings.DefaultLocale);
        Status = $"已启动，PID {pid}。";
        if (_services is not null)
        {
            try
            {
                var store = new ShelfPreferencesStore(_services.SettingsStore.DirectoryPath);
                await store.OpenedAsync(profile, CancellationToken.None); _history = await store.HistoryAsync(CancellationToken.None); ApplyHistory(); Shelf.Refresh();
            }
            catch (Exception e) when (Recoverable(e)) { Status += " 启动历史未保存：" + e.Message; }
        }
    }
    [RelayCommand] private void OpenBookFolder(BookViewModel book)
    {
        try
        {
            if (book.Item is null) throw new ArgumentException("请先在个人书架选定具体条目，不会按VNDB ID猜测目录。");
            SelectedBook = book;
            var installation = SelectedInstallation ?? throw new ArgumentException("请先选择唯一的本机安装。");
            var path = installation.Installation.Details.RootPath; LocalFiles.CheckAncestors(path);
            if (!Directory.Exists(path)) throw new IOException("目录不存在，请先重新核对本机安装。");
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { path } });
        }
        catch (Exception e) when (Recoverable(e)) { Status = "无法打开目录：" + e.Message; }
    }
}
