using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vndb.Core;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

public sealed class WebTests
{
    [Fact]
    public async Task BackgroundImportAndQueueControlsAreAuthenticatedAndExportDurableJson()
    {
        using var directory = new TestDirectory(); await using var factory = new Factory(directory.Path);
        using var http = factory.CreateClient(); var ct = TestContext.Current.CancellationToken;
        using var emptyJob = await http.GetAsync("/api/catalog/jobs/latest", ct);
        Assert.Equal("null", await emptyJob.Content.ReadAsStringAsync(ct));
        using var deniedJob = await http.PostAsJsonAsync("/api/catalog/jobs", new CatalogJobRequest(), VndbJson.Options, ct);
        using var deniedSearch = await http.PostAsJsonAsync("/api/search", new LibrarySearchRequest("作品"), VndbJson.Options, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedJob.StatusCode); Assert.Equal(HttpStatusCode.Unauthorized, deniedSearch.StatusCode);
        var local = await http.GetFromJsonAsync<LibrarySearchResult>("/api/search?q=missing", VndbJson.Options, ct);
        Assert.True(local!.NeedsRemote);
        http.DefaultRequestHeaders.Add("X-Library-Request", "1"); http.DefaultRequestHeaders.Add("X-Library-Key", "test-only-library-key");
        using var imported = await http.PostAsJsonAsync("/api/plays/imports?background=true", PlayTests.Batch(), VndbJson.Options, ct);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var job = await http.GetFromJsonAsync<CatalogJobSummary>("/api/catalog/jobs/latest", VndbJson.Options, ct);
        Assert.Equal(1, job!.Total); Assert.Equal(0, job.Completed); Assert.Equal(5, job.IntervalSeconds);
        using var pause = await http.PostAsync("/api/catalog/jobs/" + job.Id + "/pause", null, ct);
        Assert.Equal("paused", (await pause.Content.ReadFromJsonAsync<CatalogJobSummary>(VndbJson.Options, ct))!.Status);
        using var resume = await http.PostAsync("/api/catalog/jobs/" + job.Id + "/resume", null, ct);
        Assert.Equal("queued", (await resume.Content.ReadFromJsonAsync<CatalogJobSummary>(VndbJson.Options, ct))!.Status);
        var exported = await http.GetFromJsonAsync<CatalogJob>("/api/catalog/jobs/" + job.Id + "/export", VndbJson.Options, ct);
        Assert.Equal("作品名", Assert.Single(exported!.Tasks).Title);
    }

