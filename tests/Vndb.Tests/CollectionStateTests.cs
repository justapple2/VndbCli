using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.ViewModels;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

public sealed class CollectionStateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static PersonalPlayData Details(string status) => new("Synthetic", "v17", status, null, null, null, null, null, null, null, null);
    private static LibraryItem Add(SqliteLibraryStore store, string name) => store.SaveItem(new(new(name, "v17")), null, "item-" + name);

    [Fact]
    public void PublicMetadataAndDefaultImportNeverBecomePersonalUnplayedItems()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        store.Save(StorageTests.Entry());
        var snapshot = MetadataImports.Snapshot(StorageTests.Entry("v18"));
        var parsed = MetadataPayload.Parse(JsonSerializer.SerializeToElement(new MetadataBatch([snapshot], DateTimeOffset.UtcNow, "public-fixture"), VndbJson.Options), new());
        var batch = store.PreviewMetadata(parsed.Items, parsed.ClientId, parsed.RawSha, false);
        store.CommitMetadata(batch.Id, "public-import"); while (store.ProcessMetadataItem()) { }
        var counts = store.CollectionStatistics();
        Assert.Equal(2, counts.PublicNovels); Assert.Equal(0, counts.PersonalItems); Assert.Equal(0, counts.Unplayed);
        Assert.Empty(store.ReadItems().Items); Assert.Equal(0, store.Plays().Total);
        var item = Add(store, "explicit-personal");
        Assert.Equal("unrecorded", store.ReadItem(item.Id)!.State!.Progress.State);
        Assert.Equal(0, store.Plays().Total);
    }

    [Fact]
    public void SameNovelEditionsReplaysAndPresenceUseExactItemIds()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); store.Save(StorageTests.Entry());
        var a = Add(store, "a"); var b = Add(store, "b"); var c = Add(store, "c");
        store.SavePersonalPlay(new(Details("not_started"), a.Id), null, "a-unplayed");
        store.SavePersonalPlay(new(Details("completed"), b.Id), null, "b-completed");
        store.SavePersonalPlay(new(Details("not_started"), b.Id), null, "b-replay");
        var instance = Guid.NewGuid().ToString("D");
        foreach (var key in new[] { "first-device", "second-device" })
            store.SavePresence(new(Guid.NewGuid().ToString("D"), instance, Guid.NewGuid().ToString("D"), 0, [new(b.Id, true)]), instance, key);
        var rows = store.ReadItems().Items.ToDictionary(i => i.Item.Id);
        Assert.Equal("unplayed", rows[a.Id].State!.Progress.State); Assert.False(rows[a.Id].State!.IsLocal);
        Assert.Equal("played", rows[b.Id].State!.Progress.State); Assert.Equal(2, rows[b.Id].PlayCount); Assert.True(rows[b.Id].State!.IsLocal);
        Assert.Equal("unrecorded", rows[c.Id].State!.Progress.State); Assert.False(rows[c.Id].State!.IsLocal);
        var summary = store.CollectionStatistics();
        Assert.Equal(3, summary.PersonalItems); Assert.Equal(1, summary.Played); Assert.Equal(1, summary.Unplayed);
        Assert.Equal(1, summary.Unrecorded); Assert.Equal(1, summary.LocalItems);
        Assert.Single(store.ReadItems(progress: "unplayed", pageSize: 1).Items);
        var played = store.ReadItems(progress: "played", pageSize: 1); Assert.Equal(1, played.Total); Assert.Equal(b.Id, played.Items[0].Item.Id);
        Assert.Equal(2, store.ReadItems(status: "not_started").Total); // replay status does not erase prior completion
        Assert.Equal(b.Id, Assert.Single(store.ReadItems(isLocal: true).Items).Item.Id);
    }

    [Fact]
    public void FilteringPrecedesPagingAndRemovalDoesNotLeaveFalsePlayedFlags()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); store.Save(StorageTests.Entry());
        var first = Add(store, "older-played"); var second = Add(store, "newer-unrecorded");
        var play = store.SavePersonalPlay(new(Details("played"), first.Id), null, "played");
        var page = store.ReadItems(progress: "unrecorded", pageSize: 1);
        Assert.Equal(1, page.Total); Assert.False(page.More); Assert.Equal(second.Id, page.Items[0].Item.Id);
        var removed = store.SetPlayRemoved(play.Id, new(play.Revision), true, "remove-play");
        Assert.Equal(2, store.ReadItems(progress: "unrecorded").Total); Assert.Equal(0, store.CollectionStatistics().Played);
        store.SetPlayRemoved(play.Id, new(removed.Revision), false, "restore-play");
        Assert.Equal(1, store.CollectionStatistics().Played);
        store.SetItemRemoved(first.Id, new(first.Revision), true, "remove-item");
        Assert.Equal(1, store.CollectionStatistics().PersonalItems); Assert.Equal(0, store.CollectionStatistics().Played);
        Assert.Throws<ArgumentException>(() => store.ReadItems(progress: "not-a-state"));
    }

    [Fact]
    public void InconsistentHistoricalAssociationRequiresReviewRatherThanBorrowingProgress()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); store.Save(StorageTests.Entry()); store.Save(StorageTests.Entry("v18"));
        var item = Add(store, "a"); var play = store.SavePersonalPlay(new(Details("completed"), item.Id), null, "play");
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(dir.Path, "library.db") + ";Pooling=False"))
        {
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE play_records SET novel_id='v18' WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", play.Id); cmd.ExecuteNonQuery();
        }
        Assert.Equal("review", store.ReadItem(item.Id)!.State!.Progress.State);
        Assert.Equal(1, store.CollectionStatistics().NeedsReview); Assert.Equal(0, store.CollectionStatistics().Played);
    }

    [Fact]
    public void DesktopFiltersSeparateUnrecordedFromUnplayedAndRetainReplayHistory()
    {
        var now = DateTimeOffset.UtcNow;
        LibraryItemView Item(string id) => new(new(id, new("Synthetic"), 1, now, now), null, 0);
        PersonalPlayView Play(string id, string status) => new(id, Details(status), 1, now, now);
        var unknown = new BookViewModel(Item("unknown"), null, []);
        var unplayed = new BookViewModel(Item("unplayed"), null, [Play("p1", "not_started")]);
        var replay = new BookViewModel(Item("replay"), null, [Play("p2", "completed"), Play("p3", "not_started")]);
        Assert.True(unknown.IsUnrecorded); Assert.False(unknown.IsUnplayed); Assert.Equal("未记录", unknown.ShelfState);
        Assert.True(unplayed.IsUnplayed); Assert.False(replay.IsUnplayed); Assert.True(replay.HasPlayed);
        var browser = new ShelfBrowserViewModel(); browser.SetLibrary([unknown, unplayed, replay]);
        browser.ShelfState = "未记录"; Assert.Equal("unknown", Assert.Single(browser.Books).Id);
        browser.ShelfState = "未游玩"; Assert.Equal("unplayed", Assert.Single(browser.Books).Id);
        browser.ShelfState = "曾玩过"; Assert.Equal("replay", Assert.Single(browser.Books).Id);
        Assert.DoesNotContain("\"state\"", JsonSerializer.Serialize(Item("legacy"), VndbJson.Options));
    }

    private sealed class Factory(string path) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b) => b.UseSetting("Library:DataDirectory", path)
            .UseSetting("Library:WriteKey", "test-only-library-key").UseSetting("CatalogJobs:Enabled", "false");
    }
    [Fact]
    public async Task PublicReadApiExposesStatusAndCountsWithoutLocalStructure()
    {
        using var dir = new TestDirectory(); await using var app = new Factory(dir.Path); using var http = app.CreateClient();
        var store = app.Services.GetRequiredService<SqliteLibraryStore>(); store.Save(StorageTests.Entry());
        var item = Add(store, "unrecorded");
        var api = new WebLibraryClient(http, http.BaseAddress!, null, TimeSpan.FromSeconds(10));
        var rows = await api.FilterItemsAsync(progress: "unrecorded", ct: Ct);
        Assert.Equal(item.Id, Assert.Single(rows.Items).Item.Id);
        Assert.Equal(1, (await api.CollectionStatisticsAsync(Ct)).Unrecorded);
        using var bad = await http.GetAsync("/api/v1/items?progress=invalid", Ct); Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var serialized = JsonSerializer.Serialize(rows, VndbJson.Options);
        Assert.DoesNotContain("directory", serialized); Assert.DoesNotContain("executable", serialized);
        Assert.Equal(0, store.Plays().Total);
    }
}
