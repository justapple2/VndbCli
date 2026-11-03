using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.Services;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

public sealed class CoverRepairTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public void RepairIsBoundedIdempotentAndVnScopedWithoutChangingPersonalRecords()
    {
        using var f = new UiRefreshTests.Fixture(); Seed(f); var entry = f.Store.Get("v17")!;
        f.Store.Save(entry with { CoverStatus = "failed", Provenance = "vndb_dump", Completeness = "partial_text" });
        var other = f.Store.SaveItem(new(new("Second edition", "v17")), null, "cover-second-item");
        var before = f.Store.GetItem(f.Item.Id); var play = f.Store.GetPlay(f.Play.Id);
        var request = new CoverRepairRequest([f.Item.Id, other.Id]);
        var result = f.Store.RepairPersonalCovers(request, "cover-repair");
        Assert.Equal(2, result.Count); Assert.All(result, r => Assert.Equal("queued", r.State)); Assert.Single(f.Store.MetadataJobs());
        Assert.Equal(result[0].JobId, result[1].JobId); Assert.Null(Assert.Single(f.Store.MetadataJobs()).ItemId);
        Assert.Equivalent(result, f.Store.RepairPersonalCovers(request, "cover-repair"));
        Assert.Equal(before, f.Store.GetItem(f.Item.Id)); Assert.Equivalent(play, f.Store.GetPlay(f.Play.Id));
        Assert.Throws<ArgumentException>(() => f.Store.RepairPersonalCovers(new([f.Item.Id, f.Item.Id]), "duplicate-ids"));
        Assert.Throws<PlayConflictException>(() => f.Store.RepairPersonalCovers(new([other.Id]), "cover-repair"));
    }
    [Fact]
    public void RepairSkipsKnownNoCoverAndUnlinkedAndRollsBackForRemovedEntries()
    {
        using var f = new UiRefreshTests.Fixture(); Seed(f); var loose = f.Store.SaveItem(new(new("Unlinked")), null, "cover-loose");
        var result = f.Store.RepairPersonalCovers(new([f.Item.Id, loose.Id]), "cover-skip");
        Assert.Equal(new[] { "no_cover", "unlinked" }, result.Select(r => r.State)); Assert.Empty(f.Store.MetadataJobs());
        f.Store.SetItemRemoved(f.Item.Id, new(f.Store.GetItem(f.Item.Id)!.Revision), true, "cover-remove");
        Assert.Throws<PlayConflictException>(() => f.Store.RepairPersonalCovers(new([loose.Id, f.Item.Id]), "cover-removed"));
        Assert.Empty(f.Store.MetadataJobs());
    }
    [Fact]
    public void ExplicitRepairRequeuesFailedCompletedJobsButRespectsPause()
    {
        using var f = new UiRefreshTests.Fixture(); Seed(f); f.Store.Save(f.Store.Get("v17")! with { CoverStatus = "failed" });
        var first = Assert.Single(f.Store.RepairPersonalCovers(new([f.Item.Id]), "cover-first"));
        var claimed = f.Store.BeginMetadataJob(DateTimeOffset.UtcNow.AddMinutes(1))!;
        f.Store.FinishMetadataJob(claimed, [MetadataImports.Snapshot(f.Store.Get("v17")!)], null, DateTimeOffset.UtcNow);
        Assert.Equal("complete", Assert.Single(f.Store.MetadataJobs()).State);
        var next = Assert.Single(f.Store.RepairPersonalCovers(new([f.Item.Id]), "cover-next"));
        Assert.Equal(first.JobId, next.JobId); Assert.Equal("queued", next.State); Assert.Equal(0, Assert.Single(f.Store.MetadataJobs()).Attempts);
        f.Store.ControlMetadataJob(next.JobId!, "pause", "cover-pause");
        Assert.Equal("paused", Assert.Single(f.Store.RepairPersonalCovers(new([f.Item.Id]), "cover-preserve-pause")).State);
    }
    [Fact]
    public async Task ListingOfAnExistingDumpWithMissingCoverQueuesEnrichmentWithoutCreatingAnotherShelfItem()
    {
        using var f = new UiRefreshTests.Fixture(); Seed(f); f.Store.Save(f.Store.Get("v17")! with { CoverStatus = "failed", Provenance = "vndb_dump", Completeness = "partial_text" });
        var payload = JsonSerializer.SerializeToElement(new { format = "vn-library-batch", format_version = 1, kind = "metadata", batch_id = "cover-listing", items = new[] { new { vndb_id = "v17", title = "Example" } } });
        var beforeCount = f.Store.PersonalItems().Count; var imports = f.App.Services.GetRequiredService<MetadataImports>(); var batch = imports.Preview(new(payload, false)); f.Store.CommitMetadata(batch.Id, "cover-list-commit");
        Assert.True(f.Store.ProcessMetadataItem()); Assert.Single(f.Store.MetadataJobs());
        Assert.Equal(beforeCount, f.Store.PersonalItems().Count); Assert.Equal("existing", Assert.Single(f.Store.GetMetadataBatch(batch.Id).Items).Status);
        await Task.CompletedTask;
    }
    [Fact]
    public async Task FailedImageRemainsRetryableAndLaterSuccessProducesARealLocalCover()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); store.Save(StorageTests.Entry() with { CoverStatus = "failed", Provenance = "vndb_dump", Completeness = "partial_text" });
        var item = store.SaveItem(new(new("Example", "v17")), null, "cover-worker-item"); store.RepairPersonalCovers(new([item.Id]), "cover-worker");
        var clock = new Clock(); var fail = true;
        using var handler = new Routes(request => request.RequestUri!.Host == "t.vndb.org" ? fail ? StubHandler.Json("bad-image") : ImportTests.Image() :
            StubHandler.Json(request.RequestUri.AbsolutePath.EndsWith("producer", StringComparison.Ordinal) ? "{\"results\":[{\"id\":\"p1\",\"name\":\"Studio\"}],\"more\":false}" :
            "{\"results\":[{\"id\":\"v17\",\"title\":\"Example\",\"developers\":[{\"id\":\"p1\",\"name\":\"Studio\"}],\"image\":{\"id\":\"cv1\",\"url\":\"https://t.vndb.org/cv/01/1.jpg\"}}],\"more\":false}"));
        using var http = new HttpClient(handler); using var core = new VndbClient(http, VndbClientTests.FastOptions); var covers = new CoverDownloader(http, dir.Path);
        using var importer = new LibraryImporter(core, covers, store); using var imports = new MetadataImports(store, core, importer, covers, clock, new());
        Assert.True(await imports.StepAsync(Ct)); Assert.Equal("queued", Assert.Single(store.MetadataJobs()).State); Assert.Equal("failed", store.Get("v17")!.CoverStatus);
        fail = false; clock.Now += TimeSpan.FromMinutes(2); Assert.True(await imports.StepAsync(Ct));
        Assert.Equal("complete", Assert.Single(store.MetadataJobs()).State); var saved = store.Get("v17")!; Assert.Equal("ready", saved.CoverStatus);
        Assert.True(File.Exists(LibraryRules.CoverPath(dir.Path, saved.Cover!))); Assert.NotNull(MetadataImports.Snapshot(saved).Cover.Url);
    }
    [Fact]
    public async Task RepairApiRequiresAuthenticationAndStableOperationKeys()
    {
        using var f = new UiRefreshTests.Fixture(); var body = new CoverRepairRequest([f.Item.Id]);
        using var denied = await f.Http.PostAsJsonAsync("/api/v1/metadata/covers/repair", body, VndbJson.Options, Ct); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        f.Http.DefaultRequestHeaders.Add("X-Library-Key", "ui-test-key"); f.Http.DefaultRequestHeaders.Add("X-Library-Request", "1");
        using var noKey = await f.Http.PostAsJsonAsync("/api/v1/metadata/covers/repair", body, VndbJson.Options, Ct); Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        f.Http.DefaultRequestHeaders.Add("Idempotency-Key", "cover-api");
        using var accepted = await f.Http.PostAsJsonAsync("/api/v1/metadata/covers/repair", body, VndbJson.Options, Ct); Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }
    [Fact]
    public void ConfiguredDesktopDoesNotBypassTheLibraryForUndownloadedImages()
    {
        using var http = new HttpClient(); var meta = MetadataImports.Snapshot(StorageTests.Entry() with { Novel = StorageTests.Entry().Novel with { Image = new() { Id = "cv1", Url = "https://t.vndb.org/cv/01/1.jpg" } } });
        Assert.Null(new CoverCache(http, "unused", new("http://library.test/")).Source(meta));
        Assert.Equal("t.vndb.org", new CoverCache(http, "unused", new("http://library.test/")).Source(meta with { Provenance = "client_vndb" })!.Host);
        Assert.Equal("t.vndb.org", new CoverCache(http, "unused", null).Source(meta)!.Host);
    }
    [Fact]
    public void AReadyRecordWhoseAssetFileIsMissingCanBeRepaired()
    {
        using var f = new UiRefreshTests.Fixture(); Seed(f); var hash = new string('a', 64);
        f.Store.Save(f.Store.Get("v17")! with { CoverStatus = "ready", Cover = new(hash, "covers/" + hash + ".jpg", "image/jpeg", 123, "https://t.vndb.org/cv/01/1.jpg", DateTimeOffset.UtcNow) });
        Assert.Equal("queued", Assert.Single(f.Store.RepairPersonalCovers(new([f.Item.Id]), "cover-missing-file")).State);
    }
    [Theory]
    [InlineData("/api/v1/vns/v18/cover")]
    [InlineData("/api/v1/vns/v17/cover#other")]
    public void CoverReferenceCannotBorrowAnotherNovelsAsset(string path)
    {
        using var http = new HttpClient(); var meta = MetadataImports.Snapshot(StorageTests.Entry()) with { Cover = new("ready", path) };
        Assert.Throws<InvalidDataException>(() => new CoverCache(http, "unused", new("http://library.test/")).Source(meta));
    }
    private static void Seed(UiRefreshTests.Fixture fixture)
    { fixture.Store.Save(StorageTests.Entry()); fixture.Store.LinkItem(fixture.Item.Id, new("v17", fixture.Item.Revision), "cover-test-link"); }
    private sealed class Routes(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request)); }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
}
