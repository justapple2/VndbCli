using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Vndb.Cli;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.Services;
using Vndb.Desktop.ViewModels;
using Vndb.Local;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

public sealed class QueryScopeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Origin = "http://library.test/";
    private static LocalQueryScope Scope(string root, string? origin = Origin, string? cache = null) => LocalQueryScope.Resolve(root, new(origin, cache), environment: _ => null);
    private static MetadataResponse Response(string id = "v17", string title = "Synthetic") => new([new(id, title, "vn", new() { Id = id, Title = title }, [], null, new("none"), DateTimeOffset.UnixEpoch, "client_vndb")], "client_vndb", DateTimeOffset.UnixEpoch, 1, 10, 1, false, PendingImport: true);
    private static Func<string, string?> Env(string root) => n => n == "VN_DESKTOP_HOME" ? root : null;
    private sealed class Factory(string root) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b) => b.UseSetting("Library:DataDirectory", root).UseSetting("Library:WriteKey", "scope-test-key").UseSetting("CatalogJobs:Enabled", "false");
    }
    private sealed class Routes : HttpMessageHandler
    {
        public bool Offline { get; set; }
        public string Instance { get; set; } = "test-instance";
        public int Writes { get; private set; }
        public bool LoseReply { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("Synthetic offline");
            object result;
            if (request.RequestUri!.Host == "api.vndb.org") result = new { results = Response().Items.Select(i => i.Novel), more = false };
            else if (request.RequestUri.AbsolutePath.EndsWith("info", StringComparison.Ordinal)) result = new ServiceInfo(Instance, [1], "test", 10, true, ["metadata_import"], [1], true);
            else
            {
                Writes++; using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var parsed = MetadataInput.Parse(payload.RootElement, new()); var hash = PlayRules.Hash(JsonSerializer.SerializeToUtf8Bytes(parsed.Items, VndbJson.Options));
                result = new ImportBatchReceipt("import-" + hash, hash, parsed.ClientId, "queued", true, parsed.Items.Count, 0,
                    parsed.Items.Select((i, index) => new ImportItemResult(index, i.OperationId, i.VndbId, i.Title, "preview")).ToArray(), DateTimeOffset.UtcNow, Instance, parsed.RawSha);
                if (LoseReply) { LoseReply = false; throw new HttpRequestException("Synthetic reply lost"); }
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(result, options: VndbJson.Options) };
        }
    }
    [Fact]
    public void ResolverUsesOnePrecedenceAndCanonicalOriginsWithIsolatedOverrides()
    {
        using var root = new TestDirectory(); var envCache = Path.Combine(root.Path, "env");
        var configured = new ClientSettings("http://config.test/", Path.Combine(root.Path, "config"));
        string? EnvironmentValue(string n) => n switch { "VN_LIBRARY_URL" => "HTTP://ENV.TEST:80", "VN_LIBRARY_CACHE" => envCache, _ => null };
        var fromEnv = LocalQueryScope.Resolve(root.Path, configured, environment: EnvironmentValue);
        Assert.Equal("http://env.test/", fromEnv.Origin); Assert.Equal(envCache, fromEnv.QueryRoot);
        var explicitScope = LocalQueryScope.Resolve(root.Path, configured, "HTTP://OVERRIDE.TEST:80/", Path.Combine(root.Path, "override"), EnvironmentValue);
        Assert.Equal("http://override.test/", explicitScope.Origin); Assert.NotEqual(fromEnv.DirectoryPath, explicitScope.DirectoryPath);
        Assert.NotEqual(Scope(root.Path, "http://a.test/").DirectoryPath, Scope(root.Path, "http://b.test/").DirectoryPath);
        Assert.NotEqual(Scope(root.Path, null).DirectoryPath, Scope(root.Path).DirectoryPath);
        Assert.Equal(new LocalClientSettingsStore(root.Path).Scope(Origin), Scope(root.Path).DirectoryPath);
    }
    [Fact]
    public async Task CliQueryIsImmediatelyReadableByDesktopDespiteDetailPageSizeAndQueryTextDifferences()
    {
        using var root = new TestDirectory(); using var routes = new Routes(); using var output = new StringWriter(); using var error = new StringWriter();
        var cli = new CliApplication(output, error, () => new HttpClient(routes, false), Env(root.Path), VndbClientTests.FastOptions, new(Origin));
        Assert.Equal(0, await cli.RunAsync(["vn", "get", "v17", "--source", "vndb", "--json"], Ct));
        using var desktop = new DesktopServices(root.Path, new HttpClient(routes, false), VndbClientTests.FastOptions); await desktop.ConfigureAsync(new(Origin), null, false, Ct);
        routes.Offline = true; var result = await desktop.Library.QueryAsync("v17", "cache", Ct);
        Assert.Equal("client_cache", result.Source); Assert.Equal("v17", Assert.Single(result.Items).VndbId);
        Assert.Equal(Scope(root.Path).DirectoryPath, desktop.QueryDirectory); Assert.Single(Directory.GetFiles(Path.Combine(desktop.QueryDirectory, "pending")));
    }
    [Fact]
    public async Task DesktopDirectQueryCanBeSubmittedByCliFromTheSameScope()
    {
        using var root = new TestDirectory(); using var routes = new Routes();
        using var desktop = new DesktopServices(root.Path, new HttpClient(routes, false), VndbClientTests.FastOptions); await desktop.ConfigureAsync(new(Origin), null, false, Ct);
        await desktop.Library.QueryAsync("v17", "vndb", Ct);
        using var output = new StringWriter(); using var error = new StringWriter();
        var cli = new CliApplication(output, error, () => new HttpClient(routes, false), n => n == "VN_LIBRARY_KEY" ? "scope-test-key" : Env(root.Path)(n), VndbClientTests.FastOptions, new(Origin));
        Assert.Equal(0, await cli.RunAsync(["sync", "--json"], Ct)); Assert.Equal(1, routes.Writes);
        Assert.False((await desktop.Library.QueryAsync("v17", "cache", Ct)).PendingImport);
    }
    [Fact]
    public async Task ServerSwitchAndOfflineScopesCannotReadOrSubmitEachOthersQueryData()
    {
        using var root = new TestDirectory(); using var routes = new Routes(); using var http = new HttpClient(routes);
        var a = Scope(root.Path); var b = Scope(root.Path, "http://b.test/"); var offline = Scope(root.Path, null);
        await new JsonSnapshotStore(a.DirectoryPath, a.Origin).SaveAsync(ClientQuery.FromText("v17"), Response(), Ct);
        Assert.Null(await new JsonSnapshotStore(b.DirectoryPath, b.Origin).ReadAsync(ClientQuery.FromText("v17"), Ct));
        Assert.Null(await new JsonSnapshotStore(offline.DirectoryPath).ReadAsync(ClientQuery.FromText("v17"), Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => new PendingMetadataSync(new WebLibraryClient(http, new("http://b.test/"), "scope-test-key", TimeSpan.FromSeconds(5)), "http://b.test/", a.DirectoryPath).SubmitAsync(Ct));
        Assert.Equal(0, routes.Writes);
    }
    [Fact]
    public async Task QueryOverrideDoesNotMoveIdentityInstallationsProfilesOrShelfCredentials()
    {
        using var root = new TestDirectory(); using var cache = new TestDirectory(); using var routes = new Routes();
        using var desktop = new DesktopServices(root.Path, new HttpClient(routes), VndbClientTests.FastOptions); await desktop.InitializeAsync(Ct);
        var device = desktop.Identity; await desktop.ConfigureAsync(new(Origin, CacheDirectory: cache.Path), null, true, Ct);
        await desktop.Library.QueryAsync("v17", "vndb", Ct);
        Assert.Equal(Scope(root.Path, Origin, cache.Path).DirectoryPath, desktop.QueryDirectory); Assert.Equal(new LocalClientSettingsStore(root.Path).Scope(Origin), desktop.StateDirectory);
        Assert.True(File.Exists(Path.Combine(root.Path, "device.json"))); Assert.False(File.Exists(Path.Combine(cache.Path, "device.json")));
        Assert.Equal(device, desktop.Identity); Assert.Equal(cache.Path, ClientSettings.Load(ClientSettings.DefaultFile(root.Path)).CacheDirectory);
    }
    private static async Task<string> Legacy(string root, string? origin, bool marker = true)
    {
        var legacy = Path.Combine(root, "snapshots"); await new JsonSnapshotStore(legacy, origin).SaveAsync(ClientQuery.FromText("v17"), Response(), Ct);
        if (!marker) File.Delete(Path.Combine(legacy, "query-scope.json")); return legacy;
    }
    [Fact]
    public async Task ProvenMigrationRetainsExactBytesAndUnknownPendingNeverEntersCurrentQueue()
    {
        using var root = new TestDirectory(); var legacy = await Legacy(root.Path, Origin); var scope = Scope(root.Path); var migration = new QueryDataMigration(scope);
        var unknown = Path.Combine(root.Path, "cache", "pending", "unknown.json"); await JsonSnapshotStore.AtomicWriteAsync(unknown, new MetadataBatch(Response().Items, DateTimeOffset.UnixEpoch, "unknown"), false, Ct);
        var original = Directory.GetFiles(legacy, "*.json", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var preview = await migration.PreviewAsync(ct: Ct); Assert.Equal(2, preview.Files.Count); Assert.Single(preview.Review);
        await migration.ApplyAsync(preview, LocalArchiveFormat.Hash(preview), Ct); await migration.ApplyAsync(preview, LocalArchiveFormat.Hash(preview), Ct);
        foreach (var file in preview.Files) Assert.Equal(original[file.Source], await File.ReadAllBytesAsync(Path.Combine(scope.DirectoryPath, file.RelativeTarget), Ct));
        foreach (var file in original) Assert.Equal(file.Value, await File.ReadAllBytesAsync(file.Key, Ct));
        Assert.False(File.Exists(Path.Combine(scope.DirectoryPath, "pending", "unknown.json"))); Assert.True(File.Exists(unknown));
        Assert.Single(Directory.GetFiles(Path.Combine(scope.DataRoot, "query-migrations"), "*.completed.json"));
        Assert.Equal("client_cache", (await new JsonSnapshotStore(scope.DirectoryPath, Origin).ReadAsync(ClientQuery.FromText("v17", 20), Ct))!.Source);
    }
    [Fact]
    public async Task LegacyWithoutMarkerCanOnlyBeAdoptedWithMatchingServerAndRawHashReceipt()
    {
        using var root = new TestDirectory(); using var db = new TestDirectory(); await using var app = new Factory(db.Path); using var http = app.CreateClient();
        var origin = http.BaseAddress!.AbsoluteUri; var legacy = await Legacy(root.Path, origin);
        var api = new WebLibraryClient(http, http.BaseAddress!, "scope-test-key", TimeSpan.FromSeconds(10)); await new PendingMetadataSync(api, origin, legacy).SubmitAsync(Ct);
        var cachedFile = Assert.Single(Directory.GetFiles(Path.Combine(legacy, "cache")));
        var oldQuery = ClientQuery.FromText("v17");
        await JsonSnapshotStore.AtomicWriteAsync(cachedFile, new { query = oldQuery, response = Response() }, true, Ct);
        var oldName = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(oldQuery, VndbJson.Options))) + ".json";
        File.Move(cachedFile, Path.Combine(legacy, "cache", oldName));
        File.Delete(Path.Combine(legacy, "query-scope.json")); var scope = Scope(root.Path, origin); var migration = new QueryDataMigration(scope);
        var preview = await migration.PreviewAsync(ct: Ct); Assert.Equal(4, preview.Files.Count); Assert.Empty(preview.Review);
        Assert.Contains(preview.Files, f => f.Proof == "unique_batch_receipt" && Path.GetFileName(f.RelativeTarget) != oldName);
        await migration.ApplyAsync(preview, LocalArchiveFormat.Hash(preview), Ct); Assert.False((await new JsonSnapshotStore(scope.DirectoryPath, origin).ReadAsync(ClientQuery.FromText("v17"), Ct))!.PendingImport);
        var pending = Assert.Single(Directory.GetFiles(Path.Combine(legacy, "pending"))); await File.AppendAllTextAsync(pending, " ", Ct);
        var changed = await migration.PreviewAsync(ct: Ct); Assert.Empty(changed.Files); Assert.Equal(4, changed.Review.Count);
    }
    [Theory]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("approval")]
    public async Task MigrationDriftAndCollisionsNeverOverwriteEitherVersion(string change)
    {
        using var root = new TestDirectory(); await Legacy(root.Path, Origin); var scope = Scope(root.Path); var migration = new QueryDataMigration(scope); var preview = await migration.PreviewAsync(ct: Ct);
        var file = preview.Files[0]; var bytes = await File.ReadAllBytesAsync(file.Source, Ct); var target = Path.Combine(scope.DirectoryPath, file.RelativeTarget);
        if (change == "source") await File.AppendAllTextAsync(file.Source, "\n", Ct);
        if (change == "target") { Directory.CreateDirectory(Path.GetDirectoryName(target)!); await File.WriteAllTextAsync(target, "different", Ct); }
        if (change == "approval") await Assert.ThrowsAsync<ArgumentException>(() => migration.ApplyAsync(preview, "wrong", Ct));
        else await Assert.ThrowsAsync<InvalidDataException>(() => migration.ApplyAsync(preview, LocalArchiveFormat.Hash(preview), Ct));
        if (change == "target") Assert.Equal("different", await File.ReadAllTextAsync(target, Ct)); else Assert.False(File.Exists(target));
        Assert.True(File.Exists(file.Source)); if (change != "source") Assert.Equal(bytes, await File.ReadAllBytesAsync(file.Source, Ct));
    }
    [Fact]
    public async Task ForeignLegacyScopeRemainsIsolatedAndHistoricalServerCacheIsReadableWithoutRewriting()
    {
        using var root = new TestDirectory(); var legacy = await Legacy(root.Path, "http://other.test/"); var migration = new QueryDataMigration(Scope(root.Path));
        var preview = await migration.PreviewAsync(ct: Ct); Assert.Empty(preview.Files); Assert.All(preview.Review, f => Assert.Equal("foreign_scope", f.Reason));
        var scope = Scope(root.Path); var query = ClientQuery.FromText("v17");
        await new JsonSnapshotStore(scope.DirectoryPath, Origin).SaveAsync(query, Response(), Ct);
        File.Delete(Path.Combine(scope.DirectoryPath, "query-scope.json")); var bytes = File.ReadAllBytes(Assert.Single(Directory.GetFiles(Path.Combine(scope.DirectoryPath, "cache"))));
        Assert.NotNull(await new JsonSnapshotStore(scope.DirectoryPath, Origin).ReadAsync(ClientQuery.FromText("v17", 20), Ct));
        Assert.Equal(bytes, File.ReadAllBytes(Assert.Single(Directory.GetFiles(Path.Combine(scope.DirectoryPath, "cache")))));
        Assert.True(Directory.Exists(legacy));
    }
    [Fact]
    public async Task UncertainMetadataCannotBeReboundToAReplacementServiceInstance()
    {
        using var root = new TestDirectory(); using var routes = new Routes { LoseReply = true }; using var http = new HttpClient(routes);
        var scope = Scope(root.Path); await new JsonSnapshotStore(scope.DirectoryPath, Origin).SaveAsync(ClientQuery.FromText("v17"), Response(), Ct);
        var sync = new PendingMetadataSync(new WebLibraryClient(http, new(Origin), "scope-test-key", TimeSpan.FromSeconds(5)), Origin, scope.DirectoryPath);
        await Assert.ThrowsAsync<WebUnavailableException>(() => sync.SubmitAsync(Ct)); routes.Instance = "replacement-instance";
        await Assert.ThrowsAsync<InvalidDataException>(() => sync.SubmitAsync(Ct)); Assert.Equal(1, routes.Writes);
        routes.Instance = "test-instance"; Assert.Single(await sync.SubmitAsync(Ct)); Assert.Equal(2, routes.Writes);
    }
    [Fact]
    public async Task TwoProcessesCanSaveDifferentQueriesAndMigrateTheSameFilesWithoutLoss()
    {
        using var root = new TestDirectory(); await GovernanceSyncTests.WorkersAsync(root.Path, "query", "unused");
        var scope = Scope(root.Path); Assert.Equal(2, Directory.GetFiles(Path.Combine(scope.DirectoryPath, "cache")).Length); Assert.Equal(2, Directory.GetFiles(Path.Combine(scope.DirectoryPath, "pending")).Length);
        using var migrate = new TestDirectory(); await Legacy(migrate.Path, Origin); await GovernanceSyncTests.WorkersAsync(migrate.Path, "query-migrate", "unused");
        Assert.Single(Directory.GetFiles(Path.Combine(Scope(migrate.Path).DirectoryPath, "pending"))); Assert.Equal(2, Directory.GetFiles(Path.Combine(migrate.Path, "query-migrations"), "*.completed.json").Length);
    }
    [Fact]
    public async Task CliDiagnosticsAndInspectUseTheSameEnvironmentOverrideAsQueries()
    {
        using var root = new TestDirectory(); using var cache = new TestDirectory(); using var game = new TestDirectory(); using var routes = new Routes();
        using var output = new StringWriter(); using var error = new StringWriter();
        string? EnvironmentValue(string n) => n == "VN_LIBRARY_CACHE" ? cache.Path : Env(root.Path)(n);
        var cli = new CliApplication(output, error, () => new HttpClient(routes, false), EnvironmentValue, VndbClientTests.FastOptions, new(Origin));
        Assert.Equal(0, await cli.RunAsync(["query-data", "status", "--json"], Ct)); var status = JsonSerializer.Deserialize<QueryScopeStatus>(output.ToString(), VndbJson.Options)!;
        Assert.Equal(Scope(root.Path, Origin, cache.Path).DirectoryPath, status.DirectoryPath); output.GetStringBuilder().Clear();
        Assert.Equal(0, await cli.RunAsync(["organize", "inspect", game.Path, "--query", "Synthetic", "--source", "vndb", "--json"], Ct));
        Assert.Single(Directory.GetFiles(Path.Combine(status.DirectoryPath, "cache"))); Assert.False(Directory.Exists(Path.Combine(root.Path, "snapshots")));
        var vm = new QueryDataFeatureViewModel(() => Scope(root.Path), f => f(Ct), _ => { }); vm.Approved = true; Assert.False(vm.ApplyCommand.CanExecute(null));
        await vm.RefreshCommand.ExecuteAsync(null); Assert.Contains(Origin, vm.Summary); vm.Invalidate(); Assert.False(vm.Approved);
    }
    [Fact]
    public async Task OverrideCanExplicitlyAdoptThePriorDefaultServerScopeWithoutMovingPrivateState()
    {
        using var root = new TestDirectory(); using var cache = new TestDirectory(); var old = Scope(root.Path);
        await new JsonSnapshotStore(old.DirectoryPath, Origin).SaveAsync(ClientQuery.FromText("v17"), Response(), Ct);
        File.Delete(Path.Combine(old.DirectoryPath, "query-scope.json"));
        var identity = await LocalDeviceIdentity.LoadOrCreateAsync(root.Path, Ct); var profile = new LocalClientSettingsStore(root.Path);
        await profile.SaveAsync(new(Origin, CacheDirectory: cache.Path), Ct);
        var current = Scope(root.Path, Origin, cache.Path); var migration = new QueryDataMigration(current); var preview = await migration.PreviewAsync(ct: Ct);
        Assert.Equal(2, preview.Files.Count); Assert.All(preview.Files, f => Assert.Equal("scope", f.Proof));
        await migration.ApplyAsync(preview, LocalArchiveFormat.Hash(preview), Ct);
        Assert.NotNull(await new JsonSnapshotStore(current.DirectoryPath, Origin).ReadAsync(ClientQuery.FromText("v17"), Ct));
        Assert.Equal(identity, await LocalDeviceIdentity.LoadOrCreateAsync(root.Path, Ct)); Assert.False(File.Exists(Path.Combine(cache.Path, "device.json")));
        Assert.Single(Directory.GetFiles(Path.Combine(old.DirectoryPath, "pending")));
    }
    [Fact]
    public async Task DefaultConfigurationIsSharedAndCorruptOrUnboundLegacyDataIsNeverReset()
    {
        using var root = new TestDirectory();
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(root.Path, "client.json"), new ClientSettings(Origin, Path.Combine(root.Path, "query")), false, Ct);
        var store = new LocalClientSettingsStore(root.Path); var legacySettings = await store.LoadAsync(Ct);
        Assert.Equal(ClientSettings.Load(ClientSettings.DefaultFile(root.Path)).Server, legacySettings.Server);
        await store.SaveAsync(legacySettings with { Server = "http://new.test/" }, Ct);
        Assert.Equal("http://new.test/", ClientSettings.Load(ClientSettings.DefaultFile(root.Path)).Server);
        var unknown = await Legacy(root.Path, Origin, marker: false); var migration = new QueryDataMigration(Scope(root.Path));
        var preview = await migration.PreviewAsync(ct: Ct); Assert.Empty(preview.Files); Assert.Equal(2, preview.Review.Count);
        await Assert.ThrowsAsync<InvalidDataException>(() => new JsonSnapshotStore(unknown, Origin).ReadAsync(ClientQuery.FromText("v17"), Ct));
        var file = Path.Combine(unknown, "cache", "broken.json"); await File.WriteAllTextAsync(file, "{broken", Ct);
        var broken = await migration.PreviewAsync(ct: Ct); Assert.Contains(broken.Review, r => r.Reason == "invalid_json_preserved"); Assert.Equal("{broken", File.ReadAllText(file));
    }
    [Fact]
    public async Task AnOfflineSnapshotCannotBeAcknowledgedByAnotherServersReceipt()
    {
        using var root = new TestDirectory(); var scope = Scope(root.Path, null); var store = new JsonSnapshotStore(scope.DirectoryPath);
        await store.SaveAsync(ClientQuery.FromText("v17"), Response(), Ct);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(scope.DirectoryPath, "pending"))); var bytes = await File.ReadAllBytesAsync(file, Ct);
        var batch = JsonSerializer.Deserialize<MetadataBatch>(bytes, VndbJson.Options)!;
        var accepted = new ImportBatchReceipt("import-test", new string('a', 64), batch.BatchId, "queued", true, 1, 0, [], DateTimeOffset.UtcNow, "test-instance");
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(scope.DirectoryPath, "receipts", batch.BatchId + ".json"),
            new ClientBatchReceipt(batch.BatchId, accepted.PayloadSha256, Origin, accepted, DateTimeOffset.UtcNow, Convert.ToHexStringLower(SHA256.HashData(bytes))), false, Ct);
        Assert.True((await store.ReadAsync(ClientQuery.FromText("v17"), Ct))!.PendingImport);
    }
}
