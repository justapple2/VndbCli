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
    private readonly IObservationFiles? _observationFiles;
    private readonly IObservationWatchFactory? _observationWatches;
    private readonly TimeProvider? _observationTime;
    public DesktopLibraryAdapter Library { get; } = new();
    private readonly Dictionary<string, string> _sessionKeys = new(StringComparer.Ordinal);
    public LocalClientSettingsStore SettingsStore { get; }
    public ObservationSettingsStore ObservationSettingsStore { get; }
    public ObservationSettings ObservationSettings { get; private set; } = new();
    public ObservationHost? ObservationHost { get; private set; }
    private ObservationResult? _observationResult;
    public ObservationResult? ObservationResult => Volatile.Read(ref _observationResult);
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
    public DesktopServices(string directory, HttpClient? http = null, VndbClientOptions? options = null,
        IObservationFiles? observationFiles = null, IObservationWatchFactory? observationWatches = null, TimeProvider? observationTime = null)
    {
        SettingsStore = new(directory); Launcher = new(directory); Activities = new(directory); PersonalEdits = new(directory); LocalLibrary = new(directory);
        _observationFiles = observationFiles; _observationWatches = observationWatches; _observationTime = observationTime;
        ObservationSettingsStore = new(directory, observationFiles);
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _core = new(_http, options);
        _profile = new(SettingsStore, Launcher, LocalLibrary);
    }
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var profile = await _profile.LoadAsync(Environment.GetEnvironmentVariable("VN_LIBRARY_URL"), ct);
        await ConfigureAsync(profile.Settings, null, false, ct);
        Identity = profile.Identity; Library.Seed(profile.Library);
        ObservationSettings = await ObservationSettingsStore.ReadAsync(ct);
    }

    public async Task StartSessionsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Sessions is not null) return;
        var sessions = new LocalSessionManager(Launcher, Activities, SettingsStore.DirectoryPath);
        try
        {
            await sessions.RecoverAsync(ct);
            await StartObservationAsync(ct);
            ct.ThrowIfCancellationRequested();
        }
        catch { await StopObservationAsync(); sessions.Dispose(); throw; }
        Sessions = sessions; ActivityHost = new(this); ActivityHost.Start(); ActivityHost.SignalSync();
    }
    private void Observed(ObservationResult result)
    {
        Volatile.Write(ref _observationResult, result);
        if (result.Metrics.ChangedInstallations + result.Metrics.ChangedPackages > 0) ActivityHost?.SignalPresence();
    }
    private async Task StartObservationAsync(CancellationToken ct)
    {
        if (Identity is null || !ObservationSettings.Enabled) return;
        var host = new ObservationHost(LocalLibrary, Identity.Id, token => Launcher.ReadAsync(token), _observationFiles, _observationWatches, _observationTime);
        host.Changed += Observed;
        try { await host.StartAsync(ObservationSettings, ct); ObservationHost = host; }
        catch { await host.DisposeAsync(); throw; }
    }
    public async Task ConfigureObservationAsync(ObservationSettings value, CancellationToken ct)
    {
        await ObservationSettingsStore.SaveAsync(value, ct);
        if (ObservationHost is { } previous) { await previous.DisposeAsync(); ObservationHost = null; }
        ObservationSettings = value; await StartObservationAsync(ct);
    }
    public async Task<ObservationResult> CheckObservationAsync(CancellationToken ct, string? path = null)
    {
        if (ObservationHost is { Running: true } host && await Task.Run(() => host.CheckNowAsync(ct, path), ct) is { } current) return current;
        if (Identity is null) throw new InvalidOperationException("请先载入本机身份。");
        var result = await Task.Run(async () => await new LocalObservation(LocalLibrary, Identity.Id, _observationFiles, _observationTime).CheckAsync(ObservationSettings, await Launcher.ReadAsync(ct), path is null ? null : [path], path is null, ct), ct);
        Observed(result); return result;
    }
    public async Task EnsureObservedLaunchAsync(LaunchProfile profile, CancellationToken ct)
    {
        if (!ObservationSettings.Enabled || profile.InstallationId is null) return;
        var local = await LocalLibrary.ReadSummaryAsync(ct); var installation = local.Installations.SingleOrDefault(i => i.Installation.Id == profile.InstallationId);
        if (installation is null || !(ObservationSettings.Roots ?? []).Any(r => r.Kind == "games" && Vndb.Local.ObservationSettingsStore.Within(r.Path, installation.Installation.Details.RootPath))) return;
        var result = await CheckObservationAsync(ct, installation.Installation.Details.RootPath);
        var row = result.Snapshot.Installations.SingleOrDefault(i => i.Id == profile.InstallationId);
        if (result.Deferred || row is null || row.Status != "present" || (row.ReadyProfiles is null ? row.LaunchState != "ready" : !row.ReadyProfiles.Contains(profile.Id)))
            throw new InvalidOperationException("启动前观测尚未通过：" + (row is null ? "请重新核对本机登记" : ObservationText.Reason(row.Reason)));
    }
    public async Task StopObservationAsync() { if (ObservationHost is { } host) { ObservationHost = null; await host.DisposeAsync().ConfigureAwait(false); } }
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
        Library.Bind(new(LocalLibrary, StateDirectory, settings.Server, Web is null ? null : new(Web.ItemsAsync, Web.PlaysAsync, Web.RemovedItemsAsync, Web.ShelfSummaryAsync, Web.ItemAsync)),
            new(_core, new JsonSnapshotStore(QueryDirectory, uri?.AbsoluteUri), Web),
            new(QueryDirectory, uri?.AbsoluteUri, Web is null ? null : Web.BrowseCatalogAsync, Web is null ? null : Web.CatalogProducersAsync));
    }
    public LocalClientSync SyncService() => new(Web ?? throw new WebUnavailableException(), Settings.Server ?? throw new WebUnavailableException(),
        QueryDirectory, Activities, PersonalEdits, LocalLibrary, Identity?.Id);
    public LocalOrganizationWorkspace OrganizationWorkspace() => new(SettingsStore.DirectoryPath,
        Identity ?? throw new InvalidOperationException("本机身份未加载。"));
    public void Dispose() { StopObservationAsync().GetAwaiter().GetResult(); Sessions?.Dispose(); _core.Dispose(); _http.Dispose(); }
}
