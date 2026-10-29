using System.Text.Json;
using Vndb.Core;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

public sealed class AutomationTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-21T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
    private static string Novel(string id, string title) => JsonSerializer.Serialize(new { more = false, results = new[] {
        new VisualNovel { Id = id, Title = title, Titles = [new() { Lang = "ja", Title = title, Main = true }],
            Developers = [new() { Id = "p1", Name = "Studio" }], Image = new() { Id = "cv1", Url = "https://t.vndb.org/cv/01/1.jpg" } } } }, VndbJson.Options);
    private const string Producers = """{"more":false,"results":[{"id":"p1","name":"Studio","original":"会社","description":"会社詳細"}]}""";

    [Fact]
    public async Task QueuePacesWorkAndPauseSurvivesReopeningBeforeResumingWithoutChangingPersonalFields()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); var clock = new Clock();
        var first = store.SavePlay(PlayTests.Details("A作品") with { Review = "保留评价", DurationMinutes = 96 });
        var second = store.SavePlay(PlayTests.Details("B作品") with { Score = 9.1m });
        using var api = new StubHandler((call, _) => Task.FromResult(StubHandler.Json(call == 1 ? Novel("v17", "A作品") : call == 2 ? Producers : Novel("v4", "B作品"))));
        using var images = new StubHandler((_, _) => Task.FromResult(ImportTests.Image()));
        using var http = new HttpClient(api); using var imageHttp = new HttpClient(images);
        using var client = new VndbClient(http, VndbClientTests.FastOptions);
        using var importer = new LibraryImporter(client, new(imageHttp, dir.Path), store);
        using var catalog = new CatalogEnricher(client, importer, store, store);
        var jobs = new CatalogJobs(catalog, store, clock);
        var queued = jobs.Queue(new(IntervalSeconds: 5))!;
        Assert.Equal(2, queued.Total); Assert.Equal(queued.Id, jobs.Queue(new())!.Id);
        Assert.True(await jobs.StepAsync(TestContext.Current.CancellationToken));
        Assert.False(await jobs.StepAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, store.GetCatalogJob()!.Cursor); Assert.Equal(2, api.Requests.Count);
        store.PauseCatalogJob(queued.Id, true, clock.GetUtcNow()); clock.Advance(600);
        var reopened = new SqliteLibraryStore(dir.Path); var restarted = new CatalogJobs(catalog, reopened, clock);
        Assert.False(await restarted.StepAsync(TestContext.Current.CancellationToken));
        Assert.Equal("paused", reopened.GetCatalogJob()!.Status);
        reopened.PauseCatalogJob(queued.Id, false, clock.GetUtcNow());
        Assert.True(await restarted.StepAsync(TestContext.Current.CancellationToken));
        Assert.Equal("completed", reopened.GetCatalogJob()!.Status); Assert.Equal(2, reopened.GetCatalogJob()!.Matched);
        Assert.Equal(first.Details with { NovelId = "v17" }, reopened.GetPlay(first.Id)!.Details);
        Assert.Equal(second.Details with { NovelId = "v4" }, reopened.GetPlay(second.Id)!.Details);
        Assert.Equal(3, api.Requests.Count); Assert.Equal(2, store.Statistics().Covers);
        var backup = Path.Combine(dir.Path, "backup", "library.db"); reopened.Backup(backup);
        Assert.Equal("completed", new SqliteLibraryStore(Path.GetDirectoryName(backup)!).GetCatalogJob()!.Status);
    }

    [Fact]
    public async Task FailuresBackOffAndStopRetryingAfterThreeAttempts()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); var clock = new Clock();
        store.SavePlay(PlayTests.Details("未知作品"));
        using var handler = new StubHandler((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("unavailable")));
        using var http = new HttpClient(handler); using var client = new VndbClient(http, VndbClientTests.FastOptions);
        using var importer = new LibraryImporter(client, new(http, dir.Path), store);
        using var catalog = new CatalogEnricher(client, importer, store, store);
        var jobs = new CatalogJobs(catalog, store, clock); jobs.Queue(new());
        await jobs.StepAsync(TestContext.Current.CancellationToken);
        Assert.False(await jobs.StepAsync(TestContext.Current.CancellationToken));
        clock.Advance(60); await jobs.StepAsync(TestContext.Current.CancellationToken);
        clock.Advance(120); await jobs.StepAsync(TestContext.Current.CancellationToken);
        var job = store.GetCatalogJob()!;
        Assert.Equal("completed", job.Status); Assert.Equal(1, job.Cursor); Assert.Equal(1, job.Errors);
        Assert.Equal(3, handler.Requests.Count); Assert.Equal(0, store.Statistics().Novels);
    }

    [Fact]
    public async Task InterruptedInFlightItemRetainsItsCursorForRestart()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); var clock = new Clock();
        store.SavePlay(PlayTests.Details());
        using var http = new HttpClient(ImportTests.Upstream()); using var client = new VndbClient(http, VndbClientTests.FastOptions);
        using var images = new HttpClient(new StubHandler((_, _) => Task.FromResult(ImportTests.Image())));
        using var importer = new LibraryImporter(client, new(images, dir.Path), store);
        using var catalog = new CatalogEnricher(client, importer, store, store);
        var jobs = new CatalogJobs(catalog, store, clock); jobs.Queue(new());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => jobs.StepAsync(cancellation.Token));
        var reopened = new SqliteLibraryStore(dir.Path);
        Assert.Equal(0, reopened.GetCatalogJob()!.Cursor); Assert.Equal("running", reopened.GetCatalogJob()!.Status);
        await new CatalogJobs(catalog, reopened, clock).StepAsync(TestContext.Current.CancellationToken);
        Assert.Equal("completed", reopened.GetCatalogJob()!.Status); Assert.Equal(1, reopened.PlayStatistics().Total);
    }

    [Fact]
    public async Task OnlineMissPersistsDetailsCompaniesCoverAndQueryCacheThenWorksOffline()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); var clock = new Clock();
        var personal = store.SavePlay(PlayTests.Details("检索词") with { Review = "私人评价不发送上游" });
        using var handler = new StubHandler((call, _) => Task.FromResult(StubHandler.Json(call <= 2 ? Novel("v17", "作品甲") : Producers)));
        using var http = new HttpClient(handler); using var imageHttp = new HttpClient(new StubHandler((_, _) => Task.FromResult(ImportTests.Image())));
        using var client = new VndbClient(http, VndbClientTests.FastOptions);
        using var importer = new LibraryImporter(client, new(imageHttp, dir.Path), store);
        using var catalog = new CatalogEnricher(client, importer, store, store);
        using var search = new CatalogQueries(store, catalog, store, client, importer, clock);
        var request = new LibrarySearchRequest("检索词"); Assert.True(search.Local(request).NeedsRemote);
        var online = await search.SearchAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal("vndb", online.Source); Assert.Equal("ready", Assert.Single(online.Results).CoverStatus);
        Assert.Equal("会社詳細", online.Results[0].Producers[0].Description);
        var reopened = new SqliteLibraryStore(dir.Path);
        using var offline = new CatalogQueries(reopened, catalog, reopened, client, importer, clock);
        Assert.Equal("local-query-cache", offline.Local(request).Source);
        Assert.Single((await offline.SearchAsync(request, TestContext.Current.CancellationToken)).Results);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(personal.Details, store.GetPlay(personal.Id)!.Details); // Saving a search candidate does not prove a personal association.
        Assert.All(handler.Requests, r => { Assert.Null(r.Authorization); Assert.DoesNotContain("私人评价", r.Body ?? ""); });
    }

    [Fact]
    public void LocalSearchUsesNormalizedTitlesAndKnownDirectoryAssociationsWithoutNetwork()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        var entry = StorageTests.Entry() with { Novel = StorageTests.Entry().Novel with { Title = "Sugar＊Style", Titles = [new() { Lang = "ja", Title = "Sugar＊Style", Main = true }] } };
        store.Save(entry);
        store.ImportLocalGames([new("PC", @"E:\会社\FMS", "FMS", "会社", DateTimeOffset.UtcNow, "inventory.json", new string('a', 64))]);
        var location = Assert.Single(store.LocalGames()); store.LinkLocalGame(location.Id, "v17", location.Revision);
        using var handler = new StubHandler("{}"); using var http = new HttpClient(handler);
        using var client = new VndbClient(http, VndbClientTests.FastOptions);
        using var importer = new LibraryImporter(client, new(http, dir.Path), store);
        using var catalog = new CatalogEnricher(client, importer, store, store);
        using var search = new CatalogQueries(store, catalog, store, client, importer, new Clock());
        Assert.Equal("v17", Assert.Single(search.Local(new("SugarStyle")).Results).Novel.Id);
        Assert.Equal("v17", Assert.Single(search.Local(new("FMS")).Results).Novel.Id);
        Assert.Equal("v17", Assert.Single(search.Local(new("V17")).Results).Novel.Id);
        Assert.Empty(handler.Requests); Assert.Throws<ArgumentException>(() => AutomationRules.Interval(2));
    }

    [Fact]
    public void MetadataInPartialCacheDoesNotSilentlyLinkNewSpreadsheetImports()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); store.Save(StorageTests.Entry());
        Assert.Equal(0, store.ImportPlays(PlayTests.Batch()).Linked);
        Assert.Equal(2, store.Plays(unmatched: true).Total);
    }
}
