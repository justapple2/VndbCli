using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Local;

namespace Vndb.Desktop.Services;

/// <summary>Composition root for one profile. No SQLite; one shared Core rate limiter.</summary>
public sealed class DesktopServices : IDisposable
{
    private readonly HttpClient _http;
    private readonly VndbClient _core;
    private readonly LocalClientProfile _profile;
    public DesktopLibraryAdapter Library { get; } = new();
    private readonly Dictionary<string, string> _sessionKeys = new(StringComparer.Ordinal);
    public LocalClientSettingsStore SettingsStore { get; }
    public LocalLauncher Launcher { get; }
    public ActivityOutbox Activities { get; }
    public PersonalEditOutbox PersonalEdits { get; }
    public LocalLibraryStore LocalLibrary { get; }
    public LocalLibrary LocalCatalog => Library.Catalog;
    public LocalSessionManager? Sessions { get; private set; }
    public DesktopActivityHost? ActivityHost { get; private set; }
    public LocalClientSettings Settings { get; private set; } = new();
    public WebLibraryClient? Web { get; private set; }
    public ClientDeviceIdentity? Identity { get; private set; }
    public string StateDirectory => SettingsStore.Scope(Settings.Server);
    public LocalQueryScope QueryScope => LocalQueryScope.Resolve(SettingsStore.DirectoryPath, new(Settings.Server, Settings.CacheDirectory), server: Settings.Server ?? "");
    public string QueryDirectory => QueryScope.DirectoryPath;
    public Task<LocalImportStatus> ImportStatusAsync(CancellationToken ct) => ImportStatus.ReadAsync(Web, QueryScope, Activities.DirectoryPath, ct);
    public DesktopServices(string directory, HttpClient? http = null, VndbClientOptions? options = null)
    {
        SettingsStore = new(directory); Launcher = new(directory); Activities = new(directory); PersonalEdits = new(directory); LocalLibrary = new(directory);
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _core = new(_http, options);
        _profile = new(SettingsStore, Launcher, LocalLibrary);
    }
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var profile = await _profile.LoadAsync(Environment.GetEnvironmentVariable("VN_LIBRARY_URL"), ct);
        await ConfigureAsync(profile.Settings, null, false, ct);
        Identity = profile.Identity; Library.Seed(profile.Library);
    }

    public async Task StartSessionsAsync(CancellationToken ct = default)
    {
        if (Sessions is not null) return;
        Sessions = new(Launcher, Activities, SettingsStore.DirectoryPath);
        await Sessions.RecoverAsync(ct); ActivityHost = new(this); ActivityHost.Start(); ActivityHost.SignalSync();
    }
    public async Task ConfigureAsync(LocalClientSettings settings, string? sessionKey, bool save, CancellationToken ct)
    {
        var uri = string.IsNullOrWhiteSpace(settings.Server) ? null : ClientSettings.ServerUri(settings.Server);
        settings = settings with { Server = uri?.AbsoluteUri };
        var origin = uri?.AbsoluteUri;
        if (origin is not null && sessionKey is not null) _sessionKeys[origin] = sessionKey;
        var key = origin is null ? null : _sessionKeys.GetValueOrDefault(origin) ?? await SettingsStore.CredentialAsync(origin, ct);
        if (key is null && origin is not null && Environment.GetEnvironmentVariable("VN_LIBRARY_URL") is { Length: > 0 } environmentServer &&
            ClientSettings.ServerUri(environmentServer).AbsoluteUri == origin) key = Environment.GetEnvironmentVariable("VN_LIBRARY_KEY");
        if (save) await SettingsStore.SaveAsync(settings, ct);
        Settings = settings;
        Web = uri is null ? null : new(_http, uri, key, TimeSpan.FromSeconds(12));
        Library.Bind(new(LocalLibrary, StateDirectory, settings.Server, Web is null ? null : new(Web.ItemsAsync, Web.PlaysAsync, Web.RemovedItemsAsync)),
            new(_core, new JsonSnapshotStore(QueryDirectory, uri?.AbsoluteUri), Web),
            new(QueryDirectory, uri?.AbsoluteUri, Web is null ? null : Web.BrowseCatalogAsync));
    }
    public LocalClientSync SyncService() => new(Web ?? throw new WebUnavailableException(), Settings.Server ?? throw new WebUnavailableException(),
        QueryDirectory, Activities, PersonalEdits, LocalLibrary, Identity?.Id);
    public LocalOrganizationWorkspace OrganizationWorkspace() => new(SettingsStore.DirectoryPath,
        Identity ?? throw new InvalidOperationException("本机身份未加载。"));
    public void Dispose() { Sessions?.Dispose(); _core.Dispose(); _http.Dispose(); }
}
