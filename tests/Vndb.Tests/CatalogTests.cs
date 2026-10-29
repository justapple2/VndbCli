using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vndb.Core;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

public sealed class CatalogTests
{
    private static VisualNovel Novel(string id, string title, params string[] aliases) => new()
    {
        Id = id, Title = title, Titles = [new() { Lang = "ja", Title = title, Main = true }], Aliases = aliases,
        Developers = [new() { Id = "p1", Name = "Studio", Original = "会社" }],
        Image = new() { Id = "cv1", Url = "https://t.vndb.org/cv/01/1.jpg" }
    };
    private static string Page(bool more, params VisualNovel[] novels) => JsonSerializer.Serialize(new { results = novels, more }, VndbJson.Options);
    private static LocalGameInput Local(string title, string path = @"E:\会社\ゲーム") =>
        new("PC", path, title, "会社", DateTimeOffset.Parse("2026-10-21T00:00:00Z"), "known-directories.json", new string('a', 64));
    private const string Producers = """{"results":[{"id":"p1","name":"Studio","original":"会社","description":"会社の説明","type":"co"}],"more":false}""";

    [Fact]
    public async Task ExactBatchImportsFullMetadataAndCoverAndLinksReplaysAndLocalWithoutChangingPersonalData()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        var first = store.SavePlay(PlayTests.Details("作品甲") with { Score = 8.7m, DurationMinutes = 91, Review = "个人感想" });
        var replay = store.SavePlay(PlayTests.Details("作品甲") with { Notes = "二周目" });
        store.SavePlay(PlayTests.Details("未玩作品") with { Status = "not_started", StartedOn = null, FinishedOn = null });
        var local = store.ImportLocalGames([Local("作品乙")]); Assert.Equal(1, local.Inserted);
        using var api = new StubHandler((call, _) => Task.FromResult(StubHandler.Json(call <= 2 ? Page(false, Novel("v17", "作品甲"), Novel("v4", "作品乙")) : Producers)));
        using var images = new StubHandler((_, _) => Task.FromResult(ImportTests.Image()));
        using var apiHttp = new HttpClient(api); using var imageHttp = new HttpClient(images);
        using var client = new VndbClient(apiHttp, VndbClientTests.FastOptions);
        using var importer = new LibraryImporter(client, new(imageHttp, dir.Path), store);
        using var catalog = new CatalogEnricher(client, importer, store, store);
        var result = await catalog.CompleteAsync(new(), TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Processed); Assert.Equal(2, result.Imported);
        Assert.Equal(2, result.LinkedPlays); Assert.Equal(1, result.LinkedLocal); Assert.Equal(0, result.Pending);
        Assert.Equal(first.Details with { NovelId = "v17" }, store.GetPlay(first.Id)!.Details);
        Assert.Equal(replay.Details with { NovelId = "v17" }, store.GetPlay(replay.Id)!.Details);
        Assert.Equal("v4", Assert.Single(store.LocalGames()).NovelId);
        Assert.All(store.List().Results, entry => { Assert.Equal("ready", entry.CoverStatus); Assert.Equal("会社の説明", entry.Producers[0].Description); });
        Assert.Equal(3, api.Requests.Count); // One search per title and one deduplicated producer batch.
        Assert.Equal(0, (await catalog.CompleteAsync(new(), TestContext.Current.CancellationToken)).Processed);
        Assert.Equal(2, store.Statistics().Novels);
        Assert.Equal(2, catalog.Page(scope: "played").Results.Sum(r => r.Source.PlayIds.Count));
        var later = store.SavePlay(PlayTests.Details("作品甲") with { Review = "后来的重玩" });
        var reused = await catalog.CompleteAsync(new(), TestContext.Current.CancellationToken);
        Assert.Equal(1, reused.LinkedPlays); Assert.Equal(0, reused.Imported); Assert.Equal(3, api.Requests.Count);
        Assert.Equal(later.Details with { NovelId = "v17" }, store.GetPlay(later.Id)!.Details);
    }

    [Fact]
    public async Task AmbiguousAliasesRemainUnlinkedUntilExplicitConfirmation()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        var play = store.SavePlay(PlayTests.Details("共通別名"));
        var a = Novel("v17", "作品甲", "共通別名"); var b = Novel("v4", "作品乙", "共通別名");
        using var api = new StubHandler((call, _) => Task.FromResult(StubHandler.Json(call == 1 ? Page(false, a, b) : call == 2 ? Page(false, a) : Producers)));
        using var images = new StubHandler((_, _) => Task.FromResult(ImportTests.Image()));
        using var apiHttp = new HttpClient(api); using var imageHttp = new HttpClient(images);
        using var client = new VndbClient(apiHttp, VndbClientTests.FastOptions);
        using var importer = new LibraryImporter(client, new(imageHttp, dir.Path), store);
        using var catalog = new CatalogEnricher(client, importer, store, store);
        var result = await catalog.CompleteAsync(new(), TestContext.Current.CancellationToken);
        Assert.Equal("needs_review", Assert.Single(result.Results).Status);
        Assert.Equal(2, Assert.Single(store.CatalogLookups()).Candidates.Count);
        Assert.Null(store.GetPlay(play.Id)!.Details.NovelId); Assert.Equal(0, store.Statistics().Novels);
        var resolved = await catalog.ResolveAsync(new(CatalogRules.Key("共通別名"), "v17"), TestContext.Current.CancellationToken);
        Assert.Equal("manual-confirmation", resolved.Basis);
        Assert.Equal(play.Details with { NovelId = "v17" }, store.GetPlay(play.Id)!.Details);
        Assert.Equal("ready", store.Get("v17")!.CoverStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APartialNasCacheOrASoleFuzzyCandidateDoesNotProveIdentity(bool cache)
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        var play = store.SavePlay(PlayTests.Details("略称"));
        var a = Novel("v17", "作品甲", cache ? "略称" : "別名"); var b = Novel("v4", "作品乙", "略称");
        if (cache) store.Save(new(a, [new() { Id = "p1", Name = "Studio" }], null, "none", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        using var api = new StubHandler(Page(false, cache ? [a, b] : [a]));
        using var http = new HttpClient(api); using var client = new VndbClient(http, VndbClientTests.FastOptions);
        using var importer = new LibraryImporter(client, new(http, dir.Path), store);
        using var catalog = new CatalogEnricher(client, importer, store, store);
        var result = await catalog.CompleteAsync(new(), TestContext.Current.CancellationToken);
        Assert.Equal("needs_review", Assert.Single(result.Results).Status);
        Assert.Null(store.GetPlay(play.Id)!.Details.NovelId);
    }

    [Fact]
    public async Task PaginationCannotHideAnAmbiguousExactMatchAndTruncationNeverAutoLinks()
    {
        foreach (var truncated in new[] { false, true })
        {
            using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
            var play = store.SavePlay(PlayTests.Details("同名作品"));
            using var api = new StubHandler((call, _) => Task.FromResult(StubHandler.Json(Page(truncated || call == 1,
                Novel(call == 1 || truncated ? "v17" : "v4", "同名作品")))));
            using var http = new HttpClient(api); using var client = new VndbClient(http, VndbClientTests.FastOptions);
            using var importer = new LibraryImporter(client, new(http, dir.Path), store);
            using var catalog = new CatalogEnricher(client, importer, store, store);
            var result = await catalog.CompleteAsync(new(), TestContext.Current.CancellationToken);
            Assert.Equal("needs_review", Assert.Single(result.Results).Status);
            Assert.Equal(truncated, result.Results[0].Truncated);
            Assert.Null(store.GetPlay(play.Id)!.Details.NovelId);
        }
    }

    [Fact]
    public async Task EditionPlusKeepsBaseGameAndFandiscSourcesSeparate()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        var first = store.SavePlay(PlayTests.Details("アマカノ２"));
        var plus = store.SavePlay(PlayTests.Details("アマカノ2 +"));
        using var api = new StubHandler((call, _) => Task.FromResult(StubHandler.Json(call <= 2 ?
            Page(false, Novel("v17", "アマカノ２"), Novel("v4", "アマカノ２＋")) : Producers)));
        using var images = new StubHandler((_, _) => Task.FromResult(ImportTests.Image()));
        using var http = new HttpClient(api); using var imageHttp = new HttpClient(images);
        using var client = new VndbClient(http, VndbClientTests.FastOptions);
        using var importer = new LibraryImporter(client, new(imageHttp, dir.Path), store);
        using var catalog = new CatalogEnricher(client, importer, store, store);
        Assert.Equal(2, catalog.Statistics().Titles);
        await catalog.CompleteAsync(new(), TestContext.Current.CancellationToken);
        Assert.Equal(first.Details with { NovelId = "v17" }, store.GetPlay(first.Id)!.Details);
        Assert.Equal(plus.Details with { NovelId = "v4" }, store.GetPlay(plus.Id)!.Details);
        var titles = PlayRules.TitleMap(store);
        Assert.Equal("v17", titles[PlayRules.NormalizeTitle(first.Details.Title)]);
        Assert.Equal("v4", titles[PlayRules.NormalizeTitle(plus.Details.Title)]);
    }

    [Fact]
    public async Task LegacyPlusLookupCannotBeReusedForTheBaseGame()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        store.Save(StorageTests.Entry()); var play = store.SavePlay(PlayTests.Details("アマカノ2"));
        var old = new CatalogLookup(CatalogRules.Key("アマカノ2"), "アマカノ2 +", "アマカノ2 +", "matched", "v17",
            "manual-confirmation", [], false, DateTimeOffset.UtcNow, null);
        using (var db = new SqliteConnection("Data Source=" + System.IO.Path.Combine(dir.Path, "library.db") + ";Pooling=False"))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO catalog_lookups VALUES($key,$title,'matched','v17',$json,$at)";
            cmd.Parameters.AddWithValue("$key", old.Key); cmd.Parameters.AddWithValue("$title", old.Title);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(old, VndbJson.Options));
            cmd.Parameters.AddWithValue("$at", old.CheckedAt.ToString("O")); cmd.ExecuteNonQuery();
        }
        using var api = new StubHandler(Page(false)); using var http = new HttpClient(api);
        using var client = new VndbClient(http, VndbClientTests.FastOptions);
        using var importer = new LibraryImporter(client, new(http, dir.Path), store);
        using var catalog = new CatalogEnricher(client, importer, store, store);
        await catalog.CompleteAsync(new(), TestContext.Current.CancellationToken);
        Assert.Null(store.GetPlay(play.Id)!.Details.NovelId); Assert.Single(api.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactCandidatesRemainVisibleWhenManyFuzzyResultsComeFirst(bool changedQuery)
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        var play = store.SavePlay(PlayTests.Details(changedQuery ? "原始目录名" : "SPIRAL!!"));
        var novels = Enumerable.Range(1, 25).Select(i => Novel("v" + i, "Spiral Side Story " + i))
            .Concat([Novel("v100", "SPIRAL!!"), Novel("v101", "Spiral")]).ToArray();
        using var api = new StubHandler(Page(false, novels)); using var http = new HttpClient(api);
        using var client = new VndbClient(http, VndbClientTests.FastOptions);
        using var importer = new LibraryImporter(client, new(http, dir.Path), store);
        using var catalog = new CatalogEnricher(client, importer, store, store);
        var lookup = await catalog.SearchAsync(new(CatalogRules.Key(play.Details.Title), "SPIRAL!!"), TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "v100", "v101" }, lookup.Candidates.Take(2).Select(c => c.Id));
        Assert.Equal(20, lookup.Candidates.Count); Assert.True(lookup.Truncated);
        Assert.Equal("needs_review", lookup.Status); Assert.Null(store.GetPlay(play.Id)!.Details.NovelId);
    }

    [Fact]
    public async Task NotFoundIsPersistedAndSkippedOnRepeatedBatch()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        store.SavePlay(PlayTests.Details("不存在作品"));
        using var api = new StubHandler(Page(false)); using var http = new HttpClient(api);
        using var client = new VndbClient(http, VndbClientTests.FastOptions);
        using var importer = new LibraryImporter(client, new(http, dir.Path), store);
        using var catalog = new CatalogEnricher(client, importer, store, store);
        Assert.Equal("not_found", Assert.Single((await catalog.CompleteAsync(new(), TestContext.Current.CancellationToken)).Results).Status);
        Assert.Equal(0, (await catalog.CompleteAsync(new(), TestContext.Current.CancellationToken)).Processed);
        Assert.Single(api.Requests);
        Assert.Equal("not_found", Assert.Single(new SqliteLibraryStore(dir.Path).CatalogLookups()).Status);
    }

    [Fact]
    public async Task ConflictingKnownIdentityIsPreservedAndNotSpreadToOtherRecords()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        store.Save(StorageTests.Entry());
        var existing = store.SavePlay(PlayTests.Details("同名") with { NovelId = "v17" });
        var pending = store.SavePlay(PlayTests.Details("同名"));
        using var api = new StubHandler(Page(false, Novel("v4", "同名"))); using var http = new HttpClient(api);
        using var client = new VndbClient(http, VndbClientTests.FastOptions);
        using var importer = new LibraryImporter(client, new(http, dir.Path), store);
        using var catalog = new CatalogEnricher(client, importer, store, store);
        Assert.Equal("needs_review", Assert.Single((await catalog.CompleteAsync(new(), TestContext.Current.CancellationToken)).Results).Status);
        Assert.Equal(existing.Details, store.GetPlay(existing.Id)!.Details);
        Assert.Null(store.GetPlay(pending.Id)!.Details.NovelId);
        await Assert.ThrowsAsync<PlayConflictException>(() => catalog.ResolveAsync(new(CatalogRules.Key("同名"), "v4"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void LocalImportIsIdempotentValidatedAndMigrationAndBackupKeepAllSources()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        store.Save(StorageTests.Entry()); store.SavePlay(PlayTests.Details()); store.RegisterArchive(ArchiveTests.Registration("archived"));
        using (var db = new SqliteConnection("Data Source=" + System.IO.Path.Combine(dir.Path, "library.db") + ";Pooling=False"))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "DROP TABLE catalog_events; DROP TABLE catalog_lookups; DROP TABLE local_games; PRAGMA user_version=3;"; cmd.ExecuteNonQuery();
        }
        store = new SqliteLibraryStore(dir.Path);
        Assert.Throws<ArgumentException>(() => store.ImportLocalGames([Local("甲"), Local("乙", "relative/game")]));
        Assert.Empty(store.LocalGames());
        Assert.Equal(1, store.ImportLocalGames([Local("甲")]).Inserted);
        var record = Assert.Single(store.LocalGames());
        var linked = store.LinkLocalGame(record.Id, "v17", record.Revision);
        Assert.Equal(0, store.ImportLocalGames([Local("甲")]).Inserted);
        Assert.Equal(linked, Assert.Single(store.LocalGames()));
        Assert.Throws<PlayConflictException>(() => store.LinkLocalGame(record.Id, "v17", record.Revision));
        var backup = System.IO.Path.Combine(dir.Path, "backup", "library.db"); store.Backup(backup);
        var restored = new SqliteLibraryStore(System.IO.Path.GetDirectoryName(backup)!);
        Assert.Single(restored.LocalGames()); Assert.Single(restored.Archives()); Assert.Equal(1, restored.PlayStatistics().Total);
    }
}
