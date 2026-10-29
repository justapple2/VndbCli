using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class GovernanceQueryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static CatalogQuery Get => new("vn_get", Id: "v17");
    private static CatalogResult Empty(string source) => new([], source, null, 1, 10, 0, false);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Source(Func<CatalogQuery, CancellationToken, Task<CatalogResult>> action) : ICatalogSource
    {
        public int Calls { get; private set; }
        public Task<CatalogResult> QueryAsync(CatalogQuery query, CancellationToken ct) { Calls++; return action(query, ct); }
    }
    private sealed class Snapshots : ICatalogSnapshots
    {
        public CatalogResult? Value { get; set; }
        public Exception? WriteError { get; init; }
        public int Reads { get; private set; }
        public Task<CatalogResult?> ReadAsync(CatalogQuery query, CancellationToken ct) { Reads++; return Task.FromResult(Value); }
        public Task SaveAsync(CatalogQuery query, CatalogResult result, CancellationToken ct)
        { if (WriteError is not null) throw WriteError; Value = result; return Task.CompletedTask; }
    }

    [Fact]
    public async Task CoreTreatsRemoteEmptySuccessAsAuthoritativeWithoutHttpOrDisk()
    {
        var upstream = new Source((_, _) => throw new InvalidOperationException("must not query upstream"));
        var remote = new Source((_, _) => Task.FromResult(Empty("library")));
        var snapshots = new Snapshots { Value = Empty("client_cache") with { PendingImport = true } };
        var result = await new ClientCatalogQuery(upstream, snapshots, remote).QueryAsync(Get, ct: Ct);
        Assert.Equal("library", result.Source); Assert.False(result.PendingImport);
        Assert.Equal(0, upstream.Calls); Assert.Equal(0, snapshots.Reads); Assert.Same(result, snapshots.Value);
    }

    [Fact]
    public async Task CoreBackoffUsesCacheThenReturnsToRemoteAfterTheDeadline()
    {
        var clock = new Clock(); var unavailable = true;
        var remote = new Source((_, _) => unavailable ? throw new CatalogUnavailableException() : Task.FromResult(Empty("library")));
        var upstream = new Source((_, _) => throw new InvalidOperationException("cache exists"));
        var snapshots = new Snapshots { Value = Empty("client_cache") };
        var service = new ClientCatalogQuery(upstream, snapshots, remote, clock);
        Assert.Equal("client_cache", (await service.QueryAsync(Get, ct: Ct)).Source);
        unavailable = false;
        Assert.Equal("client_cache", (await service.QueryAsync(Get, ct: Ct)).Source);
        Assert.Equal(1, remote.Calls);
        clock.Now += TimeSpan.FromSeconds(31);
        Assert.Equal("library", (await service.QueryAsync(Get, ct: Ct)).Source);
        Assert.Equal(2, remote.Calls); Assert.Equal(0, upstream.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoreNeverTurnsCorruptDataOrFailedPersistenceIntoAQueryFallback(bool failedSave)
    {
        var upstream = new Source((_, _) => throw new InvalidOperationException("must not fall back"));
        var remote = new Source((_, _) => failedSave ? Task.FromResult(Empty("library")) : throw new InvalidDataException("bad protocol"));
        var snapshots = new Snapshots { WriteError = failedSave ? new IOException("disk failure") : null };
        var service = new ClientCatalogQuery(upstream, snapshots, remote);
        if (failedSave) await Assert.ThrowsAsync<IOException>(() => service.QueryAsync(Get, ct: Ct));
        else await Assert.ThrowsAsync<InvalidDataException>(() => service.QueryAsync(Get, ct: Ct));
        Assert.Equal(0, upstream.Calls); Assert.Equal(0, snapshots.Reads); Assert.Null(snapshots.Value);
    }

    [Fact]
    public async Task CoreKeepsAnOfflineUnknownDistinctFromAValidEmptyResult()
    {
        var upstream = new Source((_, _) => throw new HttpRequestException("offline"));
        var remote = new Source((_, _) => throw new CatalogUnavailableException());
        await Assert.ThrowsAsync<CatalogCacheMissException>(() => new ClientCatalogQuery(upstream, new Snapshots(), remote).QueryAsync(Get, ct: Ct));
        Assert.Equal(1, upstream.Calls);
    }

    [Fact]
    public void MetadataMappingPreservesAllPublicFlagsAndNestedMetadata()
    {
        var metadata = M5Tests.Item().Metadata! with { Completeness = "partial_text", Provenance = "client_vndb_import" };
        var wire = new MetadataResponse([metadata], "client_cache", metadata.FetchedAt, 2, 20, null, true,
            true, true, true, "client_snapshot", ["test warning"]);
        Assert.Equal(JsonSerializer.Serialize(wire, VndbJson.Options), JsonSerializer.Serialize(wire.ToCatalog().ToMetadata(), VndbJson.Options));
        Assert.Equal("v17", CatalogQuery.FromText(" v17 ").Id);
        Assert.Equal("vn_search", CatalogQuery.FromText("test title").Kind);
    }

    [Fact]
    public async Task ShelfReaderMergesPagesAndLocalItemsAndKeepsOfflineSnapshotsWithoutDesktop()
    {
        using var directory = new TestDirectory(); var clock = new Clock(); var offline = false;
        var webItem = M5Tests.Item(); var collision = webItem with { Item = webItem.Item with { Details = new("local duplicate", "v17") } };
        var store = new LocalLibraryStore(directory.Path);
        await store.SaveAsync(new([], [collision, M5Tests.Item(18)]), Ct);
        var pages = new List<int>();
        var reads = new LibraryReadOperations((page, _) =>
        {
            if (offline) throw new CatalogUnavailableException(); pages.Add(page);
            return Task.FromResult(new ItemPage([page == 1 ? webItem : M5Tests.Item(19)], page, 1, 2, page == 1));
        }, (_, _) => offline ? throw new CatalogUnavailableException() : Task.FromResult<IReadOnlyList<PersonalPlayView>>([]));
        var reader = new LocalShelfReader(store, directory.Path, "http://library.test/", reads, clock);
        var first = await reader.ShelfAsync(Ct);
        Assert.Equal(ShelfSource.Web, first.Source); Assert.Equal([1, 2], pages);
        Assert.Equal(["item-17", "item-19", "item-18"], first.Snapshot.Items.Select(i => i.Item.Id));
        Assert.Equal(webItem.Item.Details, first.Snapshot.Items[0].Item.Details);
        Assert.Empty(await reader.PlaysAsync("item-17", Ct));
        offline = true; clock.Now += TimeSpan.FromDays(1);
        var cached = await reader.ShelfAsync(Ct);
        Assert.Equal(ShelfSource.Snapshot, cached.Source); Assert.Equal(first.Snapshot.FetchedAt, cached.Snapshot.FetchedAt);
        Assert.Empty(await reader.PlaysAsync("item-17", Ct));
    }

    [Fact]
    public async Task ShelfReaderDoesNotHideAuthorizationFailureOrOverwriteExistingSnapshot()
    {
        using var directory = new TestDirectory(); var store = new LocalLibraryStore(directory.Path);
        var snapshotFile = Path.Combine(directory.Path, "shelf.json");
        const string original = "retained original";
        await File.WriteAllTextAsync(snapshotFile, original, Ct);
        var reads = new LibraryReadOperations((_, _) => throw new ClientApiException("unauthorized", 401),
            (_, _) => throw new InvalidOperationException("not reached"));
        await Assert.ThrowsAsync<ClientApiException>(() => new LocalShelfReader(store, directory.Path, "http://library.test/", reads).ShelfAsync(Ct));
        Assert.Equal(original, await File.ReadAllTextAsync(snapshotFile, Ct));
    }

    private sealed class Factory(string directory, HttpMessageHandler upstream, Clock clock) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Library:DataDirectory", directory).UseSetting("Library:WriteKey", "test-key").UseSetting("CatalogJobs:Enabled", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<VndbClient>(); services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton(_ => new VndbClient(new HttpClient(upstream, false), VndbClientTests.FastOptions));
            });
        }
    }

    [Fact]
    public async Task LegacyAndV1QueriesSharePersistentNegativeCacheAndItsExpiry()
    {
        using var directory = new TestDirectory(); var clock = new Clock();
        using var upstream = new StubHandler("""{"results":[],"more":false}""");
        await using (var factory = new Factory(directory.Path, upstream, clock))
        {
            using var http = factory.CreateClient(); http.DefaultRequestHeaders.Add("X-Library-Key", "test-key"); http.DefaultRequestHeaders.Add("X-Library-Request", "1");
            using var first = await http.PostAsJsonAsync("/api/v1/queries/vns", new MetadataQuery("test missing"), VndbJson.Options, Ct);
            first.EnsureSuccessStatusCode();
            using var second = await http.PostAsJsonAsync("/api/search", new Vndb.Library.LibrarySearchRequest("test missing", Limit: 10), VndbJson.Options, Ct);
            second.EnsureSuccessStatusCode();
            Assert.Equal("local-query-cache", (await second.Content.ReadFromJsonAsync<Vndb.Library.LibrarySearchResult>(VndbJson.Options, Ct))!.Source);
            Assert.Single(upstream.Requests);
        }
        await using var restarted = new Factory(directory.Path, upstream, clock);
        using var client = restarted.CreateClient(); client.DefaultRequestHeaders.Add("X-Library-Key", "test-key"); client.DefaultRequestHeaders.Add("X-Library-Request", "1");
        using var remembered = await client.PostAsJsonAsync("/api/search", new Vndb.Library.LibrarySearchRequest("test missing", Limit: 10), VndbJson.Options, Ct);
        remembered.EnsureSuccessStatusCode(); Assert.Single(upstream.Requests);
        clock.Now += TimeSpan.FromHours(7);
        using var expired = await client.PostAsJsonAsync("/api/search", new Vndb.Library.LibrarySearchRequest("test missing", Limit: 10), VndbJson.Options, Ct);
        expired.EnsureSuccessStatusCode(); Assert.Equal(2, upstream.Requests.Count);
    }
}
