using CommunityToolkit.Mvvm.Input;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public partial class MainViewModel
{
    [RelayCommand] private async Task QuickLaunchAsync()
    {
        if (SelectedBook is not { } book) { Status = "请先选择作品。"; return; }
        await LaunchBookAsync(book, LaunchMode == "LE 默认配置" ? LaunchMethod.GlobalLocale : LaunchMethod.Direct);
    }
    [RelayCommand] private Task RecheckLocalAsync() => GuardAsync(async ct =>
    {
        var services = Services(); var identity = services.Identity ?? throw new InvalidOperationException("本机身份未加载。");
        var report = await new LocalOrganizationWorkspace(services.SettingsStore.DirectoryPath, identity).RecheckAsync(Organization.SourceScope, Organization.TargetRoot, ct);
        var changed = (await services.LocalLibrary.ReadAsync(ct)).Installations;
        _installations = changed; Installations.Clear(); foreach (var i in changed) Installations.Add(i);
        foreach (var book in _books.Concat(_queryBooks)) UpdateOwnership(book);
        UpdateBookDetails(); Shelf.Refresh();
        services.ActivityHost?.SignalSync(); Status = $"本机核对 {report.Items.Count} 个安装；详细路径／候选只保存在本机，联网只同步存在布尔值。";
    });
}
