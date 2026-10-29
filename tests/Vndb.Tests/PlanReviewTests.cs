using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.Services;
using Vndb.Library;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class PlanReviewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Factory(string directory, StubHandler upstream) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Library:DataDirectory", directory).UseSetting("Library:WriteKey", "review-test-key").UseSetting("CatalogJobs:Enabled", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<VndbClient>();
                services.AddSingleton(_ => new VndbClient(new HttpClient(upstream, false), VndbClientTests.FastOptions));
            });
        }
    }
    private sealed class Gate(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool Offline;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Offline ? throw new HttpRequestException("synthetic unavailable") : base.SendAsync(request, ct);
    }
    [Fact]
    public async Task DesktopSearchUsesSupportedWebPageSizeAndPersistsMissingMetadataBeforeReadingLocally()
    {
        using var directory = new TestDirectory(); using var clientData = new TestDirectory();
        var entry = StorageTests.Entry();
        var novel = JsonSerializer.Serialize(new { more = false, results = new[] { entry.Novel } }, VndbJson.Options);
        var producers = JsonSerializer.Serialize(new { more = false, results = entry.Producers }, VndbJson.Options);
        using var upstream = new StubHandler((call, _) => Task.FromResult(StubHandler.Json(call == 3 ? producers : novel)));
        await using var app = new Factory(directory.Path, upstream); using var http = app.CreateClient(); using var desktop = new DesktopServices(clientData.Path, http);
        await desktop.ConfigureAsync(new(http.BaseAddress!.AbsoluteUri), "review-test-key", false, Ct);
        var first = await desktop.Library.QueryAsync("Example", "web", Ct);
        Assert.Equal(20, first.PageSize); Assert.Equal("vndb", first.Source); Assert.Single(first.Items);
        Assert.NotNull(app.Services.GetRequiredService<SqliteLibraryStore>().Get("v17"));
        var count = upstream.Requests.Count; Assert.True(count > 0);
        var second = await desktop.Library.QueryAsync("Example", "web", Ct);
        Assert.Equal("library", second.Source); Assert.Single(second.Items); Assert.Equal(count, upstream.Requests.Count);
    }
    [Fact]
    public async Task DesktopShelfHonorsWebRemovalWithoutRewritingLocalMappingsAndCachesThatDecisionOffline()
    {
        using var directory = new TestDirectory(); using var clientData = new TestDirectory(); using var upstream = new StubHandler("{}");
        await using var app = new Factory(directory.Path, upstream); using var initial = app.CreateClient();
        var store = app.Services.GetRequiredService<SqliteLibraryStore>(); store.Save(StorageTests.Entry()); store.Save(StorageTests.Entry("v18"));
        var active = store.SaveItem(new(new("active", "v17")), null, "review-active");
        var stale = store.SaveItem(new(new("stale", "v18")), null, "review-stale");
        store.SetItemRemoved(stale.Id, new(stale.Revision), true, "review-remove");
        var local = new LocalLibraryStore(clientData.Path);
        await local.SaveAsync(new([], [new(active, MetadataImports.Snapshot(StorageTests.Entry()), 0), new(stale, MetadataImports.Snapshot(StorageTests.Entry("v18")), 0), M5Tests.Item(19)]), Ct);
        var original = await File.ReadAllBytesAsync(local.FilePath, Ct);
        using var gate = new Gate(app.Server.CreateHandler()); using var http = new HttpClient(gate) { BaseAddress = initial.BaseAddress };
        using var desktop = new DesktopServices(clientData.Path, http); await desktop.ConfigureAsync(new(http.BaseAddress!.AbsoluteUri), null, false, Ct);
        var online = await desktop.Library.ShelfAsync(Ct);
        Assert.Contains(online.Snapshot.Items, i => i.Item.Id == active.Id); Assert.Contains(online.Snapshot.Items, i => i.Item.Id == "item-19");
        Assert.DoesNotContain(online.Snapshot.Items, i => i.Item.Id == stale.Id);
        gate.Offline = true; var offline = await desktop.Library.ShelfAsync(Ct);
        Assert.DoesNotContain(offline.Snapshot.Items, i => i.Item.Id == stale.Id);
        Assert.Equal(original, await File.ReadAllBytesAsync(local.FilePath, Ct)); Assert.Empty(upstream.Requests);
    }
    [Fact]
    public async Task RemovalReadFailureNeverHidesAuthorizationOrOverwritesTheShelfSnapshot()
    {
        using var directory = new TestDirectory(); var file = Path.Combine(directory.Path, "shelf.json"); await File.WriteAllTextAsync(file, "retained", Ct);
        var reads = new LibraryReadOperations((_, _) => Task.FromResult(new ItemPage([], 1, 100, 0, false)),
            (_, _) => Task.FromResult<IReadOnlyList<PersonalPlayView>>([]), (_, _) => throw new ClientApiException("unauthorized", 401));
        var reader = new LocalShelfReader(new(directory.Path), directory.Path, "http://library.test/", reads);
        await Assert.ThrowsAsync<ClientApiException>(() => reader.ShelfAsync(Ct)); Assert.Equal("retained", await File.ReadAllTextAsync(file, Ct));
    }
    [Fact]
    public async Task RemovalPaginationAndLocalRemovalMarkersAreRespectedWithoutLosingUnrelatedItems()
    {
        using var directory = new TestDirectory(); var local = new LocalLibraryStore(directory.Path);
        var removed = new[] { M5Tests.Item(18), M5Tests.Item(19) }.Select(i => i with { Item = i.Item with { DeletedAt = DateTimeOffset.UtcNow } }).ToArray();
        await local.SaveAsync(new([], [M5Tests.Item(), M5Tests.Item(18), M5Tests.Item(19), removed[0] with { Item = removed[0].Item with { Id = "locally-removed" } }]), Ct);
        var pages = new List<int>();
        var reads = new LibraryReadOperations((_, _) => Task.FromResult(new ItemPage([], 1, 100, 0, false)),
            (_, _) => Task.FromResult<IReadOnlyList<PersonalPlayView>>([]), (page, _) =>
            { pages.Add(page); return Task.FromResult(new ItemPage([removed[page - 1]], page, 1, 2, page == 1)); });
        var result = await new LocalShelfReader(local, directory.Path, "http://library.test/", reads).ShelfAsync(Ct);
        Assert.Equal([1, 2], pages); Assert.Equal("item-17", Assert.Single(result.Snapshot.Items).Item.Id);
        Assert.Equal(4, (await local.ReadAsync(Ct)).Items.Count);
    }
}
