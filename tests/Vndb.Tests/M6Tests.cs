using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Library;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class M6Tests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Id() => Guid.NewGuid().ToString("D");
    private sealed class Factory(string path) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Library:DataDirectory", path)
            .UseSetting("Library:WriteKey", "m6-test-key").UseSetting("CatalogJobs:Enabled", "false");
    }
    private sealed class Fixture : IDisposable
    {
        public readonly TestDirectory Dir = new();
        public readonly SqliteLibraryStore Store;
        public readonly LibraryDevice Device;
        public readonly PlayRecord Play;
        public Fixture(int? minutes = 60)
        {
            Store = new(Dir.Path);
            Device = Store.SaveDevice(new(Id(), new("Synthetic M6", "windows")), "device");
            var item = Store.SaveItem(new(new("Synthetic M6")), null, "item");
            Play = Store.SavePersonalPlay(new(new("Synthetic M6", null, "playing", null, null, minutes, null, null, null, null, null), item.Id), null, "play");
        }
        public SessionSample Sample(long sequence = 1, long seconds = 60, string state = "ended")
        {
            var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z"); var end = start.AddMinutes(2);
            return new(Id(), Device.Id, Play.Id, null, null, start, end, state is "active" or "paused" ? null : end, sequence, seconds, state);
        }
        public void Dispose() => Dir.Dispose();
    }
    [Fact]
    public void HundredDuplicatesOutOfOrderAndLateHeartbeatsNeverAddTimeOrReopenTerminal()
    {
        using var f = new Fixture(); var original = f.Store.GetPlay(f.Play.Id); var sample = f.Sample(state: "active");
        var first = f.Store.UpsertSession(sample);
        for (var i = 0; i < 100; i++) Assert.Equal(first, f.Store.UpsertSession(sample));
        f.Store.UpsertSession(sample with { Sequence = 3, CumulativeSeconds = 90 });
        Assert.Equal(90, f.Store.UpsertSession(sample with { Sequence = 2, CumulativeSeconds = 70 }).Sample.CumulativeSeconds);
        var ended = f.Store.UpsertSession(sample with { Sequence = 4, CumulativeSeconds = 95, State = "ended", EndedAt = sample.CheckpointAt });
        Assert.Equal(ended, f.Store.UpsertSession(sample with { Sequence = 99, CumulativeSeconds = 200 }));
        Assert.Throws<PlayConflictException>(() => f.Store.UpsertSession(ended.Sample with { CumulativeSeconds = 96 }));
        Assert.Equal(3695, f.Store.PlayTime(f.Play.Id).TotalSeconds);
        Assert.Equal(original, f.Store.GetPlay(f.Play.Id)); Assert.Single(f.Store.Sessions());
    }
    [Fact]
    public void UnknownBaseAdjustmentsAndReplayStayIndependent()
    {
        using var f = new Fixture(null); f.Store.UpsertSession(f.Sample()); var time = f.Store.PlayTime(f.Play.Id);
        Assert.Null(time.TotalSeconds); Assert.Equal(60, time.SessionSeconds);
        var adjustment = new TimeAdjustment(Id(), f.Play.Id, 30, "Synthetic correction", time.Revision);
        Assert.Equal(adjustment, f.Store.AdjustTime(adjustment, "adjust")); Assert.Equal(adjustment, f.Store.AdjustTime(adjustment, "adjust"));
        Assert.Throws<PlayConflictException>(() => f.Store.AdjustTime(adjustment with { Id = Id() }, "stale"));
        Assert.Null(f.Store.PlayTime(f.Play.Id).TotalSeconds); Assert.Equal(30, f.Store.PlayTime(f.Play.Id).AdjustmentSeconds);
        var replay = f.Store.SavePlay(PlayTests.Details() with { DurationMinutes = 0 });
        Assert.Equal(0, f.Store.PlayTime(replay.Id).TotalSeconds);
    }
    [Fact]
    public void SingleDeviceLeaseAndOverlappingDevicesRequireExplicitDecision()
    {
        using var f = new Fixture(); var first = f.Sample(state: "active"); f.Store.UpsertSession(first);
        Assert.Throws<PlayConflictException>(() => f.Store.UpsertSession(f.Sample(state: "active")));
        var secondDevice = f.Store.SaveDevice(new(Id(), new("Other synthetic", "windows")), "other");
        var second = f.Sample() with { DeviceId = secondDevice.Id }; f.Store.UpsertSession(second);
        Assert.Equal(2, f.Store.PlayTime(f.Play.Id).PendingSessions); Assert.Equal(0, f.Store.PlayTime(f.Play.Id).SessionSeconds);
        var closed = f.Store.UpsertSession(first with { Sequence = 2, State = "ended", EndedAt = first.CheckpointAt });
        f.Store.DecideSession(closed.Sample.Id, new(closed.Revision, true), "include");
        Assert.Equal(60, f.Store.PlayTime(f.Play.Id).SessionSeconds);
    }
    private sealed class Clock : ISessionClock
    {
        public long Ticks, Awake;
        public SessionClockReading Read() => new(Ticks, Awake);
        public void Advance(int seconds, int? awake = null) { Ticks += TimeSpan.FromSeconds(seconds).Ticks; Awake += TimeSpan.FromSeconds(awake ?? seconds).Ticks; }
    }
    [Fact]
    public void MonotonicPauseSleepAndUnobservedGapsNeverUseWallTime()
    {
        var clock = new Clock(); var counter = new SessionAccumulator(clock);
        clock.Advance(2); counter.Tick(true); counter.Pause(); clock.Advance(3); counter.Tick(true);
        Assert.Equal(TimeSpan.FromSeconds(2).Ticks, counter.ElapsedTicks);
        counter.Resume(); clock.Advance(2); counter.Tick(true); clock.Advance(3600, 1); counter.Tick(true);
        Assert.True(counter.Paused); Assert.Equal(TimeSpan.FromSeconds(4).Ticks, counter.ElapsedTicks);
        counter.Resume(); clock.Advance(6); counter.Tick(true); Assert.True(counter.Paused);
        counter.Resume(); clock.Advance(1); counter.Tick(false); Assert.Equal(TimeSpan.FromSeconds(4).Ticks, counter.ElapsedTicks);
    }
    [Fact]
    public void ProcessSelectionUsesPathCreationAndProvenParentNotBasenameOrReusedPid()
    {
        var now = DateTimeOffset.UtcNow; var root = new ProcessIdentity(100, now.UtcTicks, @"C:\Synthetic\wrapper.exe");
        var child = new ProcessIdentity(101, now.AddMilliseconds(1).UtcTicks, @"C:\Synthetic\game.exe");
        var tracker = new ProcessTracker(root, child.Executable);
        Assert.Equal(child, Assert.Single(tracker.Match([new(child, 100), new(root, 0)], now.AddSeconds(1))));
        Assert.Empty(new ProcessTracker(root, child.Executable).Match([new(child with { Executable = @"C:\Other\game.exe" }, 100), new(root, 0)], now.AddSeconds(1)));
        Assert.Empty(new ProcessTracker(root, child.Executable).Match([new(root with { CreatedUtcTicks = now.AddSeconds(1).UtcTicks }, 0), new(child, 100)], now.AddSeconds(2)));
        Assert.Single(new ProcessTracker(root, child.Executable).Match([new(child, 100)], now.AddSeconds(1), now.AddMilliseconds(500)));
        Assert.Equal(2, new ProcessTracker(root, child.Executable).Match([new(root, 0), new(child, 100), new(child with { Pid = 102 }, 100)], now.AddSeconds(1)).Count);
    }
    private sealed class Inventory(ProcessIdentity identity) : IProcessInventory
    { public IReadOnlyList<ProcessObservation> Snapshot() => [new(identity, 0)]; }
    [Fact]
    public async Task CrashRecoveryPreservesOnlyDurableSecondsAndDoesNotKillOrResumeProcess()
    {
        using var dir = new TestDirectory(); var clock = new Clock(); var outbox = new ActivityOutbox(dir.Path);
        var device = new ClientDeviceIdentity(Id(), "Synthetic", "windows", DateTimeOffset.UtcNow);
        var target = await outbox.CreateTargetAsync(device, "Synthetic", null, ct: Ct);
        var identity = new ProcessIdentity(99, DateTime.UtcNow.Ticks, @"C:\Synthetic\game.exe"); var inventory = new Inventory(identity);
        using (var manager = new LocalSessionManager(new(dir.Path), outbox, dir.Path, inventory, clock))
        {
            Assert.Throws<IOException>(() => new LocalSessionManager(new(dir.Path), outbox, dir.Path, inventory, clock));
            await manager.AttachAsync(identity, target, Ct);
            for (var i = 0; i < 31; i++) { clock.Advance(1); await manager.TickAsync(Ct); }
        }
        clock.Advance(3600);
        using var recovered = new LocalSessionManager(new(dir.Path), outbox, dir.Path, inventory, clock); await recovered.RecoverAsync(Ct);
        Assert.False(recovered.Running); Assert.Equal("interrupted", recovered.Current!.Sample.State);
        Assert.Equal(30, recovered.Current.Sample.CumulativeSeconds); Assert.Single(inventory.Snapshot());
    }
    [Fact]
    public async Task OfflineRoundTripImportsDependenciesIdempotentlyAndRetainsBothConflictVersions()
    {
        using var dir = new TestDirectory(); await using var app = new Factory(dir.Path); using var http = app.CreateClient();
        var store = app.Services.GetRequiredService<SqliteLibraryStore>(); var imports = app.Services.GetRequiredService<ActivityImports>();
        var outbox = new ActivityOutbox(Path.Combine(dir.Path, "client")); var device = new ClientDeviceIdentity(Id(), "Synthetic", "windows", DateTimeOffset.UtcNow);
        var target = await outbox.CreateTargetAsync(device, "Synthetic", null, ct: Ct);
        var now = DateTimeOffset.UtcNow;
        await outbox.SaveSessionAsync(target.Id, new(Id(), device.Id, target.PlayRef, null, null, now.AddMinutes(-2), now, now, 4, 65, "ended"), Ct);
        target = await outbox.EditAsync(target, "completed", "Offline review", Ct);
        var batch = Assert.Single(await outbox.BatchesAsync(ct: Ct)); var receipt = imports.Submit(batch, "test-instance");
        Assert.True(receipt.Complete); Assert.Equal(JsonSerializer.Serialize(receipt), JsonSerializer.Serialize(imports.Submit(batch, "test-instance")));
        var play = Assert.Single(store.Plays().Results); Assert.Equal("Offline review", play.Details.Review); Assert.Equal(65, store.PlayTime(play.Id).TotalSeconds);
        store.SavePlay(play.Details with { Review = "Web newer" }, play.Id, play.Revision);
        await outbox.EditAsync(target, "completed", "Offline newer", Ct);
        var conflictReceipt = imports.Submit(Assert.Single(await outbox.BatchesAsync(ct: Ct)), "test-instance");
        Assert.False(conflictReceipt.Complete); var conflict = Assert.Single(store.PlayEditConflicts());
        Assert.Equal("Web newer", conflict.Current.Details.Review); Assert.Equal("Offline newer", conflict.Incoming.Details.Review);
        var later = store.GetPlay(play.Id)!; store.SavePlay(later.Details with { Review = "Web latest" }, play.Id, later.Revision);
        Assert.Throws<PlayConflictException>(() => store.ResolvePlayEdit(conflict.Id, new(conflict.Revision, conflict.Current.Revision, false), "stale-decision"));
        conflict = Assert.Single(store.PlayEditConflicts()); Assert.Equal("Web latest", conflict.Current.Details.Review);
        store.ResolvePlayEdit(conflict.Id, new(conflict.Revision, conflict.Current.Revision, false), "keep-web");
        Assert.Equal("Web latest", store.GetPlay(play.Id)!.Details.Review); Assert.Single(store.Sessions());
    }
    [Fact]
    public async Task HttpSyncAcknowledgesOnlyReceiptsAndKeepsExportableOriginals()
    {
        using var dir = new TestDirectory(); await using var app = new Factory(dir.Path); using var http = app.CreateClient();
        var outbox = new ActivityOutbox(Path.Combine(dir.Path, "client")); var device = new ClientDeviceIdentity(Id(), "Synthetic", "windows", DateTimeOffset.UtcNow);
        var target = await outbox.CreateTargetAsync(device, "Synthetic", null, ct: Ct); await outbox.EditAsync(target, "playing", "Review", Ct);
        var web = new WebLibraryClient(http, http.BaseAddress!, "m6-test-key", TimeSpan.FromSeconds(30));
        Assert.True(Assert.Single(await outbox.SyncAsync(web, http.BaseAddress!.AbsoluteUri, Ct)).Complete);
        Assert.Empty(await outbox.BatchesAsync(ct: Ct)); Assert.Single(await outbox.BatchesAsync(true, Ct));
        Assert.Equal(1, await outbox.ExportAsync(Path.Combine(dir.Path, "export"), Ct));
        var response = await http.PostAsJsonAsync("/api/v1/activity/batches", Assert.Single(await outbox.BatchesAsync(true, Ct)), VndbJson.Options, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    [Fact]
    public async Task MalformedActivityIsRejectedWithoutPartialBatchAndKeysCannotChangePayload()
    {
        using var dir = new TestDirectory(); await using var app = new Factory(dir.Path); using var http = app.CreateClient();
        var imports = app.Services.GetRequiredService<ActivityImports>(); var store = app.Services.GetRequiredService<SqliteLibraryStore>();
        var batch = new ActivityBatch { BatchId = Id(), DeviceId = Id(), Device = new("Synthetic", "windows"), Items = [new("item", new("Synthetic"))] };
        Assert.Throws<ArgumentException>(() => imports.Submit(batch with { Items = [null!] }, "test")); Assert.Null(store.ActivityReceipt(batch.BatchId));
        Assert.True(imports.Submit(batch, "test").Complete);
        Assert.Throws<PlayConflictException>(() => imports.Submit(batch with { Items = [new("item", new("Changed"))] }, "test"));
        Assert.Equal("conflict", Assert.Single(imports.Submit(batch with { BatchId = Id(), Items = [new("item", new("Changed"))] }, "test").Results).Status);
    }
    [Fact]
    public async Task MissingDependenciesRetryAndInstallationComponentIdsMapBeforeSession()
    {
        using var dir = new TestDirectory(); await using var app = new Factory(dir.Path); using var http = app.CreateClient();
        var imports = app.Services.GetRequiredService<ActivityImports>(); var store = app.Services.GetRequiredService<SqliteLibraryStore>();
        var device = Id(); var now = DateTimeOffset.UtcNow;
        var batch = new ActivityBatch { BatchId = Id(), DeviceId = device, Device = new("Synthetic", "windows"),
            Plays = [new("play", "client:item", new("Synthetic", null, "playing", null, null, 0, null, null, null, null, null))],
            Installations = [new("install", new(device, @"C:\Synthetic", "Synthetic"), [new("client:item", ".")])],
            PlaySessions = [new(Id(), device, "client:play", "client:install", "client:install.0", now.AddMinutes(-1), now, now, 1, 30, "ended")] };
        Assert.False(imports.Submit(batch, "test").Complete); Assert.Empty(store.Sessions());
        Assert.True(imports.Submit(batch with { BatchId = Id(), Plays = [], Installations = [], PlaySessions = [], Items = [new("item", new("Synthetic"))] }, "test").Complete);
        Assert.True(imports.Submit(batch, "test").Complete);
        var session = Assert.Single(store.Sessions()); Assert.Equal(Assert.Single(store.Components()).Id, session.Sample.ComponentId);
        Assert.Equal(30, store.PlayTime(session.Sample.PlayId).TotalSeconds);
    }
    private sealed class TamperHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.RequestUri!.AbsolutePath == "/api/v1/activity/batches")
            {
                var receipt = (await response.Content.ReadFromJsonAsync<ActivityReceipt>(VndbJson.Options, ct))!;
                response.Content.Dispose(); response.Content = JsonContent.Create(receipt with { Results = [] }, options: VndbJson.Options);
            }
            return response;
        }
    }
    [Fact]
    public async Task MalformedReceiptsCannotAcknowledgePendingOperations()
    {
        using var dir = new TestDirectory(); await using var app = new Factory(dir.Path); using var initial = app.CreateClient();
        using var http = new HttpClient(new TamperHandler(app.Server.CreateHandler())) { BaseAddress = initial.BaseAddress };
        var outbox = new ActivityOutbox(Path.Combine(dir.Path, "client"));
        var target = await outbox.CreateTargetAsync(new(Id(), "Synthetic", "windows", DateTimeOffset.UtcNow), "Synthetic", null, ct: Ct);
        await outbox.EditAsync(target, "playing", "Synthetic", Ct);
        var web = new WebLibraryClient(http, http.BaseAddress!, "m6-test-key", TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ClientApiException>(() => outbox.SyncAsync(web, http.BaseAddress!.AbsoluteUri, Ct));
        Assert.Single(await outbox.BatchesAsync(ct: Ct));
    }
}
