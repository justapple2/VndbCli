using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.Services;
using Vndb.Desktop.ViewModels;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class DesktopStartupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Origin = "http://startup.test/";

    [Fact]
    public async Task AggregateShelfOverEightMiBSurvivesRestartAndExactPlayReads()
    {
        using var dir = new TestDirectory();
        var description = new string('x', 8192);
        var items = Enumerable.Range(1, 1200).Select(n =>
        {
            var item = M5Tests.Item(n);
            return item with { Metadata = item.Metadata! with { Novel = item.Metadata.Novel! with { Description = description } } };
        }).ToArray();
        var now = DateTimeOffset.UtcNow;
        PersonalPlayView play = new("play-one", new("Synthetic", "v1", "completed", null, null, null, null, null, null, null, null), 1, now, now, ItemId: "item-1");
        var remote = new LibraryReadOperations((page, _) => Task.FromResult(new ItemPage(items.Skip((page - 1) * 100).Take(100).ToArray(), page, 100, items.Length, page * 100 < items.Length)),
            (_, _) => Task.FromResult<IReadOnlyList<PersonalPlayView>>([play]), (_, _) => Task.FromResult(new ItemPage([], 1, 100, 0, false)));
        var online = new LocalShelfReader(new(dir.Path), dir.Path, Origin, remote);
        Assert.Equal(1200, (await online.ShelfAsync(Ct)).Snapshot.Items.Count);
        var file = Path.Combine(dir.Path, "shelf.json");
        Assert.InRange(new FileInfo(file).Length, 8 * 1024 * 1024 + 1, 64 * 1024 * 1024);
        Assert.Single(await online.PlaysAsync("item-1", Ct)); // Uses the same aggregate limit, not query JSON's 8 MiB.
        var bytes = await File.ReadAllBytesAsync(file, Ct);
        var offline = new LocalShelfReader(new(dir.Path), dir.Path, Origin);
        Assert.Equal(1200, (await offline.ShelfAsync(Ct)).Snapshot.Items.Count);
        Assert.Equal("play-one", Assert.Single(await offline.PlaysAsync("item-1", Ct)).Id);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file, Ct));
    }

    [Fact]
    public async Task ShelfDoesNotDuplicateFilesystemAuthorityAndOverlaysCurrentInstallations()
    {
        using var dir = new TestDirectory(); var now = DateTimeOffset.UtcNow;
        var local = new LocalLibraryStore(dir.Path);
        LocalInstallationView installation = new(new("installation", new("device", Path.Combine(dir.Path, "synthetic-game"), "Synthetic"), "present", now, 1, now, now),
            [new("component", "installation", new("item-17", "."), 1, now, now)]);
        await local.SaveAsync(new([installation], []), Ct);
        var remote = new LibraryReadOperations((_, _) => Task.FromResult(new ItemPage([M5Tests.Item()], 1, 100, 1, false)),
            (_, _) => Task.FromResult<IReadOnlyList<PersonalPlayView>>([]));
        Assert.Single((await new LocalShelfReader(local, dir.Path, Origin, remote).ShelfAsync(Ct)).Snapshot.Installations);
        var snapshot = JsonSerializer.Deserialize<ShelfSnapshot>(await File.ReadAllTextAsync(Path.Combine(dir.Path, "shelf.json"), Ct), VndbJson.Options)!;
        Assert.Empty(snapshot.Installations); Assert.Null(snapshot.Packages);
        await local.UpdateAsync(value => value with { Installations = [] }, Ct);
        Assert.Empty((await new LocalShelfReader(local, dir.Path, Origin).ShelfAsync(Ct)).Snapshot.Installations);
    }

    [Fact]
    public async Task OversizedAggregateStillRejectsBeforeNetworkAndPreservesOriginal()
    {
        using var dir = new TestDirectory(); var file = Path.Combine(dir.Path, "shelf.json");
        await using (var stream = File.Create(file)) stream.SetLength(64L * 1024 * 1024 + 1);
        var calls = 0;
        var remote = new LibraryReadOperations((_, _) => { calls++; throw new InvalidOperationException(); }, (_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalShelfReader(new(dir.Path), dir.Path, Origin, remote).ShelfAsync(Ct));
        Assert.Equal(0, calls); Assert.Equal(64L * 1024 * 1024 + 1, new FileInfo(file).Length);
    }

    [Fact]
    public async Task InvalidCacheIsAVisibleRecoverableFailureAndRefreshCanBeRetried()
    {
        using var dir = new TestDirectory(); var library = new Library { Failure = new InvalidDataException("Synthetic invalid cache") };
        var vm = new MainViewModel(library, new(dir.Path), new Dialogs());
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Contains("Synthetic invalid cache", vm.Status); Assert.False(vm.Busy);
        library.Failure = null; await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Single(vm.Shelf.Books); Assert.DoesNotContain("未完成", vm.Status); Assert.False(vm.Busy);
    }

    [Fact]
    public async Task StartupStopsAfterRefreshFailureWithoutStartingSessionsOrReplacingError()
    {
        using var dir = new TestDirectory(); using var services = new DesktopServices(dir.Path);
        var library = new Library { Failure = new InvalidDataException("Synthetic shelf failure") };
        var vm = new MainViewModel(library, services.Launcher, new Dialogs(), services); var initialized = false;
        Assert.False(await vm.StartAsync(() => initialized = true));
        Assert.True(initialized); Assert.Equal(1, library.Reads); Assert.Null(services.Sessions);
        Assert.Contains("Synthetic shelf failure", vm.Status); Assert.False(vm.Busy);
    }

    [Fact]
    public async Task StartupStopsOnIncompatibleLocalJsonAndKeepsItUnchanged()
    {
        using var dir = new TestDirectory();
        var file = Path.Combine(dir.Path, "local-library.json");
        await JsonSnapshotStore.AtomicWriteAsync(file, new LocalLibrary([], [], FormatVersion: 99), false, Ct);
        var before = await File.ReadAllBytesAsync(file, Ct);
        using var services = new DesktopServices(dir.Path); var library = new Library();
        var vm = new MainViewModel(library, services.Launcher, new Dialogs(), services); var initialized = false;
        Assert.False(await vm.StartAsync(() => initialized = true));
        Assert.False(initialized); Assert.Equal(0, library.Reads); Assert.Null(services.Sessions);
        Assert.Contains("格式不兼容", vm.Status); Assert.False(vm.Busy);
        Assert.Equal(before, await File.ReadAllBytesAsync(file, Ct));
    }

    [Fact]
    public async Task ConfiguredStartupUsesTheHttpAdapterAndRealLargeShelfCache()
    {
        using var dir = new TestDirectory();
        await new LocalClientSettingsStore(dir.Path).SaveAsync(new(Server: Origin), Ct);
        using var handler = new HttpLibrary(); using var http = new HttpClient(handler);
        using var services = new DesktopServices(dir.Path, http);
        var vm = new MainViewModel(services.Library, services.Launcher, new Dialogs(), services);
        try
        {
            Assert.True(await vm.StartAsync(() => { }));
            Assert.Equal(60, vm.Shelf.Books.Count); Assert.Contains("1200", vm.Status); Assert.NotNull(services.Sessions);
            Assert.True(new FileInfo(Path.Combine(services.StateDirectory, "shelf.json")).Length > 8 * 1024 * 1024);
        }
        finally { if (services.ActivityHost is { } host) await host.DisposeAsync(); }
    }

    [Fact]
    public async Task FailedSessionRecoveryReleasesTheLeaseAndDoesNotPublishPartialServices()
    {
        using var dir = new TestDirectory(); var file = Path.Combine(dir.Path, "active-session.json");
        await File.WriteAllTextAsync(file, "{}", Ct);
        using var services = new DesktopServices(dir.Path);
        await Assert.ThrowsAsync<InvalidDataException>(() => services.StartSessionsAsync(Ct));
        Assert.Null(services.Sessions); Assert.Null(services.ActivityHost);
        using var lease = new FileStream(Path.Combine(dir.Path, "session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal("{}", await File.ReadAllTextAsync(file, Ct));
    }

    private sealed class HttpLibrary : HttpMessageHandler
    {
        private readonly string _description = new('x', 8192);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Get) throw new InvalidOperationException("No synthetic writes accepted.");
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            object response = request.RequestUri.AbsolutePath switch
            {
                "/api/v1/info" => new ServiceInfo("test-instance", [1], "test", 1, false, [], [1], true),
                "/api/v1/items" when query["deleted"] == "true" => new ItemPage([], 1, 100, 0, false),
                "/api/v1/items" => Page(int.Parse(query["page"]!)),
                "/api/v1/plays" => Array.Empty<PersonalPlayView>(),
                _ => throw new HttpRequestException("No synthetic background network available.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response, options: VndbJson.Options) });
        }
        private ItemPage Page(int page) => new(Enumerable.Range((page - 1) * 100 + 1, 100).Select(n =>
        {
            var item = M5Tests.Item(n);
            return item with { Metadata = item.Metadata! with { Novel = item.Metadata.Novel! with { Description = _description } } };
        }).ToArray(), page, 100, 1200, page < 12);
    }

    private sealed class Library : IDesktopLibrary
    {
        public Exception? Failure { get; set; }
        public int Reads { get; private set; }
        public Task<ShelfResult> ShelfAsync(CancellationToken ct)
        {
            Reads++; if (Failure is { } failure) throw failure;
            return Task.FromResult(new ShelfResult(new(Origin, [M5Tests.Item()], [], [], DateTimeOffset.UtcNow), "Synthetic"));
        }
        public Task<MetadataResponse> QueryAsync(string text, string source, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PersonalPlayView>> PlaysAsync(string itemId, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Dialogs : IDesktopDialogs
    {
        public Task<string?> ExecutableAsync() => Task.FromResult<string?>(null);
        public Task<string?> FolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveJsonAsync(string name) => Task.FromResult<string?>(null);
    }
}