    [Fact]
    public async Task CatalogApiAuthenticatesLocalImportAndPersistsMetadataAndDirectoryAssociation()
    {
        using var directory = new TestDirectory(); await using var factory = new Factory(directory.Path);
        using var http = factory.CreateClient(); var ct = TestContext.Current.CancellationToken;
        var batch = new LocalGameBatch([new("PC", @"E:\会社\作品名", "作品名", "会社", DateTimeOffset.UtcNow, "inventory.json", new string('a', 64))]);
        using var denied = await http.PostAsJsonAsync("/api/catalog/local/imports", batch, VndbJson.Options, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        http.DefaultRequestHeaders.Add("X-Library-Request", "1"); http.DefaultRequestHeaders.Add("X-Library-Key", "test-only-library-key");
        using var imported = await http.PostAsJsonAsync("/api/catalog/local/imports", batch, VndbJson.Options, ct);
        Assert.Equal(HttpStatusCode.Gone, imported.StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<SqliteLibraryStore>().LocalGames());
        using var local = await http.GetAsync("/api/catalog/local", ct); Assert.Equal(HttpStatusCode.Gone, local.StatusCode);

    }

    [Fact]
    public async Task PlayApiProtectsWritesRetainsSourceAndRejectsConflictingEdits()
    {
        using var directory = new TestDirectory();
        await using var factory = new Factory(directory.Path);
        using var http = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        using var denied = await http.PostAsJsonAsync("/api/plays/imports", PlayTests.Batch(), VndbJson.Options, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var deniedEdit = await http.PutAsJsonAsync("/api/plays/missing", new PlayEdit(PlayTests.Details(), 1), VndbJson.Options, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedEdit.StatusCode);
        http.DefaultRequestHeaders.Add("X-Library-Request", "1");
        http.DefaultRequestHeaders.Add("X-Library-Key", "test-only-library-key");
        using var imported = await http.PostAsJsonAsync("/api/plays/imports", PlayTests.Batch(), VndbJson.Options, ct);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var page = await http.GetFromJsonAsync<PlayPage>("/api/plays?year=2024&played=true&unmatched=true", VndbJson.Options, ct);
        var first = Assert.Single(page!.Results);
        Assert.Null(first.Details.DurationMinutes); Assert.Null(first.Details.Score);
        var edit = new PlayEdit(first.Details with { DurationMinutes = 90, Score = 8.5m, Review = "个人评价 <script>" }, first.Revision);
        using var updated = await http.PutAsJsonAsync("/api/plays/" + first.Id, edit, VndbJson.Options, ct);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var conflict = await http.PutAsJsonAsync("/api/plays/" + first.Id, edit, VndbJson.Options, ct);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var repeated = await http.PostAsJsonAsync("/api/plays/imports", PlayTests.Batch(), VndbJson.Options, ct);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var result = await repeated.Content.ReadFromJsonAsync<PlayImportResult>(VndbJson.Options, ct);
        Assert.Equal(0, result!.Inserted); Assert.Equal(2, result.Existing);
        var saved = await http.GetFromJsonAsync<PlayRecord>("/api/plays/" + first.Id, VndbJson.Options, ct);
        Assert.Equal(8.5m, saved!.Details.Score); Assert.Equal(90, saved.Details.DurationMinutes);
        Assert.Equivalent(first.Source, saved.Source);
        using var invalid = await http.PostAsJsonAsync("/api/plays", new PlayEdit(PlayTests.Details() with { Score = 11 }, null), VndbJson.Options, ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private sealed class Factory(string directory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Library:DataDirectory", directory).UseSetting("Library:WriteKey", "test-only-library-key").UseSetting("CatalogJobs:Enabled", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<VndbClient>();
                services.RemoveAll<CoverDownloader>();
                services.AddSingleton(_ => new VndbClient(new HttpClient(ImportTests.Upstream()), VndbClientTests.FastOptions));
                services.AddSingleton(_ => new CoverDownloader(new HttpClient(new StubHandler((_, _) => Task.FromResult(ImportTests.Image()))), directory));
            });
        }
    }

    [Fact]
    public async Task WebSupportsAuthenticatedImportOfflineSearchCoverAndExport()
    {
        using var directory = new TestDirectory();
        await using var factory = new Factory(directory.Path);
        using var http = factory.CreateClient();
        using var denied = await http.PostAsync("/api/imports/v17", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        http.DefaultRequestHeaders.Add("X-Library-Request", "1");
        http.DefaultRequestHeaders.Add("X-Library-Key", "test-only-library-key");
        using var imported = await http.PostAsync("/api/imports/v17", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        using var list = await http.GetAsync("/api/library?q=作品名&producer=", TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, document.RootElement.GetProperty("total").GetInt32());
        using var image = await http.GetAsync("/api/library/v17/cover", TestContext.Current.CancellationToken);
        Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        Assert.NotNull(image.Headers.ETag);
        using var export = await http.GetAsync("/api/library/v17/export", TestContext.Current.CancellationToken);
        Assert.Equal("application/json", export.Content.Headers.ContentType!.MediaType);
        using var missing = await http.GetAsync("/api/library/v99", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var invalid = await http.GetAsync("/api/library?limit=101", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("frame-ancestors 'none'", image.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task StaticFrontendAndHealthEndpointAreServed()
    {
        using var directory = new TestDirectory();
        await using var factory = new Factory(directory.Path);
        using var http = factory.CreateClient();
        using var page = await http.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.EndsWith("/manage.html", page.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains("我的书架", await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var catalogue = await http.GetAsync("/?view=local", TestContext.Current.CancellationToken);
        Assert.Contains("公共资料目录", await catalogue.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var health = await http.GetAsync("/healthz", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task ArchiveApiRequiresAuthenticationAndImportedNovelAndPersistsIdempotently()
    {
        using var directory = new TestDirectory();
        await using var factory = new Factory(directory.Path);
        using var http = factory.CreateClient();
        var registration = ArchiveTests.Registration("archived");
        using var denied = await http.PostAsJsonAsync("/api/archives", registration, VndbJson.Options, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        http.DefaultRequestHeaders.Add("X-Library-Request", "1");
        http.DefaultRequestHeaders.Add("X-Library-Key", "test-only-library-key");
        using var missing = await http.PostAsJsonAsync("/api/archives", registration, VndbJson.Options, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Gone, missing.StatusCode);
        using var rows = await http.GetAsync("/api/archives", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Gone, rows.StatusCode); Assert.Empty(factory.Services.GetRequiredService<SqliteLibraryStore>().Archives());

    }
}
