using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.Services;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IDesktopLibrary _library;
    private readonly DesktopServices? _services;
    private readonly LocalLauncher _launcher;
    private readonly IDesktopDialogs _dialogs;
    private CancellationTokenSource? _operation;
    private IReadOnlyList<BookViewModel> _books = [];
    private IReadOnlyList<BookViewModel> _queryBooks = [];
    private IReadOnlyList<LocalInstallationView> _installations = [];
    private IReadOnlyList<LocalOwnedPackage> _packages = [];
    public ObservableCollection<LocalInstallationView> Installations { get; } = [];
    public ObservableCollection<LaunchProfile> Profiles { get; } = [];
    public ObservableCollection<LocaleProfile> LocaleProfiles { get; } = [];
    public ObservableCollection<ExecutableCandidate> ExecutableCandidates { get; } = [];

    [ObservableProperty] private int _section;
    [ObservableProperty] private string _status = "加载本机配置后即可查询或配置离线启动。";
    [ObservableProperty] private bool _busy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectedMetadata))] private BookViewModel? _selectedBook;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectedComponents))] private LocalInstallationView? _selectedInstallation;
    public MetadataSnapshot? SelectedMetadata => SelectedBook?.Metadata;
    public IReadOnlyList<LocalInstallationComponent> SelectedComponents => SelectedInstallation?.Components ?? [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSavedLaunch)), NotifyPropertyChangedFor(nameof(CanStartTracked)), NotifyPropertyChangedFor(nameof(LaunchDraftChanged)), NotifyPropertyChangedFor(nameof(LaunchDraftNotice))] private LaunchProfile? _selectedProfile;
    [ObservableProperty] private string _serverUrl = "";
    [ObservableProperty] private string _managementKey = "";
    [ObservableProperty] private bool _rememberKey;
    [ObservableProperty] private string _stateLocation = "";
    [ObservableProperty] private string _dataRootLocation = "";
    [ObservableProperty] private string _launchFilter = "";
    [ObservableProperty] private bool _profilesForSelectedBook;
    public IEnumerable<LaunchProfile> FilteredProfiles => Profiles.Where(p => (!ProfilesForSelectedBook || SelectedBook is { } book && SelectedInstallation is { } installation &&
        p.InstallationId == installation.Installation.Id && (p.ComponentId is null || BookComponents(installation, book).Any(c => c.Id == p.ComponentId))) &&
        (string.IsNullOrWhiteSpace(LaunchFilter) || p.Title.Contains(LaunchFilter, StringComparison.OrdinalIgnoreCase)));
    partial void OnProfilesForSelectedBookChanged(bool value) => OnPropertyChanged(nameof(FilteredProfiles));
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LaunchDraftChanged)), NotifyPropertyChangedFor(nameof(LaunchDraftNotice))] private string _launchTitle = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LaunchDraftChanged)), NotifyPropertyChangedFor(nameof(LaunchDraftNotice))] private string _executable = "";
    [ObservableProperty] private string _localeEmulatorPath = "";
    [ObservableProperty] private string _localeProfileId = "";
    [ObservableProperty] private LocaleProfile? _selectedLocaleProfile;
    [ObservableProperty] private ExecutableCandidate? _selectedExecutableCandidate;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LaunchDraftChanged)), NotifyPropertyChangedFor(nameof(LaunchDraftNotice))] private string _argumentsJson = "[]";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LaunchDraftChanged)), NotifyPropertyChangedFor(nameof(LaunchDraftNotice))] private string _workingDirectory = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LaunchDraftChanged)), NotifyPropertyChangedFor(nameof(LaunchDraftNotice))] private string _componentId = "";

    public MainViewModel(IDesktopLibrary library, LocalLauncher launcher, IDesktopDialogs dialogs, DesktopServices? services = null, IQuickPlayService? quickPlayService = null)
    {
        _library = library; _launcher = launcher; _dialogs = dialogs; _services = services;
        Profiles.CollectionChanged += (_, _) => UpdateBookDetails();
        _quickPlayService = quickPlayService ?? (services is null ? null : new QuickPlayService(services.PersonalEdits, () => services.Settings.Server, () => services.Web, library.PlaysAsync));
        Shelf = new(preferences => GuardAsync(async ct =>
        {
            await new ShelfPreferencesStore(Services().SettingsStore.DirectoryPath).SaveAsync(preferences, ct);
            Status = "已记住本机书架偏好；其他页面浏览状态独立保留在本次会话。";
        }));
        Shelf.SelectionChanged += book => SelectedBook = book;
        Discovery = new(library, dialogs, GuardAsync, text => Status = text);
        Discovery.Completed += PresentQuery;
        PublicCatalog = new(library, GuardAsync, text => Status = text, PresentQuery);
        Organization = new(() => Services().OrganizationWorkspace(), () => SelectedInstallation, () => _installations, dialogs, GuardAsync, text => Status = text);
        Registration = new(() => new(Services().LocalLibrary, Services().Identity ?? throw new InvalidOperationException("请先加载本机身份。")),
            () => _services?.Settings.Server, () => new(Services().LocalLibrary), dialogs, GuardAsync, text => Status = text, RegistrationSavedAsync);
        Packages = new(() => new(Services().LocalLibrary, Services().Identity ?? throw new InvalidOperationException("请先加载本机身份。")),
            () => _services?.Settings.Server, GuardAsync, text => Status = text, text => { Discovery.Query = text; Section = 2; }, PackagesSavedAsync, dialogs);
        Presence = new(() => new(Services().LocalLibrary), () => _services?.Web, () => _services?.Settings.Server, () => _services?.Identity?.Id, GuardAsync, text => Status = text);
        QueryData = new(() => Services().QueryScope, GuardAsync, text => Status = text);
        Imports = new(ct => Services().ImportStatusAsync(ct), GuardAsync);
        InitializeNavigation();
    }

    public BookDetailsViewModel BookDetails { get; } = new();
    private void UpdateBookDetails() => BookDetails.Update(SelectedBook, _installations, Profiles.ToArray(), SelectedInstallation);
    public ShelfBrowserViewModel Shelf { get; }
    public QueryFeatureViewModel Discovery { get; }
    public PublicCatalogViewModel PublicCatalog { get; }
    public OrganizationFeatureViewModel Organization { get; }
    public RegistrationFeatureViewModel Registration { get; }
    public PackagesFeatureViewModel Packages { get; }
    public PresenceFeatureViewModel Presence { get; }
    public QueryDataFeatureViewModel QueryData { get; }
    public ImportFeedbackViewModel Imports { get; }
    [ObservableProperty] private string _queryRootOverride = "";
    public async Task InitializeAsync() => await GuardAsync(async ct =>
    {
        if (_services is not null)
        {
            await _services.InitializeAsync(ct);
            var settings = _services.Settings; ServerUrl = settings.Server ?? "";
            Organization.SourceScope = settings.SourceScope; Organization.TargetRoot = settings.TargetRoot; StateLocation = _services.QueryDirectory; QueryRootOverride = settings.CacheDirectory ?? "";
            DataRootLocation = _services.SettingsStore.DirectoryPath;
            LocaleEmulatorPath = settings.LocaleEmulatorPath;
            LocaleProfileId = settings.DefaultLocale?.ProfileId ?? "";
            var ui = await new UiPreferencesStore(_services.SettingsStore.DirectoryPath).ReadAsync(ct);
            ThemePreference = ui.Theme; ReducedMotion = ui.ReducedMotion;
        }
        await LoadProfilesAsync(ct);
        await LoadShelfPreferencesAsync(ct);
        Status = "本机配置已加载。刷新书架或查询资料；本机启动独立于Web。";
    });

    [RelayCommand] private Task RefreshAsync() => GuardAsync(async ct =>
    {
        if (QuickEditor is { Applied: false }) { Status = "请先完成或退出当前游玩编辑。"; return; }
        var result = await _library.ShelfAsync(ct);
        _installations = result.Snapshot.Installations;
        _packages = result.Snapshot.Packages ?? [];
        _books = ShelfPlayResolver.Resolve(result.Snapshot).Select(i => new BookViewModel(i.Item, i.Item.Metadata, i.Plays, i.NeedsReview)).ToArray();
        foreach (var book in _books) UpdateOwnership(book);
        ApplyHistory(); Shelf.SetLibrary(_books, result.Source);
        Installations.Clear(); foreach (var i in _installations.Where(i => SelectedBook is null || MatchesBook(i, SelectedBook))) Installations.Add(i);
        OnPropertyChanged(nameof(LaunchInstallations));
        UpdateBookDetails();
        Status = $"已读取 {_books.Count} 部作品、{result.Snapshot.Plays.Count} 条游玩记录。" + (_books.Any(b => b.NeedsPlayReview) ? "部分关联待核对，请联网刷新或读取所选条目的记录。" : "");
    });
    private void PresentQuery(MetadataResponse result)
    {
        _queryBooks = result.Items.Select(m => { var book = new BookViewModel(null, m); UpdateOwnership(book); return book; }).ToArray();
        var source = result.Source + (result.PendingImport ? " · 待同步JSON" : "") + (result.More ? " · 还有更多结果，请缩小查询" : "");
        Shelf.SetQuery(_queryBooks, source); Section = 2;
    }
    [RelayCommand] private Task DetailAsync() => GuardAsync(async ct =>
    {
        var book = SelectedBook ?? throw new ArgumentException("请先选择作品。");
        IReadOnlyList<PersonalPlayView> plays = book.Plays;
        if (book.Item is not null)
        {
            try { plays = await _library.PlaysAsync(book.Item.Item.Id, ct); }
            catch (WebUnavailableException) { Status = "Web不可达，详情使用本机已缓存的记录。"; }
        }
        if (SelectedBook != book) return;
        if (!ReferenceEquals(plays, book.Plays)) book.ApplyPlays(plays);
        if (SelectedBook == book) SetSessionPlays(book.Plays);
        UpdateBookDetails(); Shelf.Refresh();
    });
    partial void OnSelectedBookChanged(BookViewModel? value)
    {
        Shelf.SelectIfVisible(value);
        OnPropertyChanged(nameof(FilteredProfiles));
        if (value is null)
        {
            SelectedInstallation = null;
            SetSessionPlays([]);
            Organization.Company = ""; Organization.JapaneseTitle = ""; UpdateBookDetails(); return;
        }
        Organization.Company = value.GroupCompany; Organization.JapaneseTitle = value.Title; LaunchTitle = value.Title;
        SetSessionPlays(value.Plays);
        Installations.Clear(); foreach (var i in _installations.Where(i => MatchesBook(i, value))) Installations.Add(i);
        SelectedInstallation = Installations.Count == 1 ? Installations[0] : null;
        UpdateBookDetails();
    }
    private IEnumerable<LocalInstallationComponent> BookComponents(LocalInstallationView installation, BookViewModel book) => installation.Components.Where(c => book.Item is { } item ? c.Details.ItemId == item.Item.Id :
        book.Metadata?.VndbId is { } id && _services?.LocalCatalog.Items.Any(i => i.Item.Id == c.Details.ItemId && i.Item.Details.NovelId == id) == true);
    private bool MatchesBook(LocalInstallationView installation, BookViewModel book) => BookComponents(installation, book).Any();
    partial void OnSectionChanged(int value) { Shelf.Activate(value); OnPropertyChanged(nameof(SelectedNavigation)); }
    partial void OnSelectedInstallationChanged(LocalInstallationView? value)
    {
        OnPropertyChanged(nameof(FilteredProfiles));
        OnPropertyChanged(nameof(LaunchComponents)); OnPropertyChanged(nameof(SelectedLaunchComponent));
        OnPropertyChanged(nameof(LaunchInstallations)); OnPropertyChanged(nameof(SelectedLaunchInstallation)); OnPropertyChanged(nameof(LaunchDraftChanged)); OnPropertyChanged(nameof(LaunchDraftNotice));
        Organization.Invalidate(); InvalidateRegistry(); RegistryCandidates.Clear();
        RegistrySource = RegistryTarget = value?.Installation.Details.RootPath ?? ""; RegistryTerm = SelectedBook?.GroupCompany ?? "";
        UpdateBookDetails();
        if (value is null) return;
        if (_editingLaunchAssociation)
        {
            if (!value.Components.Any(c => c.Id == ComponentId)) ComponentId = "";
            return;
        }
        if (SelectedProfile?.InstallationId != value.Installation.Id)
        {
            SelectedProfile = null; Executable = ""; SetArguments([]); ActualExecutable = "";
            WorkingDirectory = value.Installation.Details.RootPath;
            ComponentId = value.Components.Count == 1 ? value.Components[0].Id : "";
            var profiles = Profiles.Where(p => p.InstallationId == value.Installation.Id).ToArray();
            if (profiles.Length == 1) SelectedProfile = profiles[0];
        }
    }
    partial void OnLaunchFilterChanged(string value) => OnPropertyChanged(nameof(FilteredProfiles));
    partial void OnSelectedProfileChanged(LaunchProfile? value)
    {
        LaunchPreview = "当前选择已变化，请重新预览已保存命令；尚未运行程序。";
        if (value is null) return;
        if (value.InstallationId != SelectedInstallation?.Installation.Id)
            SetLaunchInstallation(_installations.FirstOrDefault(i => i.Installation.Id == value.InstallationId && i.Installation.RemovedAt is null));
        UpdateBookDetails();
        LaunchTitle = value.Title; Executable = value.Executable; SetArguments(value.Arguments);
        ActualExecutable = value.TrackedExecutable ?? "";
        WorkingDirectory = value.WorkingDirectory; ComponentId = value.ComponentId ?? "";
    }
    partial void OnSelectedLocaleProfileChanged(LocaleProfile? value) { if (value is not null) LocaleProfileId = value.Id; }
    partial void OnSelectedExecutableCandidateChanged(ExecutableCandidate? value)
    { if (value is not null) { Executable = value.Path; WorkingDirectory = Path.GetDirectoryName(value.Path)!; } }
    [RelayCommand] private void Cancel() => _operation?.Cancel();

    [RelayCommand] private Task SaveSettingsAsync() => GuardAsync(async ct =>
    {
        if (_services?.Sessions?.Running == true) throw new InvalidOperationException("请先结束当前会话，再切换连接设置。");
        var services = Services();
        var locale = string.IsNullOrWhiteSpace(LocaleEmulatorPath) ? null : await LocaleEmulator.CaptureAsync(new(LocaleEmulatorPath.Trim(), LocaleProfileId), ct);
        var settings = new LocalClientSettings(string.IsNullOrWhiteSpace(ServerUrl) ? null : ServerUrl, Organization.SourceScope, Organization.TargetRoot,
            LocaleEmulatorPath: locale?.ToolPath ?? "", DefaultLocale: locale, CacheDirectory: string.IsNullOrWhiteSpace(QueryRootOverride) ? null : Path.GetFullPath(QueryRootOverride));
        await services.ConfigureAsync(settings, RememberKey ? ManagementKey : string.IsNullOrEmpty(ManagementKey) ? null : ManagementKey, true, ct);
        Registration.Invalidate();
        Packages.Clear();
        Presence.Invalidate();
        QueryData.Invalidate();
        if (RememberKey && services.Settings.Server is { } server) await services.SettingsStore.SaveCredentialAsync(server, ManagementKey, ct);
        ManagementKey = ""; StateLocation = services.QueryDirectory;
        _books = []; _queryBooks = []; _installations = []; _packages = []; Discovery.Clear(); PublicCatalog.Clear(); Installations.Clear(); SelectedBook = null;
        SelectedInstallation = null; Organization.Invalidate(); Organization.RunDirectory = ""; Shelf.Clear();
        UpdateBookDetails(); Shelf.Refresh();
        Status = "设置已保存；书架快照按服务地址隔离，请刷新。";
    });
    [RelayCommand] private Task ForgetKeyAsync() => GuardAsync(async ct =>
    {
        var services = Services(); if (services.Settings.Server is not { } server) throw new ArgumentException("请先设置Web地址。");
        await services.SettingsStore.SaveCredentialAsync(server, null, ct);
        await services.ConfigureAsync(services.Settings, "", false, ct); ManagementKey = ""; Status = "已移除保存及本会话的管理密钥。";
    });
    [RelayCommand] private Task ChooseSourceAsync() => GuardAsync(async _ => { if (await _dialogs.FolderAsync("选择明确扫描范围") is { } folder) Organization.SourceScope = folder; });
    [RelayCommand] private Task ChooseTargetAsync() => GuardAsync(async _ => { if (await _dialogs.FolderAsync("选择归档目标根目录") is { } folder) Organization.TargetRoot = folder; });
    [RelayCommand] private Task ChooseLocaleToolAsync() => GuardAsync(async _ => { if (await _dialogs.ExecutableAsync() is { } file) LocaleEmulatorPath = LocaleEmulator.ResolveTool(file); });
    [RelayCommand] private Task LoadLocaleProfilesAsync() => GuardAsync(_ =>
    {
        var profiles = LocaleEmulator.Profiles(LocaleEmulatorPath); LocaleProfiles.Clear();
        foreach (var profile in profiles) LocaleProfiles.Add(profile);
        SelectedLocaleProfile = profiles.SingleOrDefault(p => p.Id == LocaleProfileId);
        Status = "已读取现有转区配置；请明确选择普通语言配置。"; return Task.CompletedTask;
    });
    [RelayCommand] private Task DiscoverExecutablesAsync() => GuardAsync(_ =>
    {
        ExecutableCandidates.Clear(); SelectedExecutableCandidate = null;
        foreach (var candidate in GameExecutables.Discover(WorkingDirectory)) ExecutableCandidates.Add(candidate);
        Status = "已过滤配置／安装／卸载等辅助程序。候选需手动选择，也可直接编辑EXE路径。"; return Task.CompletedTask;
    });
    [RelayCommand] private Task ChooseExecutableAsync() => GuardAsync(async _ =>
    {
        if (await _dialogs.ExecutableAsync() is not { } file) return;
        Executable = file; WorkingDirectory = Path.GetDirectoryName(file)!;
        if (LaunchTitle.Length == 0) LaunchTitle = Path.GetFileNameWithoutExtension(file);
    });
    [RelayCommand] private Task SaveLaunchAsync() => GuardAsync(async ct =>
    {
        if (_argumentsError is not null) throw new ArgumentException(_argumentsError);
        var args = JsonSerializer.Deserialize<string[]>(ArgumentsJson) ?? throw new ArgumentException("参数须为JSON字符串数组。");
        var profile = await new LaunchConfiguration(_launcher).SaveAsync(new(LaunchTitle, Executable, args, WorkingDirectory,
            SelectedInstallation, ComponentId, SelectedProfile), _services?.Identity?.Id, ct);
        await LoadProfilesAsync(ct); SelectedProfile = Profiles.Single(p => p.Id == profile.Id); Status = "本机启动配置已保存；尚未启动。";
    });
    [RelayCommand] private void NewLaunch()
    {
        SetLaunchInstallation(null);
        SelectedProfile = null; Executable = ""; WorkingDirectory = ""; LaunchTitle = ""; SetArguments([]); ComponentId = "";
        ActualExecutable = ""; ExecutableCandidates.Clear(); SelectedExecutableCandidate = null;
    }
    [RelayCommand] private async Task LaunchAsync()
    {
        // Deliberately independent of Busy, cancellation and every Web operation.
        try { await LaunchProfileAsync(SelectedProfile ?? throw new ArgumentException("请先选择已保存的本机配置。"), LaunchMode == "LE 默认配置" ? LaunchMethod.GlobalLocale : LaunchMethod.Direct); }
        catch (Exception e) when (Recoverable(e)) { Status = "启动失败：" + e.Message; }
    }
    private async Task LoadProfilesAsync(CancellationToken ct) { Profiles.Clear(); foreach (var p in await _launcher.ReadAsync(ct)) Profiles.Add(p); OnPropertyChanged(nameof(FilteredProfiles)); }
    [RelayCommand] private Task SyncAsync() => GuardAsync(async ct =>
    {
        var result = await Services().SyncService().SyncAsync(ct);
        await Imports.LoadAsync(ct);
        Status = $"已核对 {result.MetadataCount + result.PresenceCount} 个批次回执；最终处理结果以Web为准。";
    });
    private DesktopServices Services() => _services ?? throw new InvalidOperationException("此宿主没有配置管理服务。");
    private async Task GuardAsync(Func<CancellationToken, Task> action)
    {
        if (Busy) return;
        Busy = true; using var cancel = new CancellationTokenSource(); _operation = cancel;
        try { Status = "正在处理…可按Esc取消。"; await action(cancel.Token); if (Status == "正在处理…可按Esc取消。") Status = "已完成。"; }
        catch (OperationCanceledException) { Status = "操作已取消；已落盘的整理意图和回执保留，可续跑核对。"; }
        catch (Exception e) when (Recoverable(e)) { Status = "未完成：" + (e is WebUnavailableException ? "Web不可达。" : e.Message); }
        finally { _operation = null; Busy = false; }
    }
    private static bool Recoverable(Exception e) => e is ArgumentException or InvalidOperationException or IOException or
        UnauthorizedAccessException or ClientApiException or WebUnavailableException or OfflineCacheMissException or
        HttpRequestException or VndbApiException or TimeoutException or JsonException or System.ComponentModel.Win32Exception or PlatformNotSupportedException or System.Security.Cryptography.CryptographicException;
}
