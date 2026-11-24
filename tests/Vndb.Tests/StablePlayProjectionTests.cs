using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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

public sealed class StablePlayProjectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static PersonalPlayData Details(string? vn = null, string status = "played", int? minutes = null, string title = "同名作品") =>
        new(title, vn, status, null, null, minutes, null, "synthetic review", null, null, "ja");
    private sealed class Factory(string path) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b) => b.UseSetting("Library:DataDirectory", path)
            .UseSetting("Library:WriteKey", "projection-test-key").UseSetting("CatalogJobs:Enabled", "false");
    }
    // The shape shipped before P2.3; extra read fields must remain optional to old consumers.
    private sealed record OldPlay(string Id, PersonalPlayData Details, long Revision, DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt = null, PlayTimeSummary? Time = null);
    private sealed class ReplySwap(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool Wrong { get; set; } = true;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var result = await base.SendAsync(request, ct);
            if (Wrong && request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/v1/plays")
            {
                var saved = (await result.Content.ReadFromJsonAsync<PersonalPlayView>(VndbJson.Options, ct))!;
                result.Content.Dispose(); result.Content = JsonContent.Create(saved with { ItemId = "other-item" }, options: VndbJson.Options);
            }
            return result;
        }
    }
    [Fact]
    public async Task WrongStableItemInWriteReceiptCannotAcknowledgeDraftAndOriginalIdCanRetry()
    {
        using var dir = new TestDirectory(); using var local = new TestDirectory(); await using var app = new Factory(dir.Path); using var initial = app.CreateClient();
        using var swap = new ReplySwap(app.Server.CreateHandler()); using var http = new HttpClient(swap) { BaseAddress = initial.BaseAddress };
        var store = app.Services.GetRequiredService<SqliteLibraryStore>(); var item = store.SaveItem(new(new("同名作品")), null, "item");
        var api = new WebLibraryClient(http, http.BaseAddress!, "projection-test-key", TimeSpan.FromSeconds(10));
        var edits = new PersonalEditOutbox(local.Path); var draft = await edits.EnqueueAsync(item.Id, http.BaseAddress!.AbsoluteUri, null, new(Details(), item.Id), Ct);
        var error = await Assert.ThrowsAsync<ClientApiException>(() => edits.SyncOneAsync(api, http.BaseAddress!.AbsoluteUri, draft, Ct)); Assert.Equal("personal_receipt_invalid", error.Code);
        Assert.Single(await edits.PendingAsync(http.BaseAddress!.AbsoluteUri, Ct)); Assert.Single(store.ReadPersonalPlays(item.Id));
        swap.Wrong = false; var saved = await edits.SyncOneAsync(api, http.BaseAddress!.AbsoluteUri, draft, Ct); Assert.Equal(item.Id, saved.ItemId);
        Assert.Empty(await edits.PendingAsync(http.BaseAddress!.AbsoluteUri, Ct)); Assert.Single(store.ReadPersonalPlays(item.Id));
    }

    [Fact]
    public async Task V1ProjectsActualItemIdsAcrossSameNovelEditionsAndKeepsWorkbookProvenanceInDatabase()
    {
        using var dir = new TestDirectory(); await using var app = new Factory(dir.Path); using var http = app.CreateClient();
        var store = app.Services.GetRequiredService<SqliteLibraryStore>(); store.Save(StorageTests.Entry()); store.ImportPlays(PlayTests.Batch());
        var original = store.Plays().Results[0]; Assert.NotNull(original.Source);
        var a = store.SaveItem(new(new("同名作品", "v17")), null, "a"); var b = store.SaveItem(new(new("同名作品", "v17")), null, "b");
        var first = store.SavePersonalPlay(new(store.PlayView(original).Details, a.Id, original.Revision), original.Id, "first");
        var second = store.SavePersonalPlay(new(Details("v17"), b.Id), null, "second");
        var replay = store.SavePersonalPlay(new(Details("v17", "completed", 90), a.Id), null, "replay");
        var api = new WebLibraryClient(http, http.BaseAddress!, "projection-test-key", TimeSpan.FromSeconds(10));
        var all = await api.PlaysAsync(ct: Ct);
        Assert.Equal(a.Id, Assert.Single(all, p => p.Id == first.Id).ItemId); Assert.Equal(b.Id, Assert.Single(all, p => p.Id == second.Id).ItemId);
        var exact = await api.PlaysAsync(a.Id, Ct); Assert.Equal(2, exact.Count); Assert.All(exact, p => Assert.Equal(a.Id, p.ItemId));
        Assert.Equal(a.Id, (await api.PersonalPlayAsync(replay.Id, Ct)).ItemId);
        var json = await http.GetStringAsync("/api/v1/plays", Ct); using var body = JsonDocument.Parse(json);
        Assert.All(body.RootElement.EnumerateArray(), p => { Assert.False(p.TryGetProperty("source", out _)); Assert.False(p.TryGetProperty("raw_json", out _)); });
        Assert.Equal(all.Count, JsonSerializer.Deserialize<OldPlay[]>(json, VndbJson.Options)!.Length);
        var old = JsonSerializer.SerializeToNode(all[0], VndbJson.Options)!; old.AsObject().Remove("item_id");
        Assert.Null(old.Deserialize<PersonalPlayView>(VndbJson.Options)!.ItemId);
        var saved = await api.SavePersonalPlayAsync(new(Details("v17", "playing"), a.Id), null, "new-v1", Ct); Assert.Equal(a.Id, saved.ItemId);
        Assert.Equal(JsonSerializer.Serialize(original.Source, VndbJson.Options), JsonSerializer.Serialize(store.GetPlay(first.Id)!.Source, VndbJson.Options));
        Assert.Equal(original.Source!.RawJson, store.GetPlay(first.Id)!.Source!.RawJson);
        Assert.Empty(store.Installations()); Assert.Empty(store.Components());
    }
    [Fact]
    public async Task LinkChangesAndSoftRemovalNeverMovePlaysToAnotherVisibleEdition()
    {
        using var dir = new TestDirectory(); await using var app = new Factory(dir.Path); using var http = app.CreateClient();
        var store = app.Services.GetRequiredService<SqliteLibraryStore>(); store.Save(StorageTests.Entry()); store.Save(StorageTests.Entry("v18"));
        var a = store.SaveItem(new(new("同名作品", "v17")), null, "a"); var b = store.SaveItem(new(new("同名作品", "v17")), null, "b");
        var play = store.SavePersonalPlay(new(Details("v17"), a.Id), null, "play");
        a = store.LinkItem(a.Id, new("v18", a.Revision), "change-link");
        var projected = store.ReadPersonalPlay(play.Id)!; Assert.Equal(a.Id, projected.ItemId); Assert.Equal("v18", projected.Details.NovelId);
        a = store.SetItemRemoved(a.Id, new(a.Revision), true, "remove-item");
        var active = store.ReadItems().Items; Assert.DoesNotContain(active, i => i.Item.Id == a.Id);
        var snapshot = new ShelfSnapshot(http.BaseAddress!.AbsoluteUri, active, store.ReadPersonalPlays(), [], DateTimeOffset.UtcNow, store.ReadItems(deleted: true).Items);
        Assert.Empty(Assert.Single(ShelfPlayResolver.Resolve(snapshot)).Plays);
        a = store.SetItemRemoved(a.Id, new(a.Revision), false, "restore-item");
        play = store.SetPlayRemoved(play.Id, new(store.GetPlay(play.Id)!.Revision), true, "remove-play");
        Assert.Empty(store.ReadPersonalPlays(a.Id)); Assert.Equal(a.Id, Assert.Single(store.ReadPersonalPlays(a.Id, deleted: true)).ItemId);
        store.SetPlayRemoved(play.Id, new(play.Revision), false, "restore-play"); Assert.Single(store.ReadPersonalPlays(a.Id)); Assert.Empty(store.ReadPersonalPlays(b.Id));
    }
    [Fact]
    public void ReadProjectionPreservesUnknownBaseSessionsAdjustmentsAndIndependentReplays()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); var item = store.SaveItem(new(new("Synthetic")), null, "item");
        var a = store.SavePersonalPlay(new(Details(), item.Id), null, "a"); var b = store.SavePersonalPlay(new(Details(minutes: 0), item.Id), null, "b");
        var device = store.SaveDevice(new(Guid.NewGuid().ToString("D"), new("Synthetic", "windows")), "device"); var now = DateTimeOffset.UtcNow;
        store.UpsertSession(new(Guid.NewGuid().ToString("D"), device.Id, a.Id, null, null, now, now.AddMinutes(1), now.AddMinutes(1), 1, 60, "ended"));
        store.AdjustTime(new(Guid.NewGuid().ToString("D"), a.Id, 30, "synthetic correction", store.PlayTime(a.Id).Revision), "adjust");
        var projected = store.ReadPersonalPlays(item.Id); var first = Assert.Single(projected, p => p.Id == a.Id); var replay = Assert.Single(projected, p => p.Id == b.Id);
        Assert.Equal(item.Id, first.ItemId); Assert.Null(first.Time!.TotalSeconds); Assert.Null(first.Time.BaseMinutes); Assert.Equal(60, first.Time.SessionSeconds); Assert.Equal(30, first.Time.AdjustmentSeconds);
        Assert.Equal(0, replay.Time!.TotalSeconds); Assert.Equal(0, replay.Time.SessionSeconds); Assert.Equal(store.PlayTime(a.Id), first.Time);
    }
    [Fact]
    public void BatchedReadCommandsStayBoundedAsPageAndPlayCountsGrow()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); store.Save(StorageTests.Entry());
        for (var n = 0; n < 401; n++)
        {
            var item = store.SaveItem(new(new("Synthetic " + n, "v17", CompanyHint: "テスト会社")), null, "item-" + n);
            store.SavePersonalPlay(new(Details("v17", n % 2 == 0 ? "not_started" : "played"), item.Id), null, "play-" + n);
        }
        var measurements = new List<object>(); var commands = 0L; var thread = Environment.CurrentManagedThreadId;
        using var listener = new MeterListener { InstrumentPublished = (instrument, l) => { if (instrument.Meter.Name == "Vndb.Library.Reads") l.EnableMeasurementEvents(instrument); } };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => { if (instrument.Name == "vndb.library.read.commands" && Environment.CurrentManagedThreadId == thread) commands += value; }); listener.Start();
        foreach (var size in new[] { 10, 24, 100 })
        {
            commands = 0; var watch = Stopwatch.StartNew(); var page = store.ReadItems(page: 2, pageSize: size); watch.Stop();
            Assert.Equal(401, page.Total); Assert.Equal(size, page.Items.Count); Assert.All(page.Items, i => Assert.Equal(1, i.PlayCount)); Assert.Equal(3, commands);
            measurements.Add(new { operation = "items", size, commands, milliseconds = watch.Elapsed.TotalMilliseconds });
        }
        commands = 0; var playsWatch = Stopwatch.StartNew(); var plays = store.ReadPersonalPlays(); playsWatch.Stop(); Assert.Equal(401, plays.Count); Assert.Equal(7, commands);
        measurements.Add(new { operation = "plays", size = plays.Count, commands, milliseconds = playsWatch.Elapsed.TotalMilliseconds });
        Assert.Equal(401, store.ReadItems(query: "テスト会社").Total); Assert.Equal(0, store.ReadItems(unmatched: true).Total);
        Assert.Throws<ArgumentException>(() => store.ReadItems(page: 0));
        var evidence = Path.Combine(TestDirectory.ArtifactRoot, "stable-play", "read-measurement-" + Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(evidence)!); File.WriteAllText(evidence, JsonSerializer.Serialize(measurements, VndbJson.Options));
    }
}
