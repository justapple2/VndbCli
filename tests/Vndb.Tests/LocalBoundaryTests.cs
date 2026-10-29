using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.Services;
using Vndb.Library;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class LocalBoundaryTests
{
    [Theory]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task UnstructuredGatewayFailureAllowsOfflineUseButTypedUpstreamFailureDoesNot(int status)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(StubHandler.Json("{}", (HttpStatusCode)status)));
        using var http = new HttpClient(handler); var web = new WebLibraryClient(http, new("http://example.local/"), null, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<WebUnavailableException>(() => web.StatisticsAsync(Ct));
    }
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Factory(string data) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Library:DataDirectory", data)
            .UseSetting("Library:WriteKey", "boundary-test-key").UseSetting("CatalogJobs:Enabled", "false");
    }
    private sealed class Capture(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<string> Writes { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null) Writes.Add(await request.Content.ReadAsStringAsync(ct));
            return await base.SendAsync(request, ct);
        }
    }
    [Fact]
    public async Task PresenceSyncIsBooleanOnlyDurableIdempotentAndRefusesLegacyLocationRequests()
    {
        using var dir = new TestDirectory(); using var local = new TestDirectory();
        await using var app = new Factory(dir.Path); using var initial = app.CreateClient();
        using var capture = new Capture(app.Server.CreateHandler()); using var http = new HttpClient(capture) { BaseAddress = initial.BaseAddress };
        var store = app.Services.GetRequiredService<SqliteLibraryStore>(); store.Save(StorageTests.Entry());
        var item = store.SaveItem(new(new("Example", "v17")), null, "item");
        var id = Guid.NewGuid().ToString("D"); var now = DateTimeOffset.UtcNow;
        var installation = new LocalInstallation("local-install", new(id, @"E:\PrivateCompany\PrivateTitle", "Example"), "present", now, 1, now, now);
        var component = new LocalInstallationComponent("local-component", installation.Id, new(item.Id, "internal-chapter"), 1, now, now);
        var library = new LocalLibraryStore(local.Path); await library.SaveAsync(new([new(installation, [component])], []), Ct);
        var web = new WebLibraryClient(http, http.BaseAddress!, "boundary-test-key", TimeSpan.FromSeconds(10));
        Assert.Equal(1, await library.SyncPresenceAsync(web, http.BaseAddress!.AbsoluteUri, id, Ct));
        Assert.Equal(0, await library.SyncPresenceAsync(web, http.BaseAddress!.AbsoluteUri, id, Ct));
        var body = Assert.Single(capture.Writes); Assert.DoesNotContain("Private", body); Assert.DoesNotContain("chapter", body); Assert.DoesNotContain("root_path", body);
        var publicJson = await http.GetStringAsync("/api/v1/presence", Ct); Assert.DoesNotContain("local-install", publicJson); Assert.DoesNotContain("locations", publicJson);
        Assert.True(Assert.Single(JsonSerializer.Deserialize<PublicPresence[]>(publicJson, VndbJson.Options)!).IsLocal);
        var batch = JsonSerializer.Deserialize<PresenceBatch>(body, VndbJson.Options)!;
        var first = await web.SubmitPresenceAsync(batch, Ct); var second = await web.SubmitPresenceAsync(batch, Ct); Assert.Equal(first, second);
        await Assert.ThrowsAsync<ClientApiException>(() => web.SubmitPresenceAsync(batch with { Id = Guid.NewGuid().ToString("D") }, Ct));
        var sent = Path.Combine(local.Path, "presence-sent", batch.Id + ".json"); Assert.True(File.Exists(sent)); Assert.False(File.Exists(Path.Combine(local.Path, "presence-pending.json")));
        var previousWrites = capture.Writes.Count;
        Assert.Null(typeof(WebLibraryClient).GetMethod("SubmitAuditAsync"));
        Assert.Equal(previousWrites, capture.Writes.Count);
        Assert.Empty(store.Installations()); Assert.Empty(store.Components()); Assert.Empty(store.ArchiveRuns());
        var openapi = await http.GetStringAsync("/openapi/v1.json", Ct);
        Assert.DoesNotContain("/api/v1/installations", openapi); Assert.DoesNotContain("LocalArchivePlan", openapi); Assert.DoesNotContain("root_path", openapi);
        foreach (var route in new[] { "/api/v1/installations", "/api/v1/archive-runs", "/api/archives", "/api/catalog/local", "/api/v1/legacy-locations" })
        { using var response = await http.GetAsync(route, Ct); Assert.Equal(HttpStatusCode.Gone, response.StatusCode); }
    }
    [Theory]
    [InlineData("{\"items\":[{\"item_id\":\"x\",\"is_local\":true,\"relative_path\":\"chapter\"}]}")]
    [InlineData("{\"details\":{\"title\":\"Boundary fixture\",\"notes\":\"E:\\\\Private\\\\Game\"}}")]
    [InlineData("{\"details\":{\"title\":\"Boundary fixture\",\"name\":\"C:/Private/Game\"}}")]
    [InlineData("{\"details\":{\"title\":\"Boundary fixture\",\"notes\":\"游戏目录为E:/Private/Game\"}}")]
    [InlineData("{\"details\":{\"title\":\"Boundary fixture\",\"notes\":\"E:/Private/Game\"}}", "application/vnd.test+json")]
    public async Task ServerRejectsLocationsBeforePersistingOrEchoingThem(string json, string contentType = "application/json")
    {
        using var dir = new TestDirectory(); await using var app = new Factory(dir.Path); using var http = app.CreateClient();
        http.DefaultRequestHeaders.Add("X-Library-Key", "boundary-test-key"); http.DefaultRequestHeaders.Add("X-Library-Request", "1");
        using var response = await http.PostAsync("/api/v1/items", new StringContent(json, Encoding.UTF8, contentType), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.DoesNotContain("Private", await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(app.Services.GetRequiredService<SqliteLibraryStore>().PresentItemIds());
        Assert.Empty(app.Services.GetRequiredService<SqliteLibraryStore>().PersonalItems());
    }
    [Fact]
    public async Task DocumentsMigrationPreservesIdentityProfilesPendingAndEncryptedCredentialAsJson()
    {
        using var old = new TestDirectory(); using var target = new TestDirectory();
        var identity = await LocalDeviceIdentity.LoadOrCreateAsync(old.Path, Ct);
        await File.WriteAllTextAsync(Path.Combine(old.Path, "launch-profiles.json"), "[]", Ct);
        await File.WriteAllTextAsync(Path.Combine(old.Path, "pending.json"), "{\"unconfirmed\":true}", Ct);
        await File.WriteAllBytesAsync(Path.Combine(old.Path, "credential.bin"), [1, 2, 3], Ct);
        await File.WriteAllTextAsync(Path.Combine(old.Path, "not-client-data.db"), "must not copy", Ct);
        await LocalDataRoot.MigrateAsync(old.Path, target.Path, Ct);
        Assert.Equal(identity, await LocalDeviceIdentity.LoadOrCreateAsync(target.Path, Ct));
        Assert.Equal("[]", await File.ReadAllTextAsync(Path.Combine(target.Path, "launch-profiles.json"), Ct));
        Assert.True(File.Exists(Path.Combine(target.Path, "pending.json"))); Assert.True(File.Exists(Path.Combine(old.Path, "pending.json")));
        Assert.False(File.Exists(Path.Combine(target.Path, "credential.bin"))); Assert.False(File.Exists(Path.Combine(target.Path, "not-client-data.db")));
        Assert.Contains("AQID", await File.ReadAllTextAsync(Path.Combine(target.Path, "credential.json"), Ct));
        await LocalDataRoot.MigrateAsync(old.Path, target.Path, Ct);
        Assert.EndsWith(Path.Combine("Documents", "VndbLocal"), LocalDataRoot.Default, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task OfflineShelfReadsLocalJsonAndSyntheticMoveRebasesProfilesWithoutHttpOrDatabase()
    {
        using var dir = new TestDirectory(); var root = Path.Combine(dir.Path, "client"); var scope = Path.Combine(dir.Path, "scope"); var target = Path.Combine(dir.Path, "target");
        var source = Path.Combine(scope, "old"); Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        var exe = Path.Combine(source, "synthetic.exe"); await File.WriteAllTextAsync(exe, "never executed", Ct);
        var identity = await LocalDeviceIdentity.LoadOrCreateAsync(root, Ct); var now = DateTimeOffset.UtcNow;
        var view = new LocalInstallationView(new("i", new(identity.Id, source, "Synthetic"), "present", now, 1, now, now), [new("c", "i", new("item-17", "."), 1, now, now)]);
        var library = new LocalLibraryStore(root); await library.SaveAsync(new([view], [M5Tests.Item()]), Ct);
        var launcher = new LocalLauncher(root); var profile = await launcher.SaveAsync("Synthetic", exe, [], source, "i", "c", ct: Ct);
        using var services = new DesktopServices(root); await services.InitializeAsync(Ct); var shelf = await services.Library.ShelfAsync(Ct);
        Assert.Single(shelf.Snapshot.Items); Assert.Equal(source, Assert.Single(shelf.Snapshot.Installations).Installation.Details.RootPath);
        var server = new LocalOrganizationServer(library, launcher, identity.Id); var runner = new OrganizationRunner(server, "local:" + identity.Id, identity.Id);
        var plan = OrganizationPlanner.Create(view, new(scope, target), "会社", "ゲーム", Ct); var run = Path.Combine(root, "archive-runs", plan.Id);
        Assert.Equal("archived", (await runner.ExecuteAsync(plan, LocalArchiveFormat.Hash(plan), run, Ct)).State);
        Assert.Equal(Path.Combine(plan.DestinationPath, "synthetic.exe"), (await launcher.ReadAsync(Ct)).Single().Executable);
        await File.WriteAllTextAsync(Path.Combine(plan.DestinationPath, "save.dat"), "new save", Ct);
        var restore = await runner.PreviewRestoreAsync(run, Ct); Assert.Equal("restored", (await runner.RestoreAsync(restore, LocalArchiveFormat.Hash(restore), run, Ct)).State);
        Assert.Equal(JsonSerializer.Serialize(profile, VndbJson.Options), JsonSerializer.Serialize((await launcher.ReadAsync(Ct)).Single(), VndbJson.Options)); Assert.True(File.Exists(Path.Combine(source, "save.dat")));
        Assert.Empty(Directory.EnumerateFiles(root, "*.db", SearchOption.AllDirectories));
    }
}
