using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Vndb.Client;
using Vndb.Cli;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.ViewModels;
using Vndb.Library;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class ImportLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Factory(string path, int bytes, int items = 500) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b) => b.UseSetting("Library:DataDirectory", path)
            .UseSetting("Library:WriteKey", "limit-test-key").UseSetting("CatalogJobs:Enabled", "false")
            .UseSetting("MetadataImports:MaximumBytes", bytes.ToString()).UseSetting("MetadataImports:MaximumItems", items.ToString());
    }
    private static HttpClient Http(Factory app)
    {
        var http = app.CreateClient(); http.DefaultRequestHeaders.Add("X-Library-Request", "1"); http.DefaultRequestHeaders.Add("X-Library-Key", "limit-test-key");
        return http;
    }
    private static string Listing(int bytes, int count = 1)
    {
        var text = "{\"format\":\"vn-library-batch\",\"format_version\":1,\"kind\":\"metadata\",\"batch_id\":\"limit-test\",\"items\":[" +
            string.Join(',', Enumerable.Range(1, count).Select(i => "{\"id\":\"v" + i + "\",\"title\":\"synthetic\"}")) + "]}";
        return text.Insert(text.Length - 1, new string(' ', Math.Max(0, bytes - Encoding.UTF8.GetByteCount(text))));
    }
    private static async Task<ApiProblem> Problem(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode); var problem = await response.Content.ReadFromJsonAsync<ApiProblem>(VndbJson.Options, Ct);
        Assert.NotNull(problem); Assert.Equal(code, problem.Code); Assert.Equal(status, problem.Status); return problem;
    }
    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");

    [Theory]
    [InlineData(1024)]
    [InlineData(8 * 1024 * 1024)]
    [InlineData(10 * 1024 * 1024)]
    public async Task DiscoveryAndPayloadBoundaryUseSameLimitIncludingRaisedAboveOldNineMiB(int bytes)
    {
        using var dir = new TestDirectory(); await using var app = new Factory(dir.Path, bytes); using var http = Http(app);
        var info = await http.GetFromJsonAsync<ServiceInfo>("/api/v1/info", VndbJson.Options, Ct); Assert.Equal(bytes, info!.ImportLimits!.MaximumBytes);
        Assert.Equal(info.ImportLimits.MaximumRequestBytes, app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize);
        Assert.DoesNotContain("maximum_request_bytes", await http.GetStringAsync("/api/v1/info", Ct));
        using var accepted = await http.PostAsync("/api/v1/imports/preview", Json("{\"payload\":" + Listing(bytes) + "}"), Ct);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var rejected = await http.PostAsync("/api/v1/imports/preview", Json("{\"payload\":" + Listing(bytes + 1) + "}"), Ct);
        await Problem(rejected, 413, "import_too_large");
        Assert.Equal(0, app.Services.GetRequiredService<SqliteLibraryStore>().Statistics().Novels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnvelopeHasSeparateBoundForKnownAndChunkedBodies(bool chunked)
    {
        using var dir = new TestDirectory(); await using var app = new Factory(dir.Path, 1024); using var http = Http(app);
        var at = "{\"payload\":" + Listing(200) + ",\"ignored\":\"";
        at += new string('x', (int)new ImportLimits(1024).MaximumRequestBytes - at.Length - 2) + "\"}";
        using var validContent = chunked ? new StreamContent(new NonSeekable(Encoding.UTF8.GetBytes(at))) : (HttpContent)Json(at);
        validContent.Headers.ContentType = new("application/json");
        using var valid = await http.PostAsync("/api/v1/imports/preview", validContent, Ct); Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        using var content = chunked ? new StreamContent(new NonSeekable(Encoding.UTF8.GetBytes(at + " "))) : (HttpContent)Json(at + " ");
        content.Headers.ContentType = new("application/json");
        using var response = await http.PostAsync("/api/v1/imports/preview", content, Ct); await Problem(response, 413, "request_too_large");
    }

    [Fact]
    public async Task ItemLimitMalformedKindAndPrivacyFailuresHaveStableCodesBeforePersistence()
    {
        using var dir = new TestDirectory(); await using var app = new Factory(dir.Path, 4096, 1); using var http = Http(app);
        using var count = await http.PostAsync("/api/v1/sync/batches", Json(Listing(0, 2)), Ct); await Problem(count, 400, "import_item_limit");
        using var malformed = await http.PostAsync("/api/v1/activity/batches", Json("{"), Ct); await Problem(malformed, 400, "invalid_json");
        using var metadata = await http.PostAsync("/api/v1/activity/batches", Json(Listing(0)), Ct); await Problem(metadata, 400, "import_kind_mismatch");
        using var activity = await http.PostAsync("/api/v1/imports/preview", Json("{\"payload\":{\"format\":\"vn-library-activity\"}}"), Ct); await Problem(activity, 400, "import_kind_mismatch");
        using var secret = await http.PostAsync("/api/v1/sync/batches", Json("{\"title\":\"E:\\\\Synthetic\\\\Game\"}"), Ct); await Problem(secret, 400, "local_only_data");
        var store = app.Services.GetRequiredService<SqliteLibraryStore>(); Assert.Empty(store.Devices()); Assert.Equal(0, store.Statistics().Novels);
    }

    private static ActivityBatch Activity(int count = 1) => new()
    {
        BatchId = Guid.NewGuid().ToString("D"), DeviceId = Guid.NewGuid().ToString("D"), Device = new("Synthetic", "windows"),
        Items = Enumerable.Range(1, count).Select(i => new ActivityItemSeed("item-" + i, new("Synthetic " + i))).ToArray()
    };

    [Fact]
    public async Task ActivityHonorsConfiguredItemAndByteLimitsAndRetriesOriginalReceipt()
    {
        using var dir = new TestDirectory(); await using var app = new Factory(dir.Path, 1024, 1); using var http = Http(app);
        using var count = await http.PostAsJsonAsync("/api/v1/activity/batches", Activity(2), VndbJson.Options, Ct); await Problem(count, 400, "import_item_limit");
        var large = Activity() with { Device = new(new string('x', 1500), "windows") };
        using var over = await http.PostAsJsonAsync("/api/v1/activity/batches", large, VndbJson.Options, Ct); await Problem(over, 413, "import_too_large");
        Assert.Empty(app.Services.GetRequiredService<SqliteLibraryStore>().Devices());
        var batch = Activity();
        using var first = await http.PostAsJsonAsync("/api/v1/activity/batches", batch, VndbJson.Options, Ct); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var retry = await http.PostAsJsonAsync("/api/v1/activity/batches", batch, VndbJson.Options, Ct);
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await retry.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task LoweredLimitsKeepPendingBytesIdsBindingsAndReceiptsUntilCompatibleRetry()
    {
        using var db = new TestDirectory(); using var local = new TestDirectory(); var origin = "http://localhost/";
        var scope = LocalQueryScope.Resolve(local.Path, new(), server: origin);
        var entry = MetadataImports.Snapshot(StorageTests.Entry());
        await new JsonSnapshotStore(scope.DirectoryPath, origin).SaveAsync(new("vn_search", new("Synthetic")),
            QueryRules.Response([entry, entry with { VndbId = "v18", Novel = entry.Novel! with { Id = "v18" } }], "vndb", new(), 2, false) with { PendingImport = true }, Ct);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(scope.DirectoryPath, "pending"), "*.json")); var original = await File.ReadAllBytesAsync(file, Ct);
        await using (var app = new Factory(db.Path, 1024 * 1024, 1))
        {
            using var http = app.CreateClient(); var api = new WebLibraryClient(http, http.BaseAddress!, "limit-test-key", TimeSpan.FromSeconds(10));
            var error = await Assert.ThrowsAsync<ImportValidationException>(() => new PendingMetadataSync(api, origin, scope.DirectoryPath).SubmitAsync(Ct));
            Assert.Equal("import_item_limit", error.Code); Assert.Contains("原ID", error.Message); Assert.Equal(original, await File.ReadAllBytesAsync(file, Ct));
            using var output = new StringWriter(); using var errors = new StringWriter();
            var cli = new CliApplication(output, errors, () => new HttpClient(app.Server.CreateHandler()), n => n switch
                { "VN_DESKTOP_HOME" => local.Path, "VN_LIBRARY_KEY" => "limit-test-key", _ => null }, VndbClientTests.FastOptions, new(origin));
            Assert.NotEqual(0, await cli.RunAsync(["sync", "--json"], Ct));
            Assert.Equal("import_item_limit", JsonSerializer.Deserialize<ApiProblem>(output.ToString(), VndbJson.Options)!.Code);
            Assert.Equal(original, await File.ReadAllBytesAsync(file, Ct));
        }
        string receiptPath;
        await using (var app = new Factory(db.Path, 1024 * 1024, 3))
        {
            using var http = app.CreateClient(); var api = new WebLibraryClient(http, http.BaseAddress!, "limit-test-key", TimeSpan.FromSeconds(10));
            Assert.Single(await new PendingMetadataSync(api, origin, scope.DirectoryPath).SubmitAsync(Ct));
            receiptPath = Assert.Single(Directory.GetFiles(Path.Combine(scope.DirectoryPath, "receipts"), "*.json"));
            var state = await ImportStatus.ReadAsync(api, scope, Path.Combine(local.Path, "no-activity"), Ct);
            Assert.Equal(3, state.Limits!.MaximumItems); Assert.Equal(2, state.Results.Count);
            var vm = new ImportFeedbackViewModel(_ => Task.FromResult(state), action => action(Ct)); await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(2, vm.Results.Count); Assert.Contains("3项", vm.Summary); Assert.Contains("原ID", vm.Summary);
        }
        var proof = await File.ReadAllBytesAsync(receiptPath, Ct);
        await using (var app = new Factory(db.Path, 1024 * 1024, 1))
        {
            using var http = app.CreateClient(); var api = new WebLibraryClient(http, http.BaseAddress!, "limit-test-key", TimeSpan.FromSeconds(10));
            Assert.Single(await new PendingMetadataSync(api, origin, scope.DirectoryPath).SubmitAsync(Ct));
            Assert.Equal(proof, await File.ReadAllBytesAsync(receiptPath, Ct)); Assert.Equal(original, await File.ReadAllBytesAsync(file, Ct));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedResponseIsProtocolFailureRatherThanTransportFallback(bool knownLength)
    {
        using var handler = new StubHandler((_, _) =>
        {
            HttpContent content = knownLength ? new StringContent("{}") : new StreamContent(new NonSeekable(new byte[8 * 1024 * 1024 + 1]));
            if (knownLength) content.Headers.ContentLength = 8 * 1024 * 1024 + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler); var api = new WebLibraryClient(http, new("http://library.test/"), null, TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<ClientApiException>(() => api.OrganizationInfoAsync(Ct)); Assert.Equal("response_too_large", error.Code);
    }

    [Fact]
    public void ActivitySizeCounterAndConfigurationRemainIndependentOfLocalStateAndResponseLimits()
    {
        var defaults = new ImportLimits(); Assert.Equal(9 * 1024 * 1024, defaults.MaximumRequestBytes);
        Assert.Throws<ArgumentException>(() => new ImportLimits(10).Validate()); Assert.Throws<ArgumentException>(() => new ImportLimits(MaximumItems: 5001).Validate());
        var error = Assert.Throws<ImportValidationException>(() => ImportValidation.Activity(Activity() with { Device = new(new string('x', 3000), "windows") }, new(1024)));
        Assert.Equal("import_too_large", error.Code);
        var large = Activity(501); ImportValidation.Activity(large, new(1024 * 1024, 600));
        Assert.Equal("import_item_limit", Assert.Throws<ImportValidationException>(() => ImportValidation.Activity(large, defaults)).Code);
    }

    private sealed class NonSeekable(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public int Reads { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { Reads++; return _inner.Read(buffer, offset, Math.Min(count, 4096)); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { Reads++; return _inner.ReadAsync(buffer[..Math.Min(buffer.Length, 4096)], ct); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class CountWrites(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int Writes { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post) Writes++;
            return base.SendAsync(request, ct);
        }
    }

    [Fact]
    public async Task ClientPreflightUsesDiscoveredLimitsWithoutSendingOversizedOriginalBatch()
    {
        using var dir = new TestDirectory(); await using var app = new Factory(dir.Path, 4096, 1);
        using var initial = app.CreateClient(); using var writes = new CountWrites(app.Server.CreateHandler()); using var http = new HttpClient(writes);
        var api = new WebLibraryClient(http, initial.BaseAddress!, "limit-test-key", TimeSpan.FromSeconds(10));
        using var payload = JsonDocument.Parse(Listing(200, 2));
        var metadata = await Assert.ThrowsAsync<ClientApiException>(() => api.SyncBatchAsync(payload.RootElement, "original-test-key", Ct));
        Assert.Equal("import_item_limit", metadata.Code);
        var activity = await Assert.ThrowsAsync<ClientApiException>(() => api.SubmitActivityAsync(Activity(2), Ct));
        Assert.Equal("import_item_limit", activity.Code); Assert.Equal(0, writes.Writes);
    }

    [Fact]
    public async Task DeclaredOversizeIsRejectedWithoutReadingAndCancellationCannotPersist()
    {
        using var dir = new TestDirectory(); await using var app = new Factory(dir.Path, 1024); using var http = Http(app);
        using var source = new NonSeekable([1, 2, 3]); using var body = new StreamContent(source);
        body.Headers.ContentType = new("application/json"); body.Headers.ContentLength = new ImportLimits(1024).MaximumRequestBytes + 1;
        using var response = await http.PostAsync("/api/v1/imports/preview", body, Ct); await Problem(response, 413, "request_too_large");
        // TestServer may serialize the client body, but it cannot allocate the declared giant length.
        Assert.InRange(source.Reads, 0, 2);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct); await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.PostAsync("/api/v1/sync/batches", Json(Listing(200)), cancel.Token));
        Assert.Equal(0, app.Services.GetRequiredService<SqliteLibraryStore>().Statistics().Novels);
    }
}
