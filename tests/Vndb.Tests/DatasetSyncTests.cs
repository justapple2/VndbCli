using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

public sealed class DatasetSyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-06T12:00:00Z"); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Probe : HttpMessageHandler
    {
        public int Calls; public string Date = "2026-11-06";
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Assert.Equal(HttpMethod.Head, request.Method);
            Assert.Equal("https://dl.vndb.org/dump/vndb-db-latest.tar.zst", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("X-Library-Key"));
            return Reply?.Invoke(request, ct) ?? Task.FromResult(Redirect("https://dl.vndb.org/dump/vndb-db-" + Date + ".tar.zst"));
        }
    }
    private static HttpResponseMessage Redirect(string target)
    { var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect); response.Headers.Location = new(target, UriKind.RelativeOrAbsolute); return response; }

    [Fact]
    public async Task StatusIsPassiveAndScheduleDefaultsOffWithValidatedRevisionAndInterval()
    {
        using var dir = new TestDirectory(); var db = new SqliteLibraryStore(dir.Path); var clock = new Clock(); using var probe = new Probe(); using var http = new HttpClient(probe);
        using var service = new DatasetImports(db, http, clock);
        var status = service.SyncStatus(); Assert.False(status.Settings.Enabled); Assert.Equal(7, status.Settings.IntervalDays); Assert.Null(status.UpdateAvailable);
        await service.StepSyncAsync(Ct); Assert.Equal(0, probe.Calls); Assert.Empty(db.DatasetJobs());
        Assert.Throws<ArgumentException>(() => service.ConfigureSync(new(0, true, 0), "invalid"));
        var settings = service.ConfigureSync(new(0, true, 7), "enabled");
        Assert.Equal(settings, service.ConfigureSync(new(0, true, 7), "enabled"));
        Assert.Throws<PlayConflictException>(() => service.ConfigureSync(new(0, false, 7), "stale"));
        Assert.True(new DatasetImports(db, http, clock).SyncStatus().Settings.Enabled);
    }
    [Fact]
    public async Task LatestProbeUsesHeadOnlyAndCooldownAndOperationProofSurviveRestart()
    {
        using var dir = new TestDirectory(); var db = new SqliteLibraryStore(dir.Path); var clock = new Clock(); using var probe = new Probe(); using var http = new HttpClient(probe);
        using (var service = new DatasetImports(db, http, clock))
        {
            var status = await service.CheckLatestAsync("first", Ct); Assert.Equal("2026-11-06", status.LatestSnapshotDate); Assert.True(status.UpdateAvailable);
            Assert.Empty(db.DatasetJobs()); Assert.Equal(clock.Now, status.LastCheckedAt);
            await service.CheckLatestAsync("second", Ct); Assert.Equal(1, probe.Calls);
        }
        clock.Now += TimeSpan.FromDays(1); probe.Date = "2026-11-07";
        using var restarted = new DatasetImports(new(dir.Path), http, clock);
        Assert.Equal("2026-11-06", (await restarted.CheckLatestAsync("first", Ct)).LatestSnapshotDate); Assert.Equal(1, probe.Calls);
        Assert.Equal("2026-11-07", (await restarted.CheckLatestAsync("fresh", Ct)).LatestSnapshotDate); Assert.Equal(2, probe.Calls);
    }
    [Theory]
    [InlineData("https://example.test/dump/vndb-db-2026-11-06.tar.zst")]
    [InlineData("http://dl.vndb.org/dump/vndb-db-2026-11-06.tar.zst")]
    [InlineData("https://dl.vndb.org:8443/dump/vndb-db-2026-11-06.tar.zst")]
    [InlineData("https://user:secret@dl.vndb.org/dump/vndb-db-2026-11-06.tar.zst")]
    [InlineData("/dump/vndb-db-2026-11-06.tar.zst?other=1")]
    [InlineData("/dump/vndb-db-2026-11-06.tar.zst#other")]
    [InlineData("/dump/vndb-db-2099-01-01.tar.zst")]
    [InlineData("/dump/vndb-db-2026-02-31.tar.zst")]
    public async Task UnknownRedirectsAreNotFollowedOrQueued(string location)
    {
        using var dir = new TestDirectory(); var db = new SqliteLibraryStore(dir.Path); db.Save(StorageTests.Entry());
        var original = JsonSerializer.Serialize(db.Get("v17"), VndbJson.Options);
        using var probe = new Probe { Reply = (_, _) => Task.FromResult(Redirect(location)) }; using var http = new HttpClient(probe); using var service = new DatasetImports(db, http, new Clock());
        Assert.Equal("check_failed", (await service.UpdateLatestAsync("bad", Ct)).State); Assert.NotNull(service.SyncStatus().Error);
        Assert.Equal(1, probe.Calls); Assert.Empty(db.DatasetJobs()); Assert.Equal(original, JsonSerializer.Serialize(db.Get("v17"), VndbJson.Options));
    }
    [Fact]
    public async Task FailedCheckKeepsLastKnownDateButCannotQueueUsingItAndHonorsRetryAfter()
    {
        using var dir = new TestDirectory(); var db = new SqliteLibraryStore(dir.Path); var clock = new Clock(); using var probe = new Probe(); using var http = new HttpClient(probe);
        using var service = new DatasetImports(db, http, clock); await service.CheckLatestAsync("good", Ct); clock.Now += TimeSpan.FromMinutes(16);
        probe.Reply = (_, _) => { var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(3)); return Task.FromResult(response); };
        var result = await service.UpdateLatestAsync("limited", Ct); Assert.Equal("check_failed", result.State); Assert.Empty(db.DatasetJobs());
        var status = service.SyncStatus(); Assert.Equal("2026-11-06", status.LatestSnapshotDate); Assert.Equal(clock.Now.AddHours(3), status.CheckNotBefore);
        using var restarted = new DatasetImports(new(dir.Path), http, clock);
        await restarted.CheckLatestAsync("before-wait", Ct); Assert.Equal(2, probe.Calls);
        clock.Now += TimeSpan.FromHours(3); probe.Reply = null;
        Assert.Null((await restarted.CheckLatestAsync("after-wait", Ct)).Error); Assert.Equal(3, probe.Calls);
    }
    [Fact]
    public async Task UpdateDeduplicatesExactSnapshotAndLostReplyCannotRetargetTheOperation()
    {
        using var dir = new TestDirectory(); var db = new SqliteLibraryStore(dir.Path); var clock = new Clock(); using var probe = new Probe(); using var http = new HttpClient(probe); using var service = new DatasetImports(db, http, clock);
        var first = await service.UpdateLatestAsync("original", Ct); Assert.Equal("queued", first.State); Assert.Single(db.DatasetJobs());
        var again = await service.UpdateLatestAsync("another", Ct); Assert.Equal("existing", again.State); Assert.Equal(first.Job!.Id, again.Job!.Id); Assert.Equal(1, probe.Calls);
        clock.Now += TimeSpan.FromDays(1); probe.Date = "2026-11-07"; await service.CheckLatestAsync("later-discovery", Ct);
        Assert.Equal(first, await service.UpdateLatestAsync("original", Ct)); Assert.Single(db.DatasetJobs());
        Assert.Equal("blocked", (await service.UpdateLatestAsync("newer-update", Ct)).State); Assert.Single(db.DatasetJobs());
        service.Control(first.Job.Id, "pause", new(first.Job.Revision), "pause");
        Assert.Equal("blocked", (await service.UpdateLatestAsync("keep-paused", Ct)).State); Assert.Equal("paused", db.DatasetJobs()[0].State);
    }
    [Fact]
    public async Task AutomaticSyncUsesExistingQueueAndPersistsItsCadenceWithoutBusyPollingUpstream()
    {
        using var dir = new TestDirectory(); var db = new SqliteLibraryStore(dir.Path); var clock = new Clock(); using var probe = new Probe(); using var http = new HttpClient(probe);
        using var service = new DatasetImports(db, http, clock); service.ConfigureSync(new(0, true, 7), "enable");
        await service.StepSyncAsync(Ct); var job = Assert.Single(db.DatasetJobs()); Assert.Equal(1, probe.Calls); Assert.Equal(clock.Now.AddDays(7), service.SyncStatus().NextCheckAt);
        using var restarted = new DatasetImports(new(dir.Path), http, clock); clock.Now += TimeSpan.FromHours(2);
        for (var n = 0; n < 100; n++) await restarted.StepSyncAsync(Ct); Assert.Equal(1, probe.Calls); Assert.Single(db.DatasetJobs());
        service.Control(job.Id, "cancel", new(job.Revision), "cancel"); clock.Now += TimeSpan.FromDays(7); probe.Date = "2026-11-13";
        await restarted.StepSyncAsync(Ct); Assert.Equal(2, probe.Calls); Assert.Equal(2, db.DatasetJobs().Count);
        Assert.Equal("vndb-db-2026-11-13.tar.zst", db.DatasetJobs()[0].Source);
    }
    [Fact]
    public async Task CancelledAutomaticCheckAndDisabledWorkerNeverRestartARequestStorm()
    {
        using var dir = new TestDirectory(); var db = new SqliteLibraryStore(dir.Path); var clock = new Clock(); using var probe = new Probe(); using var http = new HttpClient(probe);
        using var service = new DatasetImports(db, http, clock); service.ConfigureSync(new(0, true, 1), "enable");
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        probe.Reply = (_, _) => { cancelled.Cancel(); throw new OperationCanceledException(cancelled.Token); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StepSyncAsync(cancelled.Token));
        Assert.Equal(clock.Now.AddHours(1), service.SyncStatus().NextCheckAt);
        using var restarted = new DatasetImports(new(dir.Path), http, clock);
        clock.Now += TimeSpan.FromMinutes(5); await restarted.StepSyncAsync(Ct); Assert.Equal(1, probe.Calls);
        using var disabled = new DatasetImports(db, http, clock, false);
        Assert.Throws<ArgumentException>(() => disabled.ConfigureSync(new(1, true, 1), "disabled"));
        disabled.ConfigureSync(new(1, false, 1), "turn-off"); clock.Now += TimeSpan.FromDays(2); await disabled.StepSyncAsync(Ct);
        Assert.Equal(1, probe.Calls); Assert.False(disabled.SyncStatus().WorkerEnabled); Assert.Null(disabled.SyncStatus().NextCheckAt);
    }
    [Fact]
    public async Task DisablingScheduleDuringProbeIsNotUndoneByTheLateResult()
    {
        using var dir = new TestDirectory(); var db = new SqliteLibraryStore(dir.Path); var clock = new Clock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var finish = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var probe = new Probe { Reply = (_, _) => { entered.SetResult(); return finish.Task; } }; using var http = new HttpClient(probe); using var service = new DatasetImports(db, http, clock);
        service.ConfigureSync(new(0, true, 7), "enable"); var check = service.StepSyncAsync(Ct); await entered.Task.WaitAsync(Ct);
        service.ConfigureSync(new(1, false, 7), "disable"); finish.SetResult(Redirect("vndb-db-2026-11-06.tar.zst")); await check;
        Assert.False(service.SyncStatus().Settings.Enabled); Assert.Null(service.SyncStatus().NextCheckAt); Assert.Equal("2026-11-06", service.SyncStatus().LatestSnapshotDate); Assert.Empty(db.DatasetJobs());
    }
    [Fact]
    public async Task InterruptedCheckCannotUseItsPreviousSuccessfulDateToQueueAnUpdate()
    {
        using var dir = new TestDirectory(); var db = new SqliteLibraryStore(dir.Path); var clock = new Clock(); using var probe = new Probe(); using var http = new HttpClient(probe); using var service = new DatasetImports(db, http, clock);
        await service.CheckLatestAsync("successful", Ct);
        using (var connection = new SqliteConnection("Data Source=" + Path.Combine(dir.Path, "library.db") + ";Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE dataset_sync SET record_json=json_set(record_json,'$.check_token','interrupted','$.check_lease_until',$until) WHERE id=1";
            command.Parameters.AddWithValue("$until", clock.Now.AddMinutes(-1).ToString("O")); command.ExecuteNonQuery();
        }
        Assert.NotNull(service.SyncStatus().Error); Assert.False(service.SyncStatus().Checking);
        Assert.Equal("check_failed", (await service.UpdateLatestAsync("after-interruption", Ct)).State); Assert.Empty(db.DatasetJobs()); Assert.Equal(1, probe.Calls);
    }
    [Fact]
    public async Task FullImportCompletionIsTheFreshnessMarkerAndPersonalRecordsStayUntouched()
    {
        using var dir = new TestDirectory(); var db = new SqliteLibraryStore(Path.Combine(dir.Path, "db")); db.ImportPlays(PlayTests.Batch());
        var original = StorageTests.Entry(); db.Save(original with { Novel = original.Novel with { Rating = 84, VoteCount = 200 }, UpdatedAt = DateTimeOffset.Parse("2026-10-26T00:00:00Z") });
        var personal = JsonSerializer.Serialize(db.Plays(), VndbJson.Options); var clock = new Clock(); using var probe = new Probe { Date = "2026-10-27" }; using var http = new HttpClient(probe); using var service = new DatasetImports(db, http, clock);
        await using (var input = File.OpenRead(DumpPackageTests.Archive(dir.Path))) await service.UploadAsync(input, "upload", Ct);
        Assert.Null(service.SyncStatus().CompletedSnapshotAt);
        for (var n = 0; n < 20 && db.DatasetJobs()[0].State != "completed"; n++) await service.StepAsync(Ct);
        Assert.Equal("completed", db.DatasetJobs()[0].State); Assert.NotNull(service.SyncStatus().CompletedSnapshotAt);
        Assert.Equal("current", (await service.UpdateLatestAsync("already-current", Ct)).State); Assert.False(service.SyncStatus().UpdateAvailable); Assert.Single(db.DatasetJobs());
        Assert.Equal(personal, JsonSerializer.Serialize(db.Plays(), VndbJson.Options));
        Assert.Equal(84, db.Get("v17")!.Novel.Rating); Assert.Equal(200, db.Get("v17")!.Novel.VoteCount);
    }
    [Fact]
    public async Task SchemaFourteenUpgradeAndSnapshotRestoreRetainTheSyncStateAndExistingData()
    {
        using var dir = new TestDirectory(); var live = Path.Combine(dir.Path, "live"); var db = new SqliteLibraryStore(live); db.Save(StorageTests.Entry());
        var original = JsonSerializer.Serialize(db.Get("v17"), VndbJson.Options);
        using (var connection = new SqliteConnection("Data Source=" + Path.Combine(live, "library.db") + ";Pooling=False"))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "DROP TABLE dataset_sync; PRAGMA user_version=14;"; command.ExecuteNonQuery(); }
        db = new(live); Assert.Equal(original, JsonSerializer.Serialize(db.Get("v17"), VndbJson.Options));
        var clock = new Clock(); using var probe = new Probe(); using var http = new HttpClient(probe); using var service = new DatasetImports(db, http, clock);
        service.ConfigureSync(new(0, true, 30), "save"); await service.CheckLatestAsync("check", Ct);
        File.WriteAllText(Path.Combine(live, "service-instance-id"), Guid.NewGuid().ToString("D"));
        var snapshot = Path.Combine(dir.Path, "snapshot"); var restored = Path.Combine(dir.Path, "restored");
        Assert.Equal(15, LibrarySnapshot.Create(live, snapshot, "synthetic-image", cancellationToken: Ct).Schema);
        LibrarySnapshot.Restore(snapshot, restored, cancellationToken: Ct);
        Assert.Equal(service.SyncStatus(), new SqliteLibraryStore(restored).DatasetSyncStatus(clock.Now));
    }
    private sealed class Factory(string path, HttpClient upstream, Clock clock) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b) => b.UseSetting("Library:DataDirectory", path).UseSetting("Library:WriteKey", "test-only-library-key")
            .UseSetting("CatalogJobs:Enabled", "false").ConfigureServices(s => { s.RemoveAll<DatasetImports>(); s.AddSingleton(p => new DatasetImports(p.GetRequiredService<SqliteLibraryStore>(), upstream, clock)); });
    }
    [Fact]
    public async Task ApiIsAuthenticatedIdempotentAndStatusNeverMakesUpstreamRequests()
    {
        using var dir = new TestDirectory(); using var probe = new Probe(); using var upstream = new HttpClient(probe); await using var factory = new Factory(dir.Path, upstream, new()); using var http = factory.CreateClient();
        var status = await http.GetFromJsonAsync<DatasetSyncStatus>("/api/v1/datasets/sync", VndbJson.Options, Ct); Assert.False(status!.Settings.Enabled); Assert.Equal(0, probe.Calls);
        using var denied = await http.PostAsync("/api/v1/datasets/sync/check", null, Ct); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var client = new WebLibraryClient(http, http.BaseAddress!, "test-only-library-key", TimeSpan.FromSeconds(10));
        Assert.Contains("dataset_sync", (await client.OrganizationInfoAsync(Ct)).Capabilities);
        var settings = await client.ConfigureDatasetSyncAsync(new(0, true, 7), "save", Ct); Assert.Equal(1, settings.Revision);
        Assert.Equal("2026-11-06", (await client.CheckDatasetAsync("check", Ct)).LatestSnapshotDate);
        var result = await client.UpdateDatasetAsync("update", Ct); Assert.Equal("queued", result.State); Assert.Equal(result, await client.UpdateDatasetAsync("update", Ct)); Assert.Equal(1, probe.Calls);
        var wire = JsonSerializer.Serialize(await client.DatasetSyncAsync(Ct), VndbJson.Options); Assert.DoesNotContain(dir.Path, wire); Assert.DoesNotContain("check_token", wire); Assert.DoesNotContain("stage", wire);
    }
}
