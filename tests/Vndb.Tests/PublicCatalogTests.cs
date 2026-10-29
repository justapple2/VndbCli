using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.Services;
using Vndb.Desktop.ViewModels;
using Vndb.Library;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class PublicCatalogTests
{
    [Fact]
    public void PersonalPriorityIsGlobalBeforePagingAndPreservesDistinctReplaysAndTombstones()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        foreach (var (vn, title) in new[] { ("v17", "Synthetic Z played"), ("v18", "Synthetic Y owned"), ("v19", "Synthetic A public"), ("v20", "Synthetic B removed"), ("v21", "Synthetic C review") }) Save(store, vn, title);
        var played = store.SaveItem(new(new("played", "v17")), null, "played");
        var owned = store.SaveItem(new(new("owned", "v18")), null, "owned");
        var removed = store.SaveItem(new(new("removed", "v20")), null, "removed");
        var review = store.SaveItem(new(new("review", "v21")), null, "review");
        PersonalPlayData Play(string vn, string status) => new("Synthetic", vn, status, null, null, null, null, null, null, null, null);
        store.SavePersonalPlay(new(Play("v17", "completed"), played.Id), null, "history");
        store.SavePersonalPlay(new(Play("v17", "not_started"), played.Id), null, "replay");
        store.SavePersonalPlay(new(Play("v20", "completed"), removed.Id), null, "removed-history");
        store.SetItemRemoved(removed.Id, new(removed.Revision), true, "remove");
        var bad = store.SavePersonalPlay(new(Play("v21", "completed"), review.Id), null, "bad-link");
        using (var db = new SqliteConnection("Pooling=False;Data Source=" + Path.Combine(dir.Path, "library.db")))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE play_records SET novel_id='v19' WHERE id=$id"; command.Parameters.AddWithValue("$id", bad.Id); command.ExecuteNonQuery();
        }
        var instance = Guid.NewGuid().ToString("D");
        store.SavePresence(new(Guid.NewGuid().ToString("D"), instance, Guid.NewGuid().ToString("D"), 0, [new(owned.Id, true)]), instance, "owned-presence");
        string[] expected = ["v17", "v18", "v19", "v20", "v21"];
        for (var p = 1; p <= expected.Length; p++)
        {
            var page = store.BrowseCatalog(new(Query: "Synthetic", Page: p, PageSize: 1));
            Assert.Equal(5, page.Total); Assert.Equal(expected[p - 1], Assert.Single(page.Items).Metadata.VndbId);
            Assert.Equal(expected[p - 1], Assert.Single(store.List("Synthetic", page: p, limit: 1).Results).Novel.Id);
        }
        Assert.Equal("v19", store.BrowseCatalog(new(Sort: "title", PageSize: 1)).Items[0].Metadata.VndbId);
        Assert.Equal(1, store.BrowseCatalog(new(Query: "v17")).Items[0].Personal.Played);
        Assert.Equal(1, store.BrowseCatalog(new(Query: "v21")).Items[0].Personal.NeedsReview);
        using var handler = new StubHandler("{}"); using var http = new HttpClient(handler);
        using var client = new VndbClient(http, VndbClientTests.FastOptions);
        using var importer = new LibraryImporter(client, new(http, dir.Path), store);
        using var enricher = new CatalogEnricher(client, importer, store, store);
        using var queries = new CatalogQueries(store, enricher, store, client, importer, TimeProvider.System);
        var normalized = queries.Local("vn_search", new("Syn thetic", PageSize: 1));
        Assert.Equal("v17", Assert.Single(normalized.Items).VndbId); Assert.True(normalized.More); Assert.Empty(handler.Requests);
    }
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static NovelRelease Release(string id, string vn, string lang, string type, string platform = "win", bool mtl = false) => new()
    { Id = id, Title = "Synthetic release", Languages = [new(lang, null, null, mtl, true)], Platforms = [platform], Vns = [new(vn, type)] };
    private static void Save(SqliteLibraryStore store, string vn, string title, params NovelRelease[] releases)
    {
        var entry = StorageTests.Entry(vn);
        store.Save(entry with { Novel = entry.Novel with { Title = title, ReleaseCatalog = new(releases, DateTimeOffset.UtcNow) }, Provenance = "vndb_dump", Completeness = "partial_text" });
    }
    [Fact]
    public void GlobalVersionFiltersCorrelateSameReleaseAndBundleRoleBeforeStablePagination()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        Save(store, "v17", "日本語 alpha", Release("r1", "v17", "ja", "complete"), Release("r2", "v17", "en", "trial"));
        Save(store, "v18", "beta", Release("r3", "v18", "en", "complete"));
        Save(store, "v19", "gamma", Release("r4", "v19", "en", "complete", mtl: true));
        Save(store, "v20", "delta", Release("r5", "v20", "en", "complete", "lin"));
        var q = new PublicCatalogQuery(Language: "en", ReleaseType: "complete", Platform: "win", Sort: "title", PageSize: 1);
        var page = store.BrowseCatalog(q); Assert.Equal(1, page.Total); Assert.False(page.More); Assert.Equal("v18", Assert.Single(page.Items).Metadata.VndbId);
        page = store.BrowseCatalog(q with { IncludeMtl = true }); Assert.Equal(2, page.Total); Assert.True(page.More);
        Assert.Equal("v19", Assert.Single(store.BrowseCatalog(q with { IncludeMtl = true, Page = 2 }).Items).Metadata.VndbId);
        Assert.Equal("v17", Assert.Single(store.BrowseCatalog(new(Query: "日本語" )).Items).Metadata.VndbId);
        var bundle = Release("r6", "v18", "ko", "complete") with { Vns = [new("v18", "complete"), new("v17", "trial")] };
        Save(store, "v17", "日本語 alpha", bundle); Save(store, "v18", "beta", bundle);
        var role = store.BrowseCatalog(new(Language: "ko", ReleaseType: "complete"));
        Assert.Equal("v18", Assert.Single(role.Items).Metadata.VndbId);
        Assert.Equal(0, store.Plays().Total); Assert.Empty(store.ReadItems().Items);
        Assert.Null(role.Coverage.CompletedSnapshotAt); Assert.Contains("尚无完整导入凭证", PublicCatalogRules.CoverageLabel(role.Coverage));
        Assert.Null(role.Items[0].Metadata.Novel!.ReleaseCatalog); Assert.True(role.Items[0].ReleasesKnown);
        Assert.NotNull(store.Get("v18")!.Novel.ReleaseCatalog); // compact list does not mutate persisted detail
    }
    [Fact]
    public void PersonalSummaryIsExplicitAggregateWithoutBorrowingIdentityOrWritingRecords()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); Save(store, "v17", "Synthetic"); Save(store, "v18", "Other");
        var a = store.SaveItem(new(new("A", "v17")), null, "a"); var b = store.SaveItem(new(new("B", "v17")), null, "b"); var c = store.SaveItem(new(new("C", "v17")), null, "c");
        PersonalPlayData Play(string s) => new("Synthetic", "v17", s, null, null, null, null, null, null, null, null);
        store.SavePersonalPlay(new(Play("completed"), a.Id), null, "pa"); store.SavePersonalPlay(new(Play("not_started"), a.Id), null, "replay");
        store.SavePersonalPlay(new(Play("not_started"), b.Id), null, "pb");
        var instance = Guid.NewGuid().ToString("D");
        store.SavePresence(new(Guid.NewGuid().ToString("D"), instance, Guid.NewGuid().ToString("D"), 0, [new(c.Id, true)]), instance, "presence");
        var before = JsonSerializer.Serialize(store.ReadItems(), VndbJson.Options);
        var page = store.BrowseCatalog(new()); var summary = page.Items.Single(i => i.Metadata.VndbId == "v17").Personal;
        Assert.Equal(new CatalogPersonalSummary(3, 1, 1, 1, 0, true), summary);
        Assert.Equal("○ 未记录 · 未加入书架", page.Items.Single(i => i.Metadata.VndbId == "v18").Personal.Label);
        Assert.Equal(before, JsonSerializer.Serialize(store.ReadItems(), VndbJson.Options));
        store.SetItemRemoved(a.Id, new(a.Revision), true, "remove");
        Assert.Equal(0, store.BrowseCatalog(new(Query: "v17")).Items[0].Personal.Played);
    }
    [Fact]
    public async Task DatasetCoverageRequiresCompletedProofAndNeverClaimsPartialTaskComplete()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(Path.Combine(dir.Path, "db"));
        using var http = new HttpClient(new StubHandler((_, _) => throw new InvalidOperationException("No upstream")));
        using var service = new DatasetImports(store, http); await using var file = File.OpenRead(DumpPackageTests.Archive(dir.Path));
        await service.UploadAsync(file, "upload", Ct);
        Assert.Equal("queued", store.BrowseCatalog(new()).Coverage.ActiveState); Assert.Null(store.BrowseCatalog(new()).Coverage.CompletedSnapshotAt);
        for (var i = 0; i < 20 && store.DatasetJobs()[0].State != "completed"; i++) await service.StepAsync(Ct);
        var coverage = store.BrowseCatalog(new()).Coverage; Assert.NotNull(coverage.CompletedSnapshotAt); Assert.Null(coverage.ActiveState);
    }
    private sealed class Factory(string path) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b) => b.UseSetting("Library:DataDirectory", path)
            .UseSetting("Library:WriteKey", "test-only-library-key").UseSetting("CatalogJobs:Enabled", "false");
    }
    [Fact]
    public void Schema12IndexBackfillKeepsOriginalSnapshotsAndPersonalRowsUnchanged()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        Save(store, "v17", "Synthetic", Release("r1", "v17", "ja", "complete") with { Platforms = ["win", "win"] });
        store.SaveItem(new(new("Synthetic", "v17")), null, "personal");
        var original = JsonSerializer.Serialize(store.Get("v17"), VndbJson.Options);
        var personal = JsonSerializer.Serialize(store.ReadItems(), VndbJson.Options);
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(dir.Path, "library.db") + ";Pooling=False"))
        { db.Open(); using var sql = db.CreateCommand(); sql.CommandText = "DROP TABLE release_languages; DROP TABLE release_platforms; DROP TABLE release_roles; PRAGMA user_version=12"; sql.ExecuteNonQuery(); }
        store = new SqliteLibraryStore(dir.Path);
        Assert.Single(store.BrowseCatalog(new(Language: "ja", ReleaseType: "complete", Platform: "win")).Items);
        Assert.Equal(original, JsonSerializer.Serialize(store.Get("v17"), VndbJson.Options));
        Assert.Equal(personal, JsonSerializer.Serialize(store.ReadItems(), VndbJson.Options));
        // A subsequent ordinary save without a release snapshot must not clear projections.
        var current = store.Get("v17")!; store.Save(current with { Novel = current.Novel with { ReleaseCatalog = null } });
        Assert.Single(store.BrowseCatalog(new(Language: "ja")).Items);
    }
    [Fact]
    public async Task ReadOnlyApiNeedsNoKeyAndCannotTriggerUpstreamOrExposePrivateStructure()
    {
        using var dir = new TestDirectory(); await using var app = new Factory(dir.Path); using var http = app.CreateClient();
        var store = app.Services.GetRequiredService<SqliteLibraryStore>(); Save(store, "v17", "Synthetic", Release("r1", "v17", "ja", "complete"));
        var client = new WebLibraryClient(http, http.BaseAddress!, null, TimeSpan.FromSeconds(10));
        var page = await client.BrowseCatalogAsync(new(Language: "ja", PageSize: 1), Ct); Assert.Single(page.Items);
        Assert.Empty((await client.BrowseCatalogAsync(new(Query: "no matching title"), Ct)).Items);
        using var bad = await http.GetAsync("api/v1/catalog?sort=arbitrary-sql", Ct); Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var large = await http.GetAsync("api/v1/catalog?page_size=49", Ct); Assert.Equal(HttpStatusCode.BadRequest, large.StatusCode);
        Assert.Equal(2, store.CatalogProducers("").Count); Assert.Equal("p1", Assert.Single(store.CatalogProducers("会社一")).Id);
        var json = JsonSerializer.Serialize(page, VndbJson.Options); Assert.False(WirePrivacy.ContainsPrivateData(JsonSerializer.SerializeToElement(page, VndbJson.Options)));
        Assert.DoesNotContain("directory", json); Assert.DoesNotContain("executable", json); Assert.Empty(store.ReadItems().Items);
    }
    [Fact]
    public async Task JsonFallbackIsExactScopedPageAndOnlyTransportFailureMayUseIt()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(Path.Combine(dir.Path, "server")); Save(store, "v17", "Synthetic");
        var page = store.BrowseCatalog(new()); var calls = 0;
        var reader = new PublicCatalogReader(Path.Combine(dir.Path, "json"), "http://test.local/", (_, _) => { calls++; return Task.FromResult(page); });
        await reader.ReadAsync(new(), false, Ct); Assert.Equal("web_catalog", (await reader.ReadAsync(new(), false, Ct)).Source);
        Assert.Equal(2, calls); Assert.Equal("client_catalog_cache", (await reader.ReadAsync(new(), true, Ct)).Source); Assert.Equal(2, calls);
        var offline = new PublicCatalogReader(Path.Combine(dir.Path, "json"), "http://test.local/", (_, _) => throw new CatalogUnavailableException());
        Assert.Equal("client_catalog_cache", (await offline.ReadAsync(new(), false, Ct)).Source);
        await Assert.ThrowsAsync<CatalogCacheMissException>(() => offline.ReadAsync(new(Language: "en"), false, Ct));
        foreach (var status in new[] { 401, 409, 429, 200 })
        {
            var error = new PublicCatalogReader(Path.Combine(dir.Path, "json"), "http://test.local/", (_, _) => throw new ClientApiException("protocol_invalid", status));
            await Assert.ThrowsAsync<ClientApiException>(() => error.ReadAsync(new(), false, Ct));
        }
        var cancelled = new PublicCatalogReader(Path.Combine(dir.Path, "json"), "http://test.local/", (_, _) => throw new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled.ReadAsync(new(), false, Ct));
        Assert.All(Directory.GetFiles(Path.Combine(dir.Path, "json", "public-catalog")), f => Assert.EndsWith(".json", f));
    }
    [Fact]
    public async Task InvalidPublicResponseIdentityAndCorruptCachedPageAreNotEmptySuccess()
    {
        var metadata = MetadataImports.Snapshot(StorageTests.Entry());
        var page = new PublicCatalogPage([new(metadata with { VndbId = "v99" }, new(0, 0, 0, 0, 0, false), false)], 1, 24, 1, false, new(1, null, null, 0, 0), DateTimeOffset.UtcNow);
        using var handler = new StubHandler(JsonSerializer.Serialize(page, VndbJson.Options)); using var http = new HttpClient(handler);
        var client = new WebLibraryClient(http, new("http://test.local/"), null, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ClientApiException>(() => client.BrowseCatalogAsync(new(), Ct));
        using var dir = new TestDirectory(); var cache = new PublicCatalogReader(dir.Path, "http://test.local/", (_, _) => Task.FromResult(page with { Items = [] }));
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.ReadAsync(new(), false, Ct));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "public-catalog")));
        cache = new PublicCatalogReader(dir.Path, "http://test.local/", (_, _) => Task.FromResult(page with { Items = [], Total = 0 }));
        await cache.ReadAsync(new(), false, Ct);
        var file = Directory.GetFiles(Path.Combine(dir.Path, "public-catalog")).Single();
        var corrupt = "{\"origin\":\"http://test.local/\",\"query\":{},\"page\":{}}"; await File.WriteAllTextAsync(file, corrupt, Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.ReadAsync(new(), true, Ct));
        Assert.Equal(corrupt, await File.ReadAllTextAsync(file, Ct));
    }
    [Fact]
    public async Task MalformedGeometryNeverOverwritesAValidCachedPage()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(Path.Combine(dir.Path, "server")); Save(store, "v17", "Synthetic");
        var valid = store.BrowseCatalog(new());
        var cache = Path.Combine(dir.Path, "json");
        await new PublicCatalogReader(cache, "http://test.local/", (_, _) => Task.FromResult(valid)).ReadAsync(new(), false, Ct);
        var file = Directory.GetFiles(Path.Combine(cache, "public-catalog")).Single(); var bytes = await File.ReadAllBytesAsync(file, Ct);
        var metadata = valid.Items[0].Metadata;
        foreach (var invalid in new[] { valid with { More = true }, valid with { Total = 25 }, valid with { Items = [] }, valid with { Coverage = valid.Coverage with { Processed = -1 } },
            valid with { Items = [valid.Items[0] with { Metadata = metadata with { Novel = metadata.Novel! with { Titles = [null!] } } }] } })
        {
            Assert.False(PublicCatalogRules.ValidPage(invalid, new()));
            await Assert.ThrowsAsync<InvalidDataException>(() => new PublicCatalogReader(cache, "http://test.local/", (_, _) => Task.FromResult(invalid)).ReadAsync(new(), false, Ct));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(file, Ct));
        }
        Assert.True(PublicCatalogRules.ValidPage(valid with { Items = [], Page = 2 }, new(Page: 2)));
    }
    [Fact]
    public async Task DesktopLateDetailsCannotReopenAClearedConnection()
    {
        var fake = new CatalogLibrary(); var shown = 0;
        var vm = new PublicCatalogViewModel(fake, work => work(Ct), _ => { }, _ => shown++);
        await vm.BrowseCommand.ExecuteAsync(null); vm.Selected = vm.Books[0];
        fake.PendingDetails = new(); var read = vm.DetailsCommand.ExecuteAsync(null);
        vm.Clear(); fake.PendingDetails.SetResult(new([], "web", null, 1, 24, 0, false)); await read;
        Assert.Equal(0, shown); Assert.Null(vm.Selected);
    }
    [Fact]
    public void OptionalIndexJoinsKeepPatchPlatformLanguageAndCompanySemantics()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        Save(store, "v17", "日本語", Release("r1", "v17", "ja", "complete") with { Patch = true }, Release("r2", "v17", "en", "trial", "lin"));
        Save(store, "v18", "Other", Release("r3", "v18", "ja", "complete"));
        Assert.Equal(2, store.BrowseCatalog(new()).Total);
        Assert.Equal("v17", Assert.Single(store.BrowseCatalog(new(ReleaseType: "patch")).Items).Metadata.VndbId);
        Assert.Equal(0, store.BrowseCatalog(new(Language: "en", ReleaseType: "patch")).Total);
        Assert.Equal(1, store.BrowseCatalog(new(Platform: "lin")).Total);
        Assert.Equal(0, store.BrowseCatalog(new(ProducerId: "p999")).Total);
        Assert.Equal(2, store.BrowseCatalog(new(ProducerId: "p1")).Total);
        Assert.Equal(1, store.BrowseCatalog(new(Query: "日本語", Language: "ja", Platform: "win", ReleaseType: "complete")).Total);
    }
    [Fact]
    public async Task DesktopShowsDraftPagingBoundariesAndDiscardsLatePageAfterClear()
    {
        var fake = new CatalogLibrary(); var vm = new PublicCatalogViewModel(fake, work => work(Ct), _ => { }, _ => { });
        Assert.False(vm.PreviousCommand.CanExecute(null)); Assert.False(vm.NextCommand.CanExecute(null)); Assert.False(vm.DetailsCommand.CanExecute(null));
        vm.Query = "changed"; Assert.True(vm.HasPendingFilters); vm.ResetFiltersCommand.Execute(null); Assert.False(vm.HasPendingFilters);
        await vm.BrowseCommand.ExecuteAsync(null); Assert.True(vm.HasLoaded); Assert.False(vm.IsEmpty); Assert.True(vm.NextCommand.CanExecute(null));
        vm.Language = vm.Languages[1]; Assert.True(vm.HasPendingFilters); Assert.Contains("筛选已修改", vm.FilterStatus);
        vm.Selected = vm.Books[0]; Assert.True(vm.DetailsCommand.CanExecute(null));
        var pending = new TaskCompletionSource<PublicCatalogPage>(); fake.Pending = pending;
        var read = vm.NextCommand.ExecuteAsync(null); Assert.True(vm.IsLoading); Assert.False(vm.BrowseCommand.CanExecute(null)); Assert.False(vm.NextCommand.CanExecute(null));
        vm.Clear(); pending.SetResult(new([], 2, 24, 0, false, new(0, null, null, 0, 0), DateTimeOffset.UtcNow)); await read;
        Assert.False(vm.HasLoaded); Assert.Empty(vm.Books); Assert.False(vm.NextCommand.CanExecute(null));
    }
    [Fact]
    public async Task DesktopPagingUsesAppliedGlobalFiltersAndFailuresKeepLastSuccessfulPage()
    {
        var fake = new CatalogLibrary(); string status = "";
        var vm = new PublicCatalogViewModel(fake, work => work(Ct), text => status = text, _ => { });
        vm.Language = vm.Languages.Single(l => l.Code == "ja"); await vm.BrowseCommand.ExecuteAsync(null);
        vm.Language = vm.Languages.Single(l => l.Code == "en"); await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(2, fake.Last!.Page); Assert.Equal("ja", fake.Last.Language);
        var book = Assert.Single(vm.Books); Assert.Contains("未记录", book.Personal); Assert.Contains("未上报", book.Presence);
        fake.Fail = true; await Assert.ThrowsAsync<IOException>(() => vm.BrowseCommand.ExecuteAsync(null)); Assert.Same(book, Assert.Single(vm.Books));
        vm.Clear(); Assert.Empty(vm.Books); Assert.Null(vm.Selected); Assert.Contains("重新浏览", vm.Summary);
    }
    private sealed class CatalogLibrary : IDesktopLibrary
    {
        public PublicCatalogQuery? Last; public bool Fail; public TaskCompletionSource<PublicCatalogPage>? Pending;
        public TaskCompletionSource<MetadataResponse>? PendingDetails;
        public Task<PublicCatalogPage> BrowseCatalogAsync(PublicCatalogQuery q, bool cacheOnly, CancellationToken ct)
        {
            if (Fail) throw new IOException("Synthetic offline"); Last = q;
            if (Pending is { } pending) return pending.Task;
            return Task.FromResult(new PublicCatalogPage([new(MetadataImports.Snapshot(StorageTests.Entry()), new(0, 0, 0, 0, 0, false), false)], q.Page, q.PageSize, 100, true, new(100, null, "paused", 2, 100), DateTimeOffset.UtcNow));
        }
        public Task<ShelfResult> ShelfAsync(CancellationToken ct) => throw new InvalidOperationException();
        public Task<MetadataResponse> QueryAsync(string text, string source, CancellationToken ct) => PendingDetails?.Task ?? throw new InvalidOperationException();
        public Task<IReadOnlyList<PersonalPlayView>> PlaysAsync(string itemId, CancellationToken ct) => throw new InvalidOperationException();
    }
}
