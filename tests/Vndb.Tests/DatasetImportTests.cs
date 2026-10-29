using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

public sealed class DatasetImportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static HttpClient NoNetwork() => new(new StubHandler((_, _) => throw new InvalidOperationException("No upstream expected")));
    private static async Task<DatasetJob> Upload(DatasetImports service, string file, string key)
    { await using var stream = File.OpenRead(file); return await service.UploadAsync(stream, key, Ct); }
    private static async Task Finish(DatasetImports service, SqliteLibraryStore store)
    {
        for (var n = 0; n < 20 && store.DatasetJobs()[0].State is not ("completed" or "failed"); n++) await service.StepAsync(Ct);
    }
    [Fact]
    public async Task RestartPauseResumeAndSamePackageRetryPreservePersonalDataAndNewerApiText()
    {
        using var dir = new TestDirectory(); var data = Path.Combine(dir.Path, "live"); var store = new SqliteLibraryStore(data);
        var entry = StorageTests.Entry() with { UpdatedAt = DateTimeOffset.Parse("2026-10-28T00:00:00Z") }; store.Save(entry);
        store.ImportPlays(PlayTests.Batch()); var personal = JsonSerializer.Serialize(store.Plays(), VndbJson.Options);
        var items = JsonSerializer.Serialize(store.PersonalItems(), VndbJson.Options);
        var file = DumpPackageTests.Archive(dir.Path); using var http = NoNetwork();
        using (var first = new DatasetImports(store, http))
        {
            var job = await Upload(first, file, "upload-first"); Assert.Equal(job.Id, (await Upload(first, file, "upload-retry")).Id);
            await first.StepAsync(Ct); Assert.Equal("producer", store.DatasetJobs()[0].Phase);
            await first.StepAsync(Ct); var current = store.DatasetJobs()[0]; Assert.Equal(1, current.Processed);
            first.Control(job.Id, "pause", new(current.Revision), "pause"); Assert.False(await first.StepAsync(Ct));
        }
        store = new SqliteLibraryStore(data);
        using var resumed = new DatasetImports(store, http); var paused = store.DatasetJobs()[0];
        resumed.Control(paused.Id, "resume", new(paused.Revision), "resume"); await Finish(resumed, store);
        var completed = store.DatasetJobs()[0]; Assert.Equal("completed", completed.State); Assert.Equal(2, completed.Processed);
        Assert.Equal(entry.Novel.Title, store.Get("v17")!.Novel.Title); Assert.NotNull(store.Get("v17")!.Novel.ReleaseCatalog);
        Assert.Equal(personal, JsonSerializer.Serialize(store.Plays(), VndbJson.Options));
        Assert.Equal(items, JsonSerializer.Serialize(store.PersonalItems(), VndbJson.Options));
        Assert.Equal(completed.Id, (await Upload(resumed, file, "repeat-after-completion")).Id);
        Assert.False(await resumed.StepAsync(Ct));
    }
    [Fact]
    public async Task BadPackageCannotChangeActiveDataAndCancelledWorkDoesNotDeleteCommittedMetadata()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(Path.Combine(dir.Path, "db")); store.Save(StorageTests.Entry());
        var before = JsonSerializer.Serialize(store.Get("v17"), VndbJson.Options);
        using var http = NoNetwork(); using var service = new DatasetImports(store, http);
        await Upload(service, DumpPackageTests.Archive(dir.Path, f => f["db/vn.header"] = "changed"), "bad"); await service.StepAsync(Ct);
        var failed = store.DatasetJobs()[0]; Assert.Equal("failed", failed.State); Assert.Equal(before, JsonSerializer.Serialize(store.Get("v17"), VndbJson.Options));
        service.Control(failed.Id, "cancel", new(failed.Revision), "cancel-bad");
        await Upload(service, DumpPackageTests.Archive(dir.Path), "valid");
        await service.StepAsync(Ct); await service.StepAsync(Ct); await service.StepAsync(Ct); await service.StepAsync(Ct);
        var active = store.DatasetJobs()[0]; Assert.Equal(2, active.Processed);
        service.Control(active.Id, "cancel", new(active.Revision), "cancel-after-commit");
        Assert.False(await service.StepAsync(Ct)); Assert.NotNull(store.Get("v17"));
        Assert.Throws<PlayConflictException>(() => service.Control(active.Id, "resume", new(active.Revision), "stale-control"));
    }
    [Fact]
    public async Task ModifiedNormalizedRowsAfterInitialVerificationCannotEnterLibrary()
    {
        using var dir = new TestDirectory(); var root = Path.Combine(dir.Path, "db"); var store = new SqliteLibraryStore(root);
        using var http = NoNetwork(); using var service = new DatasetImports(store, http);
        await Upload(service, DumpPackageTests.Archive(dir.Path), "upload");
        await service.StepAsync(Ct); await service.StepAsync(Ct); // producer phase has verified and cached the immutable proofs
        var stage = Directory.GetDirectories(Path.Combine(root, "datasets"), "stage-*").Single();
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(stage, "stage.db") + ";Pooling=False"))
        { db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE normalized_novels SET entry_json=replace(entry_json,'Kana','Changed')"; command.ExecuteNonQuery(); }
        await service.StepAsync(Ct); await service.StepAsync(Ct);
        Assert.Equal("failed", store.DatasetJobs()[0].State); Assert.Null(store.Get("v17"));
    }
    [Fact]
    public async Task OfficialDownloadIsFixedOriginAndThrottlingDoesNotBusyLoop()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        using var handler = new StubHandler((_, _) =>
        {
            var response = StubHandler.Json("{}", HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler); using var service = new DatasetImports(store, http);
        service.Create(new("2026-10-27"), "create"); await service.StepAsync(Ct);
        var job = store.DatasetJobs()[0]; Assert.Equal("failed", job.State); Assert.NotNull(job.EarliestResumeAt);
        Assert.False(await service.StepAsync(Ct)); Assert.Single(handler.Requests);
        Assert.Equal("https://dl.vndb.org/dump/vndb-db-2026-10-27.tar.zst", handler.Requests[0].Uri); Assert.Null(handler.Requests[0].Authorization);
        Assert.Throws<PlayConflictException>(() => service.Control(job.Id, "resume", new(job.Revision), "too-soon"));
        Assert.Throws<ArgumentException>(() => service.Create(new("https://example.test/file"), "no-url"));
    }
    private sealed class Factory(string path) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b) => b.UseSetting("Library:DataDirectory", path)
            .UseSetting("Library:WriteKey", "test-only-library-key").UseSetting("CatalogJobs:Enabled", "false");
    }
    [Fact]
    public async Task UploadAndControlsRequireKeyAndApiNeverExposesStagingPaths()
    {
        using var dir = new TestDirectory(); await using var factory = new Factory(Path.Combine(dir.Path, "db")); using var http = factory.CreateClient();
        using var denied = await http.PostAsJsonAsync("/api/v1/datasets", new DatasetRequest("2026-10-27"), VndbJson.Options, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var api = new WebLibraryClient(http, http.BaseAddress!, "test-only-library-key", TimeSpan.FromSeconds(10));
        var job = await api.CreateDatasetAsync(new("2026-10-27"), "create", Ct);
        var paused = await api.ControlDatasetAsync(job.Id, "pause", new(job.Revision), "pause", Ct); Assert.Equal("paused", paused.State);
        var wire = JsonSerializer.Serialize(await api.DatasetJobsAsync(Ct), VndbJson.Options);
        Assert.DoesNotContain("source_file", wire); Assert.DoesNotContain("stage", wire); Assert.DoesNotContain(dir.Path, wire);
        await api.ControlDatasetAsync(job.Id, "cancel", new(paused.Revision), "cancel-before-upload", Ct);
        var archive = DumpPackageTests.Archive(dir.Path);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/datasets/upload");
        request.Headers.Add("X-Library-Request", "1"); request.Headers.Add("X-Library-Key", "test-only-library-key"); request.Headers.Add("Idempotency-Key", "binary-upload");
        request.Content = new ByteArrayContent(await File.ReadAllBytesAsync(archive, Ct)); request.Content.Headers.ContentType = new("application/octet-stream");
        using var uploaded = await http.SendAsync(request, Ct); Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var accepted = await uploaded.Content.ReadFromJsonAsync<DatasetJob>(VndbJson.Options, Ct);
        Assert.Equal("upload", accepted!.Source); Assert.Equal("queued", accepted.State); Assert.NotNull(accepted.Sha256);
        Assert.Equal(0, factory.Services.GetRequiredService<SqliteLibraryStore>().CollectionStatistics().PersonalItems);
    }
}
