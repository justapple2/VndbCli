using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vndb.Cli;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.Services;
using Vndb.Desktop.ViewModels;
using Vndb.Library;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class RegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicIdMappingPreservesPhysicalArchiveApprovalAndRestoration(bool syncBeforeArchive)
    {
        using var root = new TestDirectory(); using var db = new TestDirectory();
        var data = Folder(root.Path, "client"); var scope = Folder(root.Path, "scope"); var game = Folder(scope, "Synthetic"); var target = Folder(root.Path, "target");
        await File.WriteAllTextAsync(Path.Combine(game, "game.exe"), "synthetic, never executed", Ct);
        await using var app = new Factory(db.Path); using var initial = app.CreateClient();
        using var gate = new Gate(app.Server.CreateHandler()); using var http = new HttpClient(gate) { BaseAddress = initial.BaseAddress };
        var api = new WebLibraryClient(http, http.BaseAddress!, "registration-test-key", TimeSpan.FromSeconds(10));
        var device = await LocalDeviceIdentity.LoadOrCreateAsync(data, Ct); var local = new LocalLibraryStore(data);
        var registration = await Register(new(local, device), new(game, "Synthetic", [new(Selection())], Origin: http.BaseAddress!.AbsoluteUri));
        var workspace = new LocalOrganizationWorkspace(data, device);
        var plan = await workspace.PreviewAsync(registration.Installation, scope, target, "Synthetic studio", "合成作品", Ct);
        var queue = new LocalRegistrationSync(local);
        async Task Map()
        {
            var before = Assert.Single((await local.ReadAsync(Ct)).Installations);
            Assert.Equal(1, await queue.SyncAsync(api, http.BaseAddress!.AbsoluteUri, device.Id, Ct));
            var after = Assert.Single((await local.ReadAsync(Ct)).Installations);
            Assert.Equal(before.Installation, after.Installation); // Mapping a public alias is not a physical-container edit.
            Assert.Equal(before.Components[0].Revision + 1, after.Components[0].Revision);
            Assert.Equal(before.Components[0].Id, after.Components[0].Id);
            Assert.False(LocalInstallationRegistration.IsPending(after.Components[0].Details.ItemId));
        }
        if (syncBeforeArchive) await Map();
        var run = workspace.RunDirectory(plan.Id); var runner = workspace.Runner();
        Assert.Equal("archived", (await runner.ExecuteAsync(plan, LocalArchiveFormat.Hash(plan), run, Ct)).State);
        if (!syncBeforeArchive) await Map();
        await File.WriteAllTextAsync(Path.Combine(plan.DestinationPath, "save.dat"), "new save retained", Ct);
        var restore = await runner.PreviewRestoreAsync(run, Ct);
        Assert.Equal("restored", (await runner.RestoreAsync(restore, LocalArchiveFormat.Hash(restore), run, Ct)).State);
        Assert.Equal("synthetic, never executed", await File.ReadAllTextAsync(Path.Combine(game, "game.exe"), Ct));
        Assert.Equal("new save retained", await File.ReadAllTextAsync(Path.Combine(game, "save.dat"), Ct));
        Assert.False(Directory.Exists(plan.DestinationPath));
    }
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Folder(string root, string name) { var path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    private static async Task<RegistrationResult> Register(LocalInstallationRegistration service, InstallationRegistration request)
    { var preview = await service.PreviewAsync(request, Ct); return await service.CommitAsync(preview, LocalArchiveFormat.Hash(preview), Ct); }
    private static RegistrationSelection Selection(int id = 17) => new("合成作品 " + id, Metadata: MetadataImports.Snapshot(StorageTests.Entry("v" + id)), Company: "合成会社");
    private sealed class Factory(string root) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Library:DataDirectory", root)
            .UseSetting("Library:WriteKey", "registration-test-key").UseSetting("CatalogJobs:Enabled", "false");
    }
    private sealed class Gate(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool LoseResponse;
        public Func<Task>? AfterSubmit;
        public string? InstanceOverride;
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.RequestUri!.AbsolutePath.EndsWith("/info") && InstanceOverride is { } instance)
            {
                var info = await response.Content.ReadFromJsonAsync<ServiceInfo>(VndbJson.Options, ct);
                response.Content = JsonContent.Create(info! with { ServiceInstanceId = instance }, options: VndbJson.Options);
            }
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(ct));
            if (request.RequestUri.AbsolutePath.EndsWith("/activity/batches"))
            {
                if (AfterSubmit is not null) await AfterSubmit();
                if (LoseResponse) { LoseResponse = false; response.Dispose(); throw new HttpRequestException("synthetic lost response"); }
            }
            return response;
        }
    }
    private sealed class Dialogs : IDesktopDialogs
    {
        public Task<string?> ExecutableAsync() => Task.FromResult<string?>(null);
        public Task<string?> FolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveJsonAsync(string name) => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task PreviewThenRegistrationKeepsFilesAndSupportsLaunchConfigurationAndOrganization()
    {
        using var root = new TestDirectory(); var data = Folder(root.Path, "client"); var scope = Folder(root.Path, "scope"); var game = Folder(scope, "Synthetic♡"); var target = Folder(root.Path, "target");
        var exe = Path.Combine(game, "game.exe"); await File.WriteAllTextAsync(exe, "synthetic fixture, never executed", Ct);
        var device = await LocalDeviceIdentity.LoadOrCreateAsync(data, Ct); var store = new LocalLibraryStore(data); var service = new LocalInstallationRegistration(store, device);
        var request = new InstallationRegistration(game, "合成ゲーム", [new(Selection())], "合成会社");
        var preview = await service.PreviewAsync(request, Ct);
        Assert.False(File.Exists(store.FilePath)); Assert.False(Directory.Exists(Path.Combine(data, "local-registrations")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CommitAsync(preview, new string('0', 64), Ct));
        var result = await service.CommitAsync(preview, LocalArchiveFormat.Hash(preview), Ct);
        var item = Assert.Single(result.Items); var installation = result.Installation; var component = Assert.Single(installation.Components);
        Assert.True(LocalInstallationRegistration.IsPending(item.Item.Id)); Assert.Equal(item.Item.Id, component.Details.ItemId);
        var launcher = new LocalLauncher(data);
        var profile = await new LaunchConfiguration(launcher).SaveAsync(new("合成ゲーム", exe, ["custom argument"], game, installation, component.Id, null), device.Id, Ct);
        Assert.Equal(exe, (await launcher.PreviewStartAsync(profile.Id, Ct)).FileName);
        var workspace = new LocalOrganizationWorkspace(data, device);
        var plan = await workspace.PlanAsync(installation.Installation.Id, null, scope, target, "合成会社", "合成ゲーム♡", Ct);
        Assert.Equal(game, plan.SourcePath); Assert.DoesNotContain('♡', Path.GetFileName(plan.DestinationPath));
        var server = new LocalOrganizationServer(store, launcher, device.Id);
        Assert.Equal("planned", (await server.PlanArchiveAsync(plan, Ct)).State);
        Assert.True(File.Exists(exe)); Assert.False(Directory.Exists(plan.DestinationPath));
        Assert.Equal("synthetic fixture, never executed", await File.ReadAllTextAsync(exe, Ct));
        Assert.False(Directory.EnumerateFiles(data, "*.db", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task RepeatedRegistrationAndIncrementalBundleKeepIdsBytesAndCompleteContainer()
    {
        using var root = new TestDirectory(); var data = Folder(root.Path, "client"); var game = Folder(root.Path, "bundle"); Folder(game, "bonus");
        var device = await LocalDeviceIdentity.LoadOrCreateAsync(data, Ct); var store = new LocalLibraryStore(data); var service = new LocalInstallationRegistration(store, device);
        var request = new InstallationRegistration(game, "合成合集", [new(Selection())], "合成会社");
        var first = await Register(service, request); var before = await File.ReadAllBytesAsync(store.FilePath, Ct);
        var repeat = await Register(service, request); Assert.True(repeat.Reused); Assert.Equal(first.Installation.Installation, repeat.Installation.Installation);
        Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath, Ct));
        var added = await Register(service, request with { Components = [new(Selection(18), "bonus", "bonus", "特典")] });
        Assert.Equal(first.Installation.Installation.Id, added.Installation.Installation.Id); Assert.Equal(2, added.Installation.Components.Count);
        Assert.Equal(first.Installation.Components[0].Id, added.Installation.Components[0].Id);
        Assert.Equal(2, (await store.ReadAsync(Ct)).Items.Count);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PreviewAsync(new(Path.Combine(game, "bonus"), "拆分", [new(Selection(19))]), Ct));
        Assert.True(Directory.Exists(Path.Combine(game, "bonus")));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/outside")]
    [InlineData("C:/outside")]
    [InlineData("bonus//child")]
    [InlineData("missing")]
    public async Task InvalidOrMissingComponentsNeverRegister(string relative)
    {
        using var root = new TestDirectory(); var data = Folder(root.Path, "client"); var game = Folder(root.Path, "game");
        var device = await LocalDeviceIdentity.LoadOrCreateAsync(data, Ct); var store = new LocalLibraryStore(data);
        await Assert.ThrowsAsync<ArgumentException>(() => new LocalInstallationRegistration(store, device).PreviewAsync(new(game, "Synthetic", [new(Selection(), relative)]), Ct));
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task PreviewRejectsChangedLibraryAndReplacedDirectoryIdentity()
    {
        using var root = new TestDirectory(); var data = Folder(root.Path, "client"); var game = Folder(root.Path, "game");
        var device = await LocalDeviceIdentity.LoadOrCreateAsync(data, Ct); var store = new LocalLibraryStore(data); var service = new LocalInstallationRegistration(store, device);
        var request = new InstallationRegistration(game, "Synthetic", [new(Selection())]); var preview = await service.PreviewAsync(request, Ct);
        await store.UpdateAsync(local => local with { Items = [M5Tests.Item(19)] }, Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CommitAsync(preview, LocalArchiveFormat.Hash(preview), Ct));
        var current = await service.PreviewAsync(request, Ct);
        Directory.Move(game, Path.Combine(root.Path, "retained-game")); Directory.CreateDirectory(game);
        await Assert.ThrowsAsync<IOException>(() => service.CommitAsync(current, LocalArchiveFormat.Hash(current), Ct));
        Assert.Empty((await store.ReadAsync(Ct)).Installations); Assert.True(Directory.Exists(Path.Combine(root.Path, "retained-game")));
    }

    [Fact]
    public async Task RemovedForeignAmbiguousOrPrivateSelectionsRequireExplicitReview()
    {
        using var root = new TestDirectory(); var data = Folder(root.Path, "client"); var game = Folder(root.Path, "game");
        var device = await LocalDeviceIdentity.LoadOrCreateAsync(data, Ct); var store = new LocalLibraryStore(data); var service = new LocalInstallationRegistration(store, device);
        var now = DateTimeOffset.UtcNow;
        var foreign = new LocalInstallationView(new("foreign", new(Guid.NewGuid().ToString("D"), game, "Synthetic"), "present", now, 1, now, now), []);
        await store.SaveAsync(new([foreign], []), Ct);
        var request = new InstallationRegistration(game, "Synthetic", [new(Selection())]);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PreviewAsync(request, Ct));
        await store.UpdateAsync(x => x with { Installations = [foreign with { Installation = foreign.Installation with { Details = foreign.Installation.Details with { DeviceId = device.Id }, RemovedAt = now } }] }, Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PreviewAsync(request, Ct));
        var a = M5Tests.Item(); var b = a with { Item = a.Item with { Id = "another-item" } };
        await store.UpdateAsync(x => x with { Installations = [], Items = [a, b] }, Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewAsync(request, Ct));
        Assert.Equal(a.Item.Id, (await service.PreviewAsync(request with { Components = [new(new(a.Item.Details.Title, a, a.Metadata))] }, Ct)).Items[0].Item.Id);
        await store.UpdateAsync(x => x with { Items = [] }, Ct);
        var metadata = Selection().Metadata! with { Novel = Selection().Metadata!.Novel! with { Description = @"E:\PRIVATE_FIXTURE\Game" } };
        await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewAsync(request with { Components = [new(new("Synthetic", Metadata: metadata))] }, Ct));
    }

    [Fact]
    public async Task OfflineShelfIncludesNewRegistrationsWithoutResurrectingRemovedWebItems()
    {
        using var root = new TestDirectory(); var data = Folder(root.Path, "client"); var game = Folder(root.Path, "game"); var cache = Folder(root.Path, "cache");
        var device = await LocalDeviceIdentity.LoadOrCreateAsync(data, Ct); var local = new LocalLibraryStore(data);
        await local.SaveAsync(new([], [M5Tests.Item(18)]), Ct);
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(cache, "shelf.json"), new ShelfSnapshot("http://test.local/", [], [], [], DateTimeOffset.UtcNow), false, Ct);
        var result = await Register(new(local, device), new(game, "Synthetic", [new(Selection())], Origin: "http://test.local/"));
        var shelf = await new LocalShelfReader(local, cache, "http://test.local/").ShelfAsync(Ct);
        Assert.Equal(result.Items[0].Item.Id, Assert.Single(shelf.Snapshot.Items).Item.Id);
        Assert.DoesNotContain(shelf.Snapshot.Items, i => i.Item.Id == "item-18");
    }

    [Fact]
    public async Task ConcurrentConfirmationsCannotCreateTwoInstallationsOrLoseOtherItems()
    {
        using var root = new TestDirectory(); var data = Folder(root.Path, "client"); var game = Folder(root.Path, "game");
        var device = await LocalDeviceIdentity.LoadOrCreateAsync(data, Ct); var store = new LocalLibraryStore(data); var service = new LocalInstallationRegistration(store, device);
        await store.SaveAsync(new([], [M5Tests.Item(18)]), Ct);
        var preview = await service.PreviewAsync(new(game, "Synthetic", [new(Selection())]), Ct); var hash = LocalArchiveFormat.Hash(preview);
        async Task<bool> Confirm() { try { await new LocalInstallationRegistration(new(data), device).CommitAsync(preview, hash, Ct); return true; } catch (InvalidDataException) { return false; } }
        var outcomes = await Task.WhenAll(Confirm(), Confirm()); Assert.Single(outcomes, x => x);
        var local = await store.ReadAsync(Ct); Assert.Single(local.Installations); Assert.Equal(2, local.Items.Count); Assert.Contains(local.Items, i => i.Item.Id == "item-18");
    }

    [Fact]
    public async Task PublicRegistrationExportRefusesAllGameDirectoriesAndDoesNotOverwrite()
    {
        using var root = new TestDirectory(); var data = Folder(root.Path, "client"); var game = Folder(root.Path, "game"); var other = Folder(root.Path, "other-game"); var output = Folder(root.Path, "export");
        var device = await LocalDeviceIdentity.LoadOrCreateAsync(data, Ct); var local = new LocalLibraryStore(data); var service = new LocalInstallationRegistration(local, device);
        var first = await Register(service, new(game, "Synthetic", [new(Selection())]));
        await Register(service, new(other, "Synthetic other", [new(Selection(18))]));
        var queue = new LocalRegistrationSync(local); var id = first.Items[0].Item.Id;
        await Assert.ThrowsAsync<ArgumentException>(() => queue.ExportToDirectoryAsync([id], other, Ct));
        Assert.Equal(1, await queue.ExportToDirectoryAsync([id], output, Ct)); var file = Path.Combine(output, id + ".json"); var bytes = await File.ReadAllBytesAsync(file, Ct);
        await Assert.ThrowsAsync<IOException>(() => queue.ExportToDirectoryAsync([id], output, Ct)); Assert.Equal(bytes, await File.ReadAllBytesAsync(file, Ct));
        Assert.False(WirePrivacy.ContainsPrivateData(JsonSerializer.Deserialize<JsonElement>(bytes))); Assert.Empty(Directory.EnumerateFileSystemEntries(game));
    }

    [Fact]
    public async Task LostResponseRetriesSamePublicBatchAndMapsWithoutChangingInstallationOrLaunchIdentity()
    {
        using var root = new TestDirectory(); using var db = new TestDirectory(); var data = Folder(root.Path, "client"); var game = Folder(root.Path, "PRIVATE_GAME_DIRECTORY");
        await using var app = new Factory(db.Path); using var initial = app.CreateClient(); using var gate = new Gate(app.Server.CreateHandler()); using var http = new HttpClient(gate) { BaseAddress = initial.BaseAddress };
        var api = new WebLibraryClient(http, http.BaseAddress!, "registration-test-key", TimeSpan.FromSeconds(10));
        var device = await LocalDeviceIdentity.LoadOrCreateAsync(data, Ct); var local = new LocalLibraryStore(data);
        var registered = await Register(new(local, device), new(game, "Synthetic", [new(Selection())], Origin: http.BaseAddress!.AbsoluteUri));
        var item = Assert.Single(registered.Items); var queue = new LocalRegistrationSync(local);
        var shelfDirectory = Path.Combine(data, "synthetic-shelf");
        var cachedPlay = new PersonalPlayView("snapshot-play", new(item.Item.Details.Title, item.Item.Details.NovelId, "played", null, null, null, null, null, null, null, null), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ItemId: item.Item.Id);
        var shelfFile = Path.Combine(shelfDirectory, "shelf.json");
        await JsonSnapshotStore.AtomicWriteAsync(shelfFile, new ShelfSnapshot(http.BaseAddress!.AbsoluteUri, [item], [cachedPlay], [registered.Installation], DateTimeOffset.UtcNow), false, Ct);
        var shelfBytes = await File.ReadAllBytesAsync(shelfFile, Ct);
        var batch = await queue.ExportAsync(item.Item.Id, Ct); Assert.False(WirePrivacy.ContainsPrivateData(JsonSerializer.SerializeToElement(batch, VndbJson.Options)));
        gate.LoseResponse = true;
        await Assert.ThrowsAsync<WebUnavailableException>(() => queue.SyncAsync(api, http.BaseAddress!.AbsoluteUri, device.Id, Ct));
        Assert.Equal(item.Item.Id, Assert.Single((await local.ReadAsync(Ct)).Items).Item.Id);
        Assert.Equal(1, await queue.SyncAsync(api, http.BaseAddress!.AbsoluteUri, device.Id, Ct));
        Assert.Equal(0, await queue.SyncAsync(api, http.BaseAddress!.AbsoluteUri, device.Id, Ct));
        var current = await local.ReadAsync(Ct); var mapped = Assert.Single(current.Items); Assert.False(LocalInstallationRegistration.IsPending(mapped.Item.Id));
        Assert.Equal(registered.Installation.Installation.Id, current.Installations[0].Installation.Id);
        Assert.Equal(registered.Installation.Components[0].Id, current.Installations[0].Components[0].Id);
        Assert.Equal(game, current.Installations[0].Installation.Details.RootPath);
        Assert.Equal(mapped.Item.Id, current.Installations[0].Components[0].Details.ItemId);
        var cachedShelf = await new LocalShelfReader(local, shelfDirectory, http.BaseAddress!.AbsoluteUri).ShelfAsync(Ct);
        Assert.Equal(mapped.Item.Id, Assert.Single(Assert.Single(ShelfPlayResolver.Resolve(cachedShelf.Snapshot)).Plays).ItemId);
        Assert.Equal(shelfBytes, await File.ReadAllBytesAsync(shelfFile, Ct));
        Assert.Equal(1, await new PresenceSync(local).SubmitAsync(api, http.BaseAddress!.AbsoluteUri, device.Id, Ct));
        var store = app.Services.GetRequiredService<SqliteLibraryStore>(); Assert.Single(store.PersonalItems()); Assert.Empty(store.ItemPlays());
        Assert.Single(store.PublicPresence(), p => p.IsLocal); Assert.Empty(store.Installations());
        var submissions = gate.Bodies.Where(b => b.Contains("vn-library-activity")).ToArray(); Assert.Equal(2, submissions.Length); Assert.Equal(submissions[0], submissions[1]);
        Assert.All(gate.Bodies, body => { Assert.DoesNotContain("PRIVATE_GAME_DIRECTORY", body); Assert.DoesNotContain("relative_path", body); Assert.DoesNotContain("executable", body); });
    }

    [Fact]
    public async Task ExportImportThenAutomaticSyncAndOfflineSessionDoNotDuplicateItemsOrTime()
    {
        using var root = new TestDirectory(); using var db = new TestDirectory(); var data = Folder(root.Path, "client"); var game = Folder(root.Path, "game");
        await using var app = new Factory(db.Path); using var http = app.CreateClient(); var api = new WebLibraryClient(http, http.BaseAddress!, "registration-test-key", TimeSpan.FromSeconds(10));
        var device = await LocalDeviceIdentity.LoadOrCreateAsync(data, Ct); var local = new LocalLibraryStore(data);
        var registered = await Register(new(local, device), new(game, "Synthetic", [new(Selection())], Origin: http.BaseAddress!.AbsoluteUri));
        var item = registered.Items[0]; var activities = new ActivityOutbox(data);
        var target = await activities.CreateTargetAsync(device, item.Item.Details.Title, item.Metadata, item.Item.Id, ct: Ct, pendingItem: item.Item.Details);
        var now = DateTimeOffset.UtcNow;
        await activities.SaveSessionAsync(target.Id, new(Guid.NewGuid().ToString("D"), device.Id, target.PlayRef, null, null, now, now.AddSeconds(10), now.AddSeconds(10), 1, 10, "ended"), Ct);
        var queue = new LocalRegistrationSync(local); Assert.True((await api.SubmitActivityAsync(await queue.ExportAsync(item.Item.Id, Ct), Ct)).Complete);
        await new LocalClientSync(api, http.BaseAddress!.AbsoluteUri, Path.Combine(data, "snapshots"), activities, new(data), local, device.Id).SyncAsync(Ct);
        await new LocalClientSync(api, http.BaseAddress!.AbsoluteUri, Path.Combine(data, "snapshots"), activities, new(data), local, device.Id).SyncAsync(Ct);
        var sql = app.Services.GetRequiredService<SqliteLibraryStore>(); Assert.Single(sql.PersonalItems());
        var play = Assert.Single(sql.ItemPlays()); Assert.Equal(10, sql.PlayTime(play.Id).SessionSeconds);
        Assert.Single(sql.Sessions()); Assert.Empty(sql.Installations());
    }

    [Fact]
    public async Task RegistrationBindingRejectsAnotherOriginOrChangedServiceAfterUncertainResult()
    {
        using var root = new TestDirectory(); using var db = new TestDirectory(); var data = Folder(root.Path, "client"); var game = Folder(root.Path, "game");
        await using var app = new Factory(db.Path); using var initial = app.CreateClient(); using var gate = new Gate(app.Server.CreateHandler()) { LoseResponse = true }; using var http = new HttpClient(gate) { BaseAddress = initial.BaseAddress };
        var api = new WebLibraryClient(http, http.BaseAddress!, "registration-test-key", TimeSpan.FromSeconds(10));
        var device = await LocalDeviceIdentity.LoadOrCreateAsync(data, Ct); var local = new LocalLibraryStore(data);
        await Register(new(local, device), new(game, "Synthetic", [new(Selection())], Origin: http.BaseAddress!.AbsoluteUri)); var queue = new LocalRegistrationSync(local);
        await Assert.ThrowsAsync<InvalidDataException>(() => queue.SyncAsync(api, "http://other.test/", device.Id, Ct)); Assert.Empty(gate.Bodies);
        await Assert.ThrowsAsync<WebUnavailableException>(() => queue.SyncAsync(api, http.BaseAddress!.AbsoluteUri, device.Id, Ct));
        gate.InstanceOverride = "new-test-instance";
        await Assert.ThrowsAsync<InvalidDataException>(() => queue.SyncAsync(api, http.BaseAddress!.AbsoluteUri, device.Id, Ct)); Assert.Single(gate.Bodies);
        Assert.True(LocalInstallationRegistration.IsPending((await local.ReadAsync(Ct)).Items[0].Item.Id));
    }

    [Fact]
    public async Task ChangedIntentDuringSubmissionCannotAcknowledgeOrRebindLocalItems()
    {
        using var root = new TestDirectory(); using var db = new TestDirectory(); var data = Folder(root.Path, "client"); var game = Folder(root.Path, "game");
        await using var app = new Factory(db.Path); using var initial = app.CreateClient(); using var gate = new Gate(app.Server.CreateHandler()); using var http = new HttpClient(gate) { BaseAddress = initial.BaseAddress };
        var api = new WebLibraryClient(http, http.BaseAddress!, "registration-test-key", TimeSpan.FromSeconds(10));
        var device = await LocalDeviceIdentity.LoadOrCreateAsync(data, Ct); var local = new LocalLibraryStore(data);
        var result = await Register(new(local, device), new(game, "Synthetic", [new(Selection())], Origin: http.BaseAddress!.AbsoluteUri));
        var item = result.Items[0]; var intentFile = Path.Combine(data, "local-registrations", item.Item.Id, "intent.json");
        gate.AfterSubmit = async () => { var intent = JsonSerializer.Deserialize<LocalRegistrationIntent>(await File.ReadAllTextAsync(intentFile, Ct), VndbJson.Options)!; await JsonSnapshotStore.AtomicWriteAsync(intentFile, intent with { Origin = "http://changed.test/" }, true, Ct); };
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalRegistrationSync(local).SyncAsync(api, http.BaseAddress!.AbsoluteUri, device.Id, Ct));
        Assert.False(File.Exists(Path.Combine(data, "local-registrations", item.Item.Id, "receipt.json")));
        Assert.Equal(item.Item.Id, (await local.ReadAsync(Ct)).Items[0].Item.Id);
    }

    [Fact]
    public async Task CliPreviewIsReadOnlyAndConfirmationPersistsWithoutNetworkOrGameOutput()
    {
        using var root = new TestDirectory(); var data = Folder(root.Path, "client"); var game = Folder(root.Path, "game"); using var handler = new StubHandler("{}");
        using var output = new StringWriter(); using var error = new StringWriter();
        var cli = new CliApplication(output, error, () => new HttpClient(handler, false), _ => null, VndbClientTests.FastOptions, new());
        string[] args = ["organize", "register", "--identity-dir", data, "--directory", game, "--title", "合成未收录作品", "--json"];
        Assert.Equal(0, await cli.RunAsync(args, Ct)); Assert.False(File.Exists(Path.Combine(data, "local-library.json")));
        Assert.Equal(0, await cli.RunAsync([..args, "--confirm"], Ct)); Assert.Single((await new LocalLibraryStore(data).ReadAsync(Ct)).Installations);
        Assert.Equal(2, await cli.RunAsync([..args, "--output", Path.Combine(game, "forbidden.json")], Ct));
        var metadataFile = Path.Combine(root.Path, "selected.json");
        await JsonSnapshotStore.AtomicWriteAsync(metadataFile, new MetadataBatch([Selection().Metadata!], DateTimeOffset.UtcNow, Guid.NewGuid().ToString("D")), false, Ct);
        Assert.Equal(0, await cli.RunAsync(["organize", "register", "--identity-dir", data, "--directory", Folder(root.Path, "second-game"), "--title", "合成游戏", "--item-json", metadataFile, "--confirm", "--json"], Ct));
        Assert.Equal(2, (await new LocalLibraryStore(data).ReadAsync(Ct)).Installations.Count);
        Assert.Empty(Directory.EnumerateFileSystemEntries(game)); Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DesktopQueryRegistrationSurvivesRestartAndConnectsSelectedLaunchConfiguration()
    {
        using var root = new TestDirectory(); using var db = new TestDirectory(); var data = Folder(root.Path, "client"); var game = Folder(root.Path, "game");
        var exe = Path.Combine(game, "synthetic.exe"); await File.WriteAllTextAsync(exe, "not executable", Ct);
        await using var app = new Factory(db.Path); using var http = app.CreateClient(); app.Services.GetRequiredService<SqliteLibraryStore>().Save(StorageTests.Entry());
        using var desktop = new DesktopServices(data, http); await desktop.InitializeAsync(Ct); await desktop.ConfigureAsync(new(http.BaseAddress!.AbsoluteUri), "registration-test-key", false, Ct);
        var vm = new MainViewModel(desktop.Library, desktop.Launcher, new Dialogs(), desktop);
        vm.Discovery.Query = "v17"; vm.Discovery.QuerySource = "web"; await vm.Discovery.SearchCommand.ExecuteAsync(null);
        vm.RegisterBookCommand.Execute(Assert.Single(vm.Shelf.Books)); Assert.Equal(7, vm.Section);
        var form = vm.Registration; form.RootPath = game; form.AddComponentCommand.Execute(null);
        await form.PreviewCommand.ExecuteAsync(null); Assert.False(form.ConfirmCommand.CanExecute(null));
        form.Approved = true; Assert.True(form.ConfirmCommand.CanExecute(null));
        form.ContainerTitle += "変更"; Assert.False(form.Approved); Assert.False(form.ConfirmCommand.CanExecute(null));
        await form.PreviewCommand.ExecuteAsync(null); form.Approved = true; await form.ConfirmCommand.ExecuteAsync(null);
        Assert.NotNull(form.Result); Assert.True(vm.SelectedBook!.IsLocal); Assert.NotNull(vm.SelectedInstallation);
        vm.ConfigureBookCommand.Execute(vm.SelectedBook); vm.Executable = exe; await vm.SaveLaunchCommand.ExecuteAsync(null);
        Assert.Single(await desktop.Launcher.ReadAsync(Ct)); Assert.Equal(game, vm.WorkingDirectory);
        using var reopened = new DesktopServices(data); await reopened.InitializeAsync(Ct);
        Assert.Single((await reopened.Library.ShelfAsync(Ct)).Snapshot.Items); Assert.Single(await reopened.Launcher.ReadAsync(Ct));
        Assert.True(File.Exists(exe)); Assert.Empty(app.Services.GetRequiredService<SqliteLibraryStore>().PersonalItems());
    }
}
