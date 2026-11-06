using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Data.Sqlite;
using Vndb.Client;
using Vndb.Cli;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Library;
using Vndb.Local;
using Vndb.Desktop.ViewModels;
using Xunit;

namespace Vndb.Tests;

public sealed class ReleaseCatalogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static NovelRelease Release(string id = "r9") => new()
    {
        Id = id, Title = "Synthetic edition", AltTitle = "合成版", Released = "2099-12", Patch = true,
        Languages = [new("ja", null, null, false, true), new("zh-Hans", "合成译名", null, true, false)],
        Platforms = ["win"], Vns = [new("v17", "partial"), new("v18", "trial")],
        Producers = [new("p1", "Synthetic", null, true, false)]
    };
    private static string Page(params NovelRelease[] items) => JsonSerializer.Serialize(new { results = items, more = false }, VndbJson.Options);

    [Fact]
    public async Task CursorPaginationRetainsLanguageTranslationPlatformBundleAndPartialDates()
    {
        using var handler = new StubHandler((i, _) => Task.FromResult(StubHandler.Json(i == 1
            ? JsonSerializer.Serialize(new { results = new[] { Release() }, more = true }, VndbJson.Options) : Page(Release("r10")))));
        using var http = new HttpClient(handler); using var core = new VndbClient(http, VndbClientTests.FastOptions);
        var result = await core.GetNovelReleasesAsync("v17", Ct);
        Assert.Equal(2, result.Items.Count); Assert.True(result.Items[0].Languages[1].Mtl);
        Assert.Equal("2099-12", result.Items[0].Released); Assert.Equal(2, result.Items[0].Vns.Count);
        Assert.Contains("机翻", ReleaseRules.Describe(result.Items[0], "v17"));
        Assert.Contains("部分", ReleaseRules.Describe(result.Items[0], "v17"));
        Assert.Contains("试玩", ReleaseRules.Describe(result.Items[0], "v18"));
        Assert.Contains("r9", handler.Requests[1].Body); Assert.Contains("\"page\":1", handler.Requests[1].Body!.Replace(" ", ""));
        Assert.All(handler.Requests, r => Assert.Null(r.Authorization));
    }

    [Fact]
    public async Task DuplicateOrWrongNovelAndEmptyMoreCannotProduceCompletedCatalog()
    {
        foreach (var json in new[] { Page(Release(), Release()), Page(Release() with { Vns = [new("v99", "complete")] }), "{\"results\":[],\"more\":true}" })
        {
            using var handler = new StubHandler(json); using var http = new HttpClient(handler);
            using var core = new VndbClient(http, VndbClientTests.FastOptions);
            await Assert.ThrowsAsync<JsonException>(() => core.GetNovelReleasesAsync("v17", Ct));
        }
    }

    [Fact]
    public void LegacySerializationUnchangedAndPortableImportValidatesReleaseAssociations()
    {
        var entry = StorageTests.Entry();
        Assert.DoesNotContain("release_catalog", JsonSerializer.Serialize(entry.Novel, VndbJson.Options));
        var catalog = new ReleaseCatalog([Release()], DateTimeOffset.UtcNow);
        var snapshot = MetadataImports.Snapshot(entry with { Novel = entry.Novel with { ReleaseCatalog = catalog } });
        var parsed = MetadataPayload.Parse(JsonSerializer.SerializeToElement(new MetadataBatch([snapshot], DateTimeOffset.UtcNow, "release-fixture"), VndbJson.Options), new());
        Assert.Equal("snapshot", parsed.Items[0].InputKind);
        Assert.Equal("r9", parsed.Items[0].Snapshot!.Novel!.ReleaseCatalog!.Items[0].Id);
        var bad = snapshot with { Novel = snapshot.Novel! with { ReleaseCatalog = catalog with { Items = [Release() with { Vns = [] }] } } };
        var rejected = MetadataPayload.Parse(JsonSerializer.SerializeToElement(new MetadataBatch([bad], DateTimeOffset.UtcNow, "bad-release"), VndbJson.Options), new());
        Assert.Equal("invalid", rejected.Items[0].InputKind);
    }

    [Fact]
    public void Schema10UpgradeKeepsPersonalRowsAndOldRefreshCannotClearNewReleaseSnapshot()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); store.Save(StorageTests.Entry());
        store.ImportPlays(PlayTests.Batch()); var before = JsonSerializer.Serialize(store.Plays(), VndbJson.Options);
        var shelfBefore = JsonSerializer.Serialize(store.ReadItems(), VndbJson.Options);
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(dir.Path, "library.db") + ";Pooling=False"))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DROP TABLE novel_releases; DROP TABLE releases; PRAGMA user_version=10"; cmd.ExecuteNonQuery(); }
        store = new SqliteLibraryStore(dir.Path);
        var now = DateTimeOffset.UtcNow; var original = store.Get("v17")!;
        store.Save(original with { Novel = original.Novel with { ReleaseCatalog = new([Release()], now) } });
        store.Save(original); // old JSON/ordinary refresh has no release fields
        store.Save(original with { Novel = original.Novel with { ReleaseCatalog = new([], now.AddDays(-1)) } });
        Assert.Single(store.Get("v17")!.Novel.ReleaseCatalog!.Items);
        Assert.Equal(before, JsonSerializer.Serialize(store.Plays(), VndbJson.Options));
        Assert.Equal(shelfBefore, JsonSerializer.Serialize(store.ReadItems(), VndbJson.Options));
        store.Save(original with { Novel = original.Novel with { ReleaseCatalog = new([], now.AddDays(1)) } });
        Assert.Empty(store.Get("v17")!.Novel.ReleaseCatalog!.Items); // proven empty differs from unknown
    }

    private sealed class Factory(string path, VndbClient core) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
            .UseSetting("Library:DataDirectory", path).UseSetting("Library:WriteKey", "test-only-library-key")
            .UseSetting("CatalogJobs:Enabled", "false").ConfigureServices(s => { s.RemoveAll<VndbClient>(); s.AddSingleton(core); });
    }
    [Fact]
    public async Task WebQueryPersistsAndClientOfflineJsonSurvivesServerUnavailability()
    {
        using var dir = new TestDirectory();
        var novel = StorageTests.Entry().Novel with { Developers = [] };
        using var handler = new StubHandler((i, _) => Task.FromResult(StubHandler.Json(i == 1
            ? JsonSerializer.Serialize(new { results = new[] { novel }, more = false }, VndbJson.Options) : Page(Release()))));
        using var upstreamHttp = new HttpClient(handler); using var core = new VndbClient(upstreamHttp, VndbClientTests.FastOptions);
        await using var factory = new Factory(Path.Combine(dir.Path, "db"), core); using var http = factory.CreateClient();
        var client = new WebLibraryClient(http, http.BaseAddress!, "test-only-library-key", TimeSpan.FromSeconds(20));
        var query = new ClientQuery("vn_releases", new(), "v17");
        var result = await client.QueryAsync(query, Ct);
        Assert.Single(result.Items[0].Novel!.ReleaseCatalog!.Items);
        var again = await client.QueryAsync(query, Ct);
        Assert.Equal("library", again.Source); Assert.Equal(2, handler.Requests.Count);
        var store = factory.Services.GetRequiredService<SqliteLibraryStore>(); Assert.Empty(store.ReadItems().Items);
        Assert.Equal(0, store.Plays().Total);
        var snapshots = new JsonSnapshotStore(Path.Combine(dir.Path, "json"));
        await snapshots.SaveAsync(query, result, Ct);
        var cached = await snapshots.ReadAsync(query with { Request = new(PageSize: 20) }, Ct);
        Assert.Single(cached!.Items[0].Novel!.ReleaseCatalog!.Items);
        using var denied = await http.PostAsync("/api/v1/vns/v17/releases/refresh", null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public void DesktopDetailsDistinguishUnknownCatalogAndEmptyCatalog()
    {
        var model = new BookDetailsViewModel();
        var snapshot = MetadataImports.Snapshot(StorageTests.Entry());
        model.Update(new BookViewModel(null, snapshot), [], [], null);
        Assert.Contains("尚未查询", model.ReleaseBrowser.Summary);
        model.Update(new BookViewModel(null, snapshot with { Novel = snapshot.Novel! with { ReleaseCatalog = new([], DateTimeOffset.UtcNow) } }), [], [], null);
        Assert.Contains("0 项", model.ReleaseBrowser.Summary);
    }

    [Fact]
    public async Task CliReleaseQueryExportsPortableJsonAndThenReadsWithoutNetwork()
    {
        using var dir = new TestDirectory();
        var novel = StorageTests.Entry().Novel with { Developers = [] };
        using var handler = new StubHandler((i, _) => Task.FromResult(StubHandler.Json(i == 1
            ? JsonSerializer.Serialize(new { results = new[] { novel }, more = false }, VndbJson.Options) : Page(Release()))));
        using var output = new StringWriter(); using var error = new StringWriter();
        var app = new CliApplication(output, error, () => new HttpClient(handler, false), _ => null, VndbClientTests.FastOptions,
            new(CacheDirectory: Path.Combine(dir.Path, "json")));
        Assert.Equal(0, await app.RunAsync(["vn", "releases", "v17", "--source", "vndb", "--json", "--output", Path.Combine(dir.Path, "export.json")], Ct));
        Assert.Contains("release_catalog", await File.ReadAllTextAsync(Path.Combine(dir.Path, "export.json"), Ct));
        var calls = handler.Requests.Count; output.GetStringBuilder().Clear();
        Assert.Equal(0, await app.RunAsync(["vn", "releases", "v17", "--source", "cache", "--json"], Ct));
        Assert.Equal(calls, handler.Requests.Count); Assert.Contains("client_cache", output.ToString()); Assert.Empty(error.ToString());
    }

    [Fact]
    public void CompleteSnapshotRestoresReleaseCatalogueAndSchema11Projection()
    {
        using var dir = new TestDirectory(); var live = Path.Combine(dir.Path, "live");
        var store = new SqliteLibraryStore(live); var entry = StorageTests.Entry();
        store.Save(entry with { Novel = entry.Novel with { ReleaseCatalog = new([Release()], DateTimeOffset.UtcNow) } });
        File.WriteAllText(Path.Combine(live, "service-instance-id"), Guid.NewGuid().ToString("D"));
        var snapshot = Path.Combine(dir.Path, "snapshot"); var restored = Path.Combine(dir.Path, "restored");
        Assert.Equal(SqliteLibraryStore.SchemaVersion, LibrarySnapshot.Create(live, snapshot, "synthetic-image", cancellationToken: Ct).Schema);
        LibrarySnapshot.Restore(snapshot, restored, cancellationToken: Ct);
        var result = new SqliteLibraryStore(restored); Assert.Single(result.Get("v17")!.Novel.ReleaseCatalog!.Items);
        using var db = new SqliteConnection("Data Source=" + Path.Combine(restored, "library.db") + ";Pooling=False"); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT count(*) FROM novel_releases";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public async Task InvalidUpstreamReleasePagingIsNotMistakenForAnUnavailableLibrary()
    {
        using var dir = new TestDirectory(); var entry = StorageTests.Entry();
        var novel = entry.Novel with { Developers = [] };
        using var handler = new StubHandler((i, _) => Task.FromResult(StubHandler.Json(i == 1
            ? JsonSerializer.Serialize(new { results = new[] { novel }, more = false }, VndbJson.Options) : "{\"results\":[],\"more\":true}")));
        using var upstream = new HttpClient(handler); using var core = new VndbClient(upstream, VndbClientTests.FastOptions);
        await using var factory = new Factory(dir.Path, core); using var http = factory.CreateClient();
        var store = factory.Services.GetRequiredService<SqliteLibraryStore>();
        store.Save(entry with { Novel = entry.Novel with { ReleaseCatalog = new([Release()], DateTimeOffset.UtcNow) } });
        var before = JsonSerializer.Serialize(store.Get("v17"), VndbJson.Options);
        var client = new WebLibraryClient(http, http.BaseAddress!, "test-only-library-key", TimeSpan.FromSeconds(20));
        var error = await Assert.ThrowsAsync<ClientApiException>(() => client.QueryAsync(new("vn_releases", new(Mode: "explicit_refresh"), "v17"), Ct));
        Assert.Equal("upstream_protocol", error.Code);
        Assert.Equal(before, JsonSerializer.Serialize(store.Get("v17"), VndbJson.Options));
        Assert.Equal(2, handler.Requests.Count);
    }
}
