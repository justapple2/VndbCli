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
using Vndb.Desktop.ViewModels;
using Vndb.Library;
using Vndb.Local;
using Vndb.Web;
using Xunit;

namespace Vndb.Tests;

public sealed class M5Tests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Handler : HttpMessageHandler
    {
        public bool Offline { get; set; }
        public bool Unauthorized { get; set; }
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!); ct.ThrowIfCancellationRequested();
            if (Offline) throw new HttpRequestException("test offline");
            if (Unauthorized) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = JsonContent.Create(new ApiProblem("unauthorized", "test", 401), options: VndbJson.Options) });
            object response = request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/info" => new ServiceInfo("test-instance", [1], "test", 1, false, [], [1], true),
                "/api/v1/items" => new ItemPage([Item()], 1, 100, 1, false),
                "/api/v1/plays" => Array.Empty<PersonalPlayView>(),
                "/api/v1/installations" => new InstallationPage([], 1, 24, 0, false),
                _ => throw new InvalidOperationException("Unexpected test request")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response, options: VndbJson.Options) });
        }
    }
    internal static LibraryItemView Item(int n = 17)
    {
        var now = DateTimeOffset.UtcNow;
        var meta = MetadataImports.Snapshot(StorageTests.Entry("v" + n));
        return new(new("item-" + n, new("测试作品 " + n, "v" + n), 1, now, now), meta, n % 2);
    }
    private sealed class Dialogs : IDesktopDialogs
    {
        public Task<string?> ExecutableAsync() => Task.FromResult<string?>(null);
        public Task<string?> FolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveJsonAsync(string name) => Task.FromResult<string?>(null);
    }
    private sealed class Library : IDesktopLibrary
    {
        public Func<CancellationToken, Task<MetadataResponse>> Search { get; set; } = _ => throw new OfflineCacheMissException();
        public IReadOnlyList<LibraryItemView> Items { get; set; } = [];
        public Task<ShelfResult> ShelfAsync(CancellationToken ct)
        {
            var now = DateTimeOffset.UtcNow;
            var plays = Items.Where(i => i.PlayCount > 0).Select(i => new PersonalPlayView("play-" + i.Item.Id,
                new(i.Item.Details.Title, i.Item.Details.NovelId, "played", null, null, null, null, null, null, null, null), 1, now, now, ItemId: i.Item.Id)).ToArray();
            return Task.FromResult(new ShelfResult(new("http://test.local/", Items, plays, [], now), "test"));
        }
        public Task<MetadataResponse> QueryAsync(string text, string source, CancellationToken ct) => Search(ct);
        public Task<IReadOnlyList<PersonalPlayView>> PlaysAsync(string itemId, CancellationToken ct) => throw new WebUnavailableException();
    }

    [Fact]
    public async Task ShelfCacheIsScopedToOriginAndOnlyTransportFailureFallsBack()
    {
        using var dir = new TestDirectory(); using var http = new HttpClient(new Handler());
        var handler = new Handler(); using var client = new HttpClient(handler); using var services = new DesktopServices(dir.Path, client);
        await services.ConfigureAsync(new("http://one.local/"), null, false, Ct);
        Assert.Equal("Web 本地资料库", (await services.Library.ShelfAsync(Ct)).Source);
        handler.Offline = true; Assert.StartsWith("本机书架快照", (await services.Library.ShelfAsync(Ct)).Source);
        handler.Offline = false; handler.Unauthorized = true;
        Assert.Equal(401, (await Assert.ThrowsAsync<ClientApiException>(() => services.Library.ShelfAsync(Ct))).Status);
        handler.Offline = true; await services.ConfigureAsync(new("http://two.local/"), null, false, Ct);
        await Assert.ThrowsAsync<OfflineCacheMissException>(() => services.Library.ShelfAsync(Ct));
    }
    [Fact]
    public async Task SettingsNeverSerializeCredentialAndDpapiIsUserAndOriginBound()
    {
        using var dir = new TestDirectory(); var store = new LocalClientSettingsStore(dir.Path);
        const string secret = "m5-test-key";
        await store.SaveAsync(new("http://one.local/", "D:/Synthetic", "D:/Target"), Ct);
        if (!OperatingSystem.IsWindows()) return;
        await store.SaveCredentialAsync("http://one.local/", secret, Ct);
        Assert.Equal(secret, await store.CredentialAsync("http://one.local/", Ct));
        Assert.Null(await store.CredentialAsync("http://two.local/", Ct));
        Assert.DoesNotContain(secret, await File.ReadAllTextAsync(Path.Combine(dir.Path, "desktop-settings.json"), Ct));
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(store.Scope("http://one.local/"), "credential.json"), Ct)));
        await store.SaveCredentialAsync("http://one.local/", null, Ct); Assert.Null(await store.CredentialAsync("http://one.local/", Ct));
    }
    [Fact]
    public async Task BookcasePagesAndFiltersWithoutLosingJapaneseOrAllCompanies()
    {
        using var dir = new TestDirectory(); var library = new Library { Items = Enumerable.Range(1, 123).Select(Item).ToArray() };
        var vm = new MainViewModel(library, new(dir.Path), new Dialogs());
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(60, vm.Shelf.Books.Count); Assert.Equal(20, vm.Shelf.Rows.Count);
        Assert.Equal("作品名", vm.Shelf.Books[0].Title); Assert.Equal("会社一 / Second", vm.Shelf.Books[0].Company);
        vm.Shelf.NextCommand.Execute(null); Assert.Equal(2, vm.Shelf.Page);
        vm.Shelf.NextCommand.Execute(null); Assert.Equal(3, vm.Shelf.Books.Count);
        vm.Section = 1; Assert.Equal(1, vm.Shelf.Page); Assert.Contains("62 部", vm.Shelf.PageLabel);
        vm.Shelf.Filter = "missing"; Assert.Empty(vm.Shelf.Books); Assert.Equal(1, vm.Shelf.Page);
    }
    [Fact]
    public async Task ViewModelShowsQueryFailureCancellationAndPendingSource()
    {
        using var dir = new TestDirectory(); var library = new Library(); var vm = new MainViewModel(library, new(dir.Path), new Dialogs());
        vm.Discovery.Query = "Synthetic query";
        await vm.Discovery.SearchCommand.ExecuteAsync(null); Assert.Contains("本机没有", vm.Status); Assert.False(vm.Busy);
        library.Search = async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); throw new InvalidOperationException(); };
        var search = vm.Discovery.SearchCommand.ExecuteAsync(null); Assert.True(vm.Busy); vm.CancelCommand.Execute(null); await search;
        Assert.Contains("已取消", vm.Status); Assert.False(vm.Busy);
        library.Search = _ => Task.FromResult(new MetadataResponse([Item().Metadata!], "client_vndb", DateTimeOffset.UtcNow, 1, 30, null, false, PendingImport: true));
        await vm.Discovery.SearchCommand.ExecuteAsync(null); Assert.Contains("待同步", vm.Shelf.Source); Assert.Single(vm.Shelf.Books);
        library.Search = _ => throw new VndbApiException(HttpStatusCode.TooManyRequests, "test throttled");
        await vm.Discovery.SearchCommand.ExecuteAsync(null); Assert.Contains("throttled", vm.Status);
    }
    [Fact]
    public async Task DirectLaunchWorksWhileWebQueryIsBlockedAndMetadataCannotGrantLaunch()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); var launcher = new LocalLauncher(dir.Path);
        var fake = Path.GetFullPath(Path.Combine(TestDirectory.ScratchRoot, "../build/Vndb.FakeGame/bin/Release/net10.0/Vndb.FakeGame.exe"));
        var receipt = Path.Combine(dir.Path, "fake result 日本語.json");
        var profile = await launcher.SaveAsync("合成游戏", fake, ["--receipt", receipt], dir.Path, ct: Ct);
        var library = new Library { Search = async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); throw new InvalidOperationException(); } };
        var vm = new MainViewModel(library, launcher, new Dialogs()) { SelectedProfile = profile };
        vm.Discovery.Query = "Synthetic query";
        var search = vm.Discovery.SearchCommand.ExecuteAsync(null); Assert.True(vm.Busy);
        await vm.LaunchCommand.ExecuteAsync(null); Assert.Contains("已启动", vm.Status); Assert.False(search.IsCompleted);
        vm.CancelCommand.Execute(null); await search;
        for (var i = 0; i < 100 && !File.Exists(receipt); i++) await Task.Delay(50, Ct);
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(receipt, Ct));
        Assert.Equal(dir.Path, result.RootElement.GetProperty("cwd").GetString());
        Assert.Equal(receipt, result.RootElement.GetProperty("args")[1].GetString());
        try { using var process = System.Diagnostics.Process.GetProcessById(result.RootElement.GetProperty("pid").GetInt32()); await process.WaitForExitAsync(Ct); }
        catch (ArgumentException) { } // The synthetic process already exited; no forced termination.
        await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.LaunchAsync("metadata-import-id", Ct));
    }
    [Fact]
    public async Task ExecutableMutationRequiresNewExplicitProfileSave()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); var executable = Path.Combine(dir.Path, "synthetic.exe"); await File.WriteAllTextAsync(executable, "test", Ct);
        var launcher = new LocalLauncher(dir.Path); var profile = await launcher.SaveAsync("test", executable, [], dir.Path, ct: Ct);
        await File.WriteAllTextAsync(executable, "changed", Ct);
        await Assert.ThrowsAsync<IOException>(() => launcher.LaunchAsync(profile.Id, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => launcher.SaveAsync("test", Path.Combine(dir.Path, "bad.cmd"), [], dir.Path, ct: Ct));
    }
    [Fact]
    public void CoversRejectArbitraryOriginsAndAllowOnlyVndbFallback()
    {
        using var dir = new TestDirectory(); using var http = new HttpClient(); var covers = new CoverCache(http, dir.Path, new("http://nas.local:5187/"));
        var meta = Item().Metadata!;
        Assert.Equal("nas.local", covers.Source(meta with { Cover = new("ready", "/api/v1/vns/v17/cover") })!.Host);
        Assert.Throws<InvalidDataException>(() => covers.Source(meta with { Cover = new("ready", "https://example.com/api/v1/vns/v17/cover") }));
        Assert.Throws<InvalidDataException>(() => covers.Source(meta with { Novel = meta.Novel! with { Image = new() { Id = "x", Url = "file:///bad" } } }));
    }
    private sealed class Factory(string path) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Library:DataDirectory", path).UseSetting("Library:WriteKey", "m5-test-key").UseSetting("CatalogJobs:Enabled", "false");
    }
    [Fact]
    public async Task ShelfSdkReadsExistingWebPlayShapeWithoutSqliteDependency()
    {
        using var dir = new TestDirectory(); using var app = new Factory(dir.Path); using var http = app.CreateClient();
        var store = app.Services.GetRequiredService<SqliteLibraryStore>(); store.Save(StorageTests.Entry());
        var item = store.SaveItem(new(new("Example", "v17")), null, "m5-item");
        store.SavePersonalPlay(new(new("Example", "v17", "completed", null, null, 65, 9m, "test review", null, null, "ja"), item.Id), null, "m5-play");
        var sdk = new WebLibraryClient(http, http.BaseAddress!, null, TimeSpan.FromSeconds(5));
        Assert.Contains((await sdk.ItemsAsync(ct: Ct)).Items, i => i.Item.Id == item.Id);
        var play = Assert.Single(await sdk.PlaysAsync(item.Id, Ct)); Assert.Equal("test review", play.Details.Review); Assert.Equal(65, play.Details.DurationMinutes);
    }
    private sealed class DirectHandler : HttpMessageHandler
    {
        public int UpstreamCalls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host != "api.vndb.org") throw new HttpRequestException("test Web offline");
            UpstreamCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new
                { results = new[] { new VisualNovel { Id = "v17", Title = "合成离线作品" } }, more = false }, options: VndbJson.Options) });
        }
    }
    [Fact]
    public async Task DesktopCompositionFallsBackToCorePersistsPendingAndCanUseCacheWithNoNetwork()
    {
        using var dir = new TestDirectory(); var handler = new DirectHandler(); using var http = new HttpClient(handler);
        using var services = new DesktopServices(dir.Path, http, new() { RequestInterval = TimeSpan.Zero });
        await services.ConfigureAsync(new("http://offline.local/"), null, false, Ct);
        var result = await services.Library.QueryAsync("v17", "auto", Ct);
        Assert.Equal("client_vndb", result.Source); Assert.True(result.PendingImport);
        Assert.Single(Directory.GetFiles(Path.Combine(services.StateDirectory, "pending"), "*.json"));
        var before = handler.UpstreamCalls;
        var cached = await services.Library.QueryAsync("v17", "cache", Ct);
        Assert.Equal("client_cache", cached.Source); Assert.True(cached.PendingImport); Assert.Equal(before, handler.UpstreamCalls);
        Assert.Empty(await services.Launcher.ReadAsync(Ct));
    }
    [Fact]
    public async Task OrganizationPreviewCannotExecuteAfterItsInputsChange()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); using var services = new DesktopServices(Path.Combine(dir.Path, "state"));
        await services.InitializeAsync(Ct);
        var sourceScope = Path.Combine(dir.Path, "scope"); var source = Path.Combine(sourceScope, "old"); var target = Path.Combine(dir.Path, "target");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target); await File.WriteAllTextAsync(Path.Combine(source, "keep.txt"), "synthetic", Ct);
        var now = DateTimeOffset.UtcNow;
        var installation = new LocalInstallationView(new("test-install", new(services.Identity!.Id, source, "test"), "present", now, 1, now, now),
            [new("test-component", "test-install", new("test-item", "", "game"), 1, now, now)]);
        var vm = new MainViewModel(new Library(), services.Launcher, new Dialogs(), services)
            { SelectedInstallation = installation };
        vm.Organization.SourceScope = sourceScope; vm.Organization.TargetRoot = target; vm.Organization.Company = "会社"; vm.Organization.JapaneseTitle = "作品";
        await vm.Organization.PreviewCommand.ExecuteAsync(null); Assert.NotEmpty(vm.Organization.PlanHash); vm.Organization.Approval = vm.Organization.PlanHash;
        vm.Organization.JapaneseTitle = "新名称"; Assert.Empty(vm.Organization.PlanHash); Assert.Empty(vm.Organization.Approval);
        await vm.Organization.ExecuteCommand.ExecuteAsync(null); Assert.Contains("先生成计划", vm.Status); Assert.True(File.Exists(Path.Combine(source, "keep.txt")));
    }
    [Fact]
    public void NotStartedIsExcludedAndCompanyOverrideDoesNotHideCoDevelopers()
    {
        var item = Item() with { Item = Item().Item with { Details = Item().Item.Details with { CompanyOverride = "归档集团" } } };
        var now = DateTimeOffset.UtcNow;
        var book = new BookViewModel(item, item.Metadata, [new("test-play", new("test", "v17", "not_started", null, null, null, null, null, null, null, null), 1, now, now)]);
        Assert.False(book.HasPlayed); Assert.Equal("归档集团", book.GroupCompany); Assert.Contains("Second", book.Company);
    }
    private sealed class KeyHandler : HttpMessageHandler
    {
        public List<string?> Keys { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Keys.Add(request.Headers.TryGetValues("X-Library-Key", out var values) ? values.Single() : null);
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(
                new LibraryDevice("test-device", new("test", "windows"), 1, now, now), options: VndbJson.Options) });
        }
    }
    [Fact]
    public async Task SessionManagementKeyNeverFollowsAChangedWebOrigin()
    {
        using var dir = new TestDirectory(); var handler = new KeyHandler(); using var http = new HttpClient(handler); using var services = new DesktopServices(dir.Path, http);
        var write = new DeviceWrite("test-device", new("test", "windows"));
        await services.ConfigureAsync(new("http://one.local/"), "m5-test-key", false, Ct);
        await services.Web!.RegisterDeviceAsync(write, "test-one", Ct);
        await services.ConfigureAsync(new("http://two.local/"), null, false, Ct);
        await services.Web!.RegisterDeviceAsync(write, "test-two", Ct);
        await services.ConfigureAsync(new("http://one.local/"), null, false, Ct);
        await services.Web!.RegisterDeviceAsync(write, "test-again", Ct);
        await services.ConfigureAsync(new("http://one.local/"), "", false, Ct);
        await services.Web!.RegisterDeviceAsync(write, "test-cleared", Ct);
        Assert.Equal(new string?[] { "m5-test-key", null, "m5-test-key", null }, handler.Keys);
    }
    [Fact]
    public async Task SwitchingServerClearsVisiblePersonalDetailsAndPriorApproval()
    {
        using var dir = new TestDirectory(); using var services = new DesktopServices(dir.Path);
        var vm = new MainViewModel(new Library { Items = [Item()] }, services.Launcher, new Dialogs(), services);
        await vm.RefreshCommand.ExecuteAsync(null); vm.SelectedBook = vm.Shelf.Books[0]; vm.Organization.Approval = "old-approval";
        var now = DateTimeOffset.UtcNow;
        var sessionPlay = new PersonalPlayView("synthetic-play", new("Synthetic", null, "playing", null, null, 0, null, "Synthetic private review", null, null, null), 1, now, now);
        vm.SessionPlays.Add(sessionPlay); vm.SelectedPlay = sessionPlay;
        vm.ServerUrl = "http://changed.local/"; await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Empty(vm.Shelf.Books); Assert.Null(vm.SelectedBook); Assert.Empty(vm.Organization.Approval);
        Assert.Empty(vm.SessionPlays); Assert.Null(vm.SelectedPlay);
        Assert.Equal("尚未加载", vm.Shelf.Source); Assert.Null(vm.BookDetails.Book); Assert.Empty(vm.BookDetails.Metadata);
    }
}
