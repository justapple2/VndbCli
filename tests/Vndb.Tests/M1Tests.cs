using Vndb.Local;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

public sealed class M1Tests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static ClientQuery Get => new("vn_get", new(), "v17");
    private const string Novels = """
        {"results":[{"id":"v17","title":"Example","alttitle":"作品名","description":"Full text",
        "titles":[{"lang":"ja","title":"作品名","main":true}],"developers":[{"id":"p1","name":"Studio"},{"id":"p2","name":"Second"}],
        "image":{"id":"cv1","url":"https://t.vndb.org/cv/01/1.jpg"}}],"more":false}
        """;
    private const string Producers = """
        {"results":[{"id":"p1","name":"Studio","original":"会社","aliases":["Brand"],"description":"Company details"},
        {"id":"p2","name":"Second","description":"Second details"}],"more":false}
        """;
    private static MetadataResponse Empty => QueryRules.Response([], "vndb", new(), null, false);
    private static HttpResponseMessage Wire<T>(T value) => StubHandler.Json(JsonSerializer.Serialize(value, VndbJson.Options));
    private static HttpResponseMessage Info() => Wire(new ServiceInfo("test-instance", [1], "0.7.0", 5, true, ["metadata_query"], [1], false));
    private static RouteHandler Upstream() => new((r, _) => Task.FromResult(StubHandler.Json(r.RequestUri!.AbsolutePath.EndsWith("producer", StringComparison.Ordinal) ? Producers : Novels)));
    private static VndbClient Core(HttpClient http) => new(http, VndbClientTests.FastOptions);
    private static WebLibraryClient Web(HttpClient http) => new(http, new("http://library.test/"), "private-library-key", TimeSpan.FromSeconds(10));
    private sealed class RouteHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        public List<(string Url, string? Key, string? Authorization)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.TryGetValues("X-Library-Key", out var keys) ? keys.Single() : null,
                request.Headers.Authorization?.ToString()));
            return action(request, token);
        }
    }
    private sealed class Factory(string directory, HttpMessageHandler upstream, bool brokenCover = false) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Library:DataDirectory", directory).UseSetting("Library:WriteKey", "test-key").UseSetting("CatalogJobs:Enabled", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<VndbClient>(); services.RemoveAll<CoverDownloader>();
                services.AddSingleton(_ => Core(new HttpClient(upstream, false)));
                services.AddSingleton(_ => new CoverDownloader(new HttpClient(new StubHandler((_, _) => Task.FromResult(
                    brokenCover ? StubHandler.Json("broken") : ImportTests.Image()))), directory));
            });
        }
    }
    private static void Authenticate(HttpClient http)
    { http.DefaultRequestHeaders.Add("X-Library-Request", "1"); http.DefaultRequestHeaders.Add("X-Library-Key", "test-key"); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebResolvePersistsFullTextAllCompaniesAndCoverStatusThenReadsWithoutUpstream(bool brokenCover)
    {
        using var directory = new TestDirectory(); using var upstream = Upstream();
        await using var factory = new Factory(directory.Path, upstream, brokenCover); using var http = factory.CreateClient();
        using var miss = await http.GetAsync("/api/v1/vns/v17", Ct); Assert.Equal(HttpStatusCode.NotFound, miss.StatusCode);
        using var denied = await http.PostAsync("/api/v1/vns/v17/resolve", null, Ct); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Empty(upstream.Requests); Authenticate(http);
        using var imported = await http.PostAsync("/api/v1/vns/v17/resolve", null, Ct);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var first = (await imported.Content.ReadFromJsonAsync<MetadataResponse>(VndbJson.Options, Ct))!;
        Assert.Equal("vndb", first.Source); Assert.Null(first.Total);
        var item = Assert.Single(first.Items); Assert.Equal("Full text", item.Novel!.Description);
        Assert.Equal(2, item.Developers.Count); Assert.Equal("Second details", item.Developers[1].Description);
        Assert.Equal(brokenCover ? "failed" : "ready", item.Cover.Status);
        var stored = factory.Services.GetRequiredService<ILibraryStore>().Get("v17"); Assert.NotNull(stored);
        var count = upstream.Requests.Count;
        var second = (await http.GetFromJsonAsync<MetadataResponse>("/api/v1/vns/v17", VndbJson.Options, Ct))!;
        Assert.Equal("library", second.Source); Assert.Equal(first.FetchedAt, second.FetchedAt); Assert.Equal(count, upstream.Requests.Count);
        var raw = JsonSerializer.Serialize(second, VndbJson.Options);
        Assert.DoesNotContain("relative_path", raw); Assert.DoesNotContain(directory.Path, raw);
        using var image = await http.GetAsync("/api/v1/vns/v17/cover", Ct);
        Assert.Equal(brokenCover ? HttpStatusCode.NotFound : HttpStatusCode.OK, image.StatusCode);
        if (!brokenCover) { Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType); Assert.NotNull(image.Headers.ETag); }
        using var localOnly = await http.PostAsJsonAsync("/api/v1/queries/vns", new MetadataQuery("Unknown", Mode: "local_only"), VndbJson.Options, Ct);
        Assert.Empty((await localOnly.Content.ReadFromJsonAsync<MetadataResponse>(VndbJson.Options, Ct))!.Items);
        Assert.Equal(count, upstream.Requests.Count);
    }

    [Fact]
    public async Task ProducerQueriesArePersistedWithoutRequiringANovelAndPagingHasNoFabricatedTotal()
    {
        using var directory = new TestDirectory(); using var upstream = Upstream();
        await using var factory = new Factory(directory.Path, upstream); using var http = factory.CreateClient(); Authenticate(http);
        using var query = await http.PostAsJsonAsync("/api/v1/queries/producers", new MetadataQuery("Brand"), VndbJson.Options, Ct);
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        var result = (await query.Content.ReadFromJsonAsync<MetadataResponse>(VndbJson.Options, Ct))!;
        Assert.Null(result.Total); Assert.Equal("Company details", result.Items[0].Producer!.Description);
        var stats = await http.GetFromJsonAsync<LocalStatistics>("/api/v1/stats", VndbJson.Options, Ct);
        Assert.Equal(2, stats!.Producers); Assert.Equal(0, stats.Novels);
        var count = upstream.Requests.Count;
        var local = await http.GetFromJsonAsync<MetadataResponse>("/api/v1/producers/p1", VndbJson.Options, Ct);
        Assert.Equal("library", local!.Source); Assert.Equal(count, upstream.Requests.Count);
        using var works = await http.PostAsJsonAsync("/api/v1/producers/p1/vns/query", new MetadataQuery(), VndbJson.Options, Ct);
        var novels = await works.Content.ReadFromJsonAsync<MetadataResponse>(VndbJson.Options, Ct);
        Assert.Single(novels!.Items); Assert.NotNull(factory.Services.GetRequiredService<ILibraryStore>().Get("v17"));
        count = upstream.Requests.Count;
        using var page = await http.PostAsJsonAsync("/api/v1/producers/p1/vns/query", new MetadataQuery(Page: 2), VndbJson.Options, Ct);
        var last = (await page.Content.ReadFromJsonAsync<MetadataResponse>(VndbJson.Options, Ct))!;
        Assert.Equal("library", last.Source); Assert.Equal(1, last.Total); Assert.Empty(last.Items); Assert.Equal(count, upstream.Requests.Count);
    }

    [Fact]
    public async Task ActualOpenApiDiscoveryAndLegacyAdapterAreAvailable()
    {
        using var directory = new TestDirectory(); using var upstream = Upstream();
        await using var factory = new Factory(directory.Path, upstream); using var http = factory.CreateClient();
        var info = await http.GetFromJsonAsync<ServiceInfo>("/api/v1/info", VndbJson.Options, Ct);
        Assert.Contains(1, info!.ApiVersions); Assert.True(info.MetadataImportAvailable); Assert.Equal(13, info.SchemaVersion);
        var second = await http.GetFromJsonAsync<ServiceInfo>("/api/v1/info", VndbJson.Options, Ct);
        Assert.Equal(info.ServiceInstanceId, second!.ServiceInstanceId);
        using var spec = JsonDocument.Parse(await http.GetStringAsync("/openapi/v1.json", Ct));
        var paths = spec.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/v1/queries/vns", out _)); Assert.True(paths.TryGetProperty("/api/v1/producers/{id}/resolve", out _));
        Authenticate(http);
        using var legacy = await http.GetAsync("/api/remote/search?q=Example", Ct);
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode); Assert.True(legacy.Headers.Contains("Deprecation"));
        Assert.NotNull(factory.Services.GetRequiredService<ILibraryStore>().Get("v17"));
        var count = upstream.Requests.Count;
        using var local = await http.PostAsJsonAsync("/api/v1/queries/vns", new MetadataQuery("Example"), VndbJson.Options, Ct);
        Assert.Equal("library", (await local.Content.ReadFromJsonAsync<MetadataResponse>(VndbJson.Options, Ct))!.Source);
        Assert.Equal(count, upstream.Requests.Count);
        using var malformed = await http.PostAsync("/api/v1/queries/vns", new StringContent("{broken", System.Text.Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("invalid_json", (await malformed.Content.ReadFromJsonAsync<ApiProblem>(VndbJson.Options, Ct))!.Code);
    }

    [Fact]
    public async Task GenuineNegativeQueryIsCachedButErrorsAreNot()
    {
        using var directory = new TestDirectory(); using var upstream = new StubHandler("""{"results":[],"more":false}""");
        await using var factory = new Factory(directory.Path, upstream); using var http = factory.CreateClient(); Authenticate(http);
        using var first = await http.PostAsJsonAsync("/api/v1/queries/vns", new MetadataQuery("absent"), VndbJson.Options, Ct);
        Assert.Empty((await first.Content.ReadFromJsonAsync<MetadataResponse>(VndbJson.Options, Ct))!.Items);
        using var second = await http.PostAsJsonAsync("/api/v1/queries/vns", new MetadataQuery("absent"), VndbJson.Options, Ct);
        Assert.Equal("query_cache", (await second.Content.ReadFromJsonAsync<MetadataResponse>(VndbJson.Options, Ct))!.Source);
        Assert.Single(upstream.Requests);
    }

    [Fact]
    public async Task NovelQueryAssociationsSurviveWebRestart()
    {
        using var directory = new TestDirectory(); using var upstream = Upstream();
        await using (var factory = new Factory(directory.Path, upstream))
        {
            using var http = factory.CreateClient(); Authenticate(http);
            using var query = await http.PostAsJsonAsync("/api/v1/queries/vns", new MetadataQuery("release-only-search-text"), VndbJson.Options, Ct);
            Assert.Single((await query.Content.ReadFromJsonAsync<MetadataResponse>(VndbJson.Options, Ct))!.Items);
        }
        var count = upstream.Requests.Count;
        await using var restarted = new Factory(directory.Path, upstream); using var localHttp = restarted.CreateClient();
        var local = await localHttp.GetFromJsonAsync<MetadataResponse>("/api/v1/vns?q=release-only-search-text", VndbJson.Options, Ct);
        Assert.Equal("query_cache", local!.Source); Assert.Single(local.Items); Assert.Null(local.Total);
        Assert.Equal(count, upstream.Requests.Count);
    }

    [Fact]
    public async Task ConnectionFailureFallsBackOnceAndSavesImmutableBatchThenOfflineCacheRetainsFetchedAt()
    {
        using var directory = new TestDirectory(); using var upstream = Upstream(); using var coreHttp = new HttpClient(upstream); using var core = Core(coreHttp);
        using var failed = new RouteHandler((_, _) => throw new HttpRequestException("private error")); using var webHttp = new HttpClient(failed);
        var store = new JsonSnapshotStore(directory.Path); var router = new QueryRouter(core, store, Web(webHttp));
        var result = await router.QueryAsync(Get, ct: Ct);
        Assert.Equal("client_vndb", result.Source); Assert.True(result.PendingImport); Assert.Single(failed.Requests);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "pending")));
        var bytes = await File.ReadAllBytesAsync(file, Ct); var batch = JsonSerializer.Deserialize<MetadataBatch>(bytes, VndbJson.Options)!;
        Assert.Equal("vn-library-batch", batch.Format); Assert.Equal(1, batch.FormatVersion); Assert.Equal(2, batch.Items[0].Developers.Count);
        Assert.DoesNotContain("private-library-key", System.Text.Encoding.UTF8.GetString(bytes));
        var count = upstream.Requests.Count; var cached = await router.QueryAsync(Get, "cache", Ct);
        Assert.Equal("client_cache", cached.Source); Assert.Equal(result.FetchedAt, cached.FetchedAt);
        Assert.True(cached.PendingImport); Assert.Equal(count, upstream.Requests.Count); Assert.Equal(bytes, await File.ReadAllBytesAsync(file, Ct));
        Assert.Equal("client_cache", (await router.QueryAsync(Get, ct: Ct)).Source);
        Assert.Single(failed.Requests); Assert.Equal(count, upstream.Requests.Count);
        Assert.All(upstream.Requests, r => { Assert.Null(r.Key); Assert.Null(r.Authorization); });
        using var offline = new RouteHandler((_, _) => throw new HttpRequestException()); using var offlineHttp = new HttpClient(offline); using var offlineCore = Core(offlineHttp);
        var bothOffline = new QueryRouter(offlineCore, store, Web(webHttp));
        Assert.Equal("client_cache", (await bothOffline.QueryAsync(Get, ct: Ct)).Source);
        await Assert.ThrowsAsync<OfflineCacheMissException>(() => bothOffline.QueryAsync(Get with { Id = "v999999" }, ct: Ct));
    }

    [Theory]
    [InlineData(401, "unauthorized")]
    [InlineData(403, "unauthorized")]
    [InlineData(409, "conflict")]
    [InlineData(429, "upstream_throttled")]
    [InlineData(503, "upstream_throttled")]
    [InlineData(502, "upstream_unavailable")]
    [InlineData(413, "import_too_large")]
    [InlineData(413, "request_too_large")]
    [InlineData(400, "import_item_limit")]
    [InlineData(400, "invalid_json")]
    [InlineData(400, "import_kind_mismatch")]
    public async Task BusinessAndUpstreamErrorsNeverFallBack(int status, string code)
    {
        using var directory = new TestDirectory(); using var upstream = Upstream(); using var coreHttp = new HttpClient(upstream); using var core = Core(coreHttp);
        using var server = new RouteHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = JsonContent.Create(new ApiProblem(code, "secret body", status), options: VndbJson.Options) }));
        using var http = new HttpClient(server);
        var error = await Assert.ThrowsAsync<ClientApiException>(() => new QueryRouter(core, new JsonSnapshotStore(directory.Path), Web(http)).QueryAsync(Get, ct: Ct));
        Assert.Equal(status, error.Status); Assert.DoesNotContain("secret body", error.Message); Assert.Empty(upstream.Requests);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.json", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("incompatible")]
    [InlineData("redirect")]
    public async Task ProtocolErrorsAndRedirectsDoNotFallBackOrForwardKeys(string scenario)
    {
        using var directory = new TestDirectory(); using var upstream = Upstream(); using var coreHttp = new HttpClient(upstream); using var core = Core(coreHttp);
        using var server = new RouteHandler((_, _) => Task.FromResult(scenario switch
        {
            "incompatible" => Wire(new ServiceInfo("other", [2], "future", 5, true, [], [], false)),
            "redirect" => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://api.vndb.org/kana/vn") } },
            _ => StubHandler.Json("{broken")
        }));
        using var http = new HttpClient(server);
        await Assert.ThrowsAsync<ClientApiException>(() => new QueryRouter(core, new JsonSnapshotStore(directory.Path), Web(http)).QueryAsync(Get, ct: Ct));
        Assert.Empty(upstream.Requests); Assert.Single(server.Requests); Assert.Null(server.Requests[0].Key);
    }

    [Fact]
    public async Task EmptySuccessAndCancellationDoNotFallBackAndKeysOnlyGoToWebWrites()
    {
        using var directory = new TestDirectory(); using var upstream = Upstream(); using var coreHttp = new HttpClient(upstream); using var core = Core(coreHttp);
        using var server = new RouteHandler((r, _) => Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("info", StringComparison.Ordinal) ? Info() :
            r.Method == HttpMethod.Get ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = JsonContent.Create(new ApiProblem("not_found", "", 404), options: VndbJson.Options) } : Wire(Empty)));
        using var http = new HttpClient(server);
        var router = new QueryRouter(core, new JsonSnapshotStore(directory.Path), Web(http));
        Assert.Empty((await router.QueryAsync(Get, ct: Ct)).Items); Assert.Empty(upstream.Requests);
        Assert.Equal(3, server.Requests.Count); Assert.Null(server.Requests[0].Key); Assert.Null(server.Requests[1].Key); Assert.Equal("private-library-key", server.Requests[2].Key);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.QueryAsync(Get, ct: cts.Token));
        Assert.Empty(upstream.Requests); Assert.Equal(3, server.Requests.Count);
    }

    [Fact]
    public async Task InFlightWebCancellationDoesNotBecomeAnOfflineFallback()
    {
        using var directory = new TestDirectory(); using var upstream = Upstream(); using var coreHttp = new HttpClient(upstream); using var core = Core(coreHttp);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new RouteHandler(async (_, token) => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Info(); });
        using var http = new HttpClient(server); using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var task = new QueryRouter(core, new JsonSnapshotStore(directory.Path), Web(http)).QueryAsync(Get, ct: cancellation.Token);
        await started.Task.WaitAsync(Ct); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); Assert.Empty(upstream.Requests); Assert.Single(server.Requests);
    }

    [Fact]
    public async Task TimeoutCanFallBackButForcedWebCannotAndFailureToSaveIsNotReportedAsSaved()
    {
        using var directory = new TestDirectory(); using var upstream = Upstream(); using var coreHttp = new HttpClient(upstream); using var core = Core(coreHttp);
        using var server = new RouteHandler(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Info(); });
        using var http = new HttpClient(server);
        var web = new WebLibraryClient(http, new("http://library.test/"), null, TimeSpan.FromMilliseconds(30));
        var router = new QueryRouter(core, new JsonSnapshotStore(directory.Path), web);
        var result = await router.QueryAsync(Get, ct: Ct); Assert.Equal("client_vndb", result.Source); Assert.Single(server.Requests);
        var count = upstream.Requests.Count;
        await Assert.ThrowsAsync<WebUnavailableException>(() => router.QueryAsync(Get, "web", Ct));
        Assert.Equal(count, upstream.Requests.Count);
        await Assert.ThrowsAsync<IOException>(() => new QueryRouter(core, new FailedStore()).QueryAsync(Get, "vndb", Ct));
    }
    private sealed class FailedStore : ICatalogSnapshots
    {
        public Task SaveAsync(CatalogQuery query, CatalogResult response, CancellationToken ct) => throw new IOException("disk failure");
        public Task<CatalogResult?> ReadAsync(CatalogQuery query, CancellationToken ct) => Task.FromResult<CatalogResult?>(null);
    }

    [Fact]
    public async Task CoreRejectsWrongIdentityBeforeItCanBePersisted()
    {
        using var upstream = new StubHandler(Novels); using var http = new HttpClient(upstream); using var core = Core(http);
        await Assert.ThrowsAsync<JsonException>(() => core.GetNovelAsync("v999999", Ct));
        Assert.Equal(TimeSpan.FromSeconds(5), new VndbClientOptions().RequestInterval);
    }
}
