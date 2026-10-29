using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Desktop.Services;

namespace Vndb.Desktop.ViewModels;

public sealed record ShelfPage(MainViewModel Owner);
public sealed record QueryPage(MainViewModel Owner);
public sealed record LaunchPage(MainViewModel Owner);
public sealed record OrganizePage(MainViewModel Owner);
public sealed record SettingsPage(MainViewModel Owner);
public sealed record SessionPage(MainViewModel Owner);
public sealed record RegistrationPage(MainViewModel Owner);
public sealed record PublicCatalogPageView(MainViewModel Owner);
public sealed record NavigationEntry(int Index, string Title, string Description, string Icon, object Page);

public partial class MainViewModel
{
    public IReadOnlyList<NavigationEntry> NavigationEntries { get; private set; } = [];
    public NavigationEntry SelectedNavigation { get => NavigationEntries[Math.Clamp(Section, 0, NavigationEntries.Count - 1)]; set { if (value is not null) Section = value.Index; } }
    [ObservableProperty] private string _themePreference = "Default";
    [ObservableProperty] private bool _reducedMotion;
    private void InitializeNavigation() => NavigationEntries =
    [
        new(0, "我的书架", "收藏的故事，随时继续", "Library", new ShelfPage(this)),
        new(1, "游玩记录", "留下走过的故事与感想", "Played", new ShelfPage(this)),
        new(2, "查询作品", "优先查询Web资料库，离线仍可检索", "Search", new QueryPage(this)),
        new(3, "本机启动", "管理游戏与自定义启动方式", "Play", new LaunchPage(this)),
        new(4, "目录整理", "先预览，再整理；证据留在本机", "Folder", new OrganizePage(this)),
        new(5, "设置", "连接、语言与个人界面偏好", "Settings", new SettingsPage(this)),
        new(6, "游玩会话", "可选计时与待同步记录", "Clock", new SessionPage(this)),
        new(7, "本机登记", "关联完整游戏目录与作品组件", "Folder", new RegistrationPage(this)),
        new(8, "公共资料目录", "浏览Web离线目录；与个人书架独立", "Library", new PublicCatalogPageView(this))
    ];
    public async Task SetThemeAsync(string theme)
    {
        if (theme is not ("Default" or "Light" or "Dark")) throw new ArgumentException("未知主题。");
        ThemePreference = theme; await SaveUiPreferencesAsync();
    }
    [RelayCommand] private Task SaveAppearanceAsync() => SaveUiPreferencesAsync();
    private async Task SaveUiPreferencesAsync()
    {
        try { if (_services is not null) await new UiPreferencesStore(_services.SettingsStore.DirectoryPath).SaveAsync(new(ThemePreference, ReducedMotion)); }
        catch (Exception e) when (Recoverable(e)) { Status = "界面已应用，偏好未保存：" + e.Message; }
    }
    [RelayCommand] private void Escape()
    {
        if (QuickEditor is { } editor) { if (!editor.Busy) editor.CloseCommand.Execute(null); }
        else if (Shelf.DetailsOpen) Shelf.CloseDetailsCommand.Execute(null);
        else Cancel();
    }
}
