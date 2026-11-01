using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.ViewModels;
using Vndb.Library;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class ShelfSummaryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static LibraryItemView Summary(int n = 17) => ShelfSummaries.Item(M5Tests.Item(n)) with
    { State = new(PersonalProgress.FromStatuses(["completed", "not_started"]), true, "not_started"), PlayCount = 2 };
    private sealed class Factory(string root) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b) => b.UseSetting("Library:DataDirectory", root)
            .UseSetting("Library:WriteKey", "summary-test-key").UseSetting("CatalogJobs:Enabled", "false");
    }
    [Fact]
    public async Task NewRoutesMatchExactPersonalStateAndKeepFullOldApiAndDetails()
    {
        using var dir = new TestDirectory(); await using var app = new Factory(dir.Path); using var http = app.CreateClient();
        var store = app.Services.GetRequiredService<SqliteLibraryStore>(); var entry = StorageTests.Entry();
        store.Save(entry with { Novel = entry.Novel with { Description = new string('x', 100_000) } });
        var item = store.SaveItem(new(new("Synthetic", "v17", Notes: "Long private notes", ExtraNotes: "keep-two")), null, "create");
        foreach (var status in new[] { "completed", "not_started" })
            store.SavePersonalPlay(new(new("Synthetic", "v17", status, null, null, null, null, "long review", null, null, null), item.Id), null, "play-" + status);
        var api = new WebLibraryClient(http, http.BaseAddress!, null, TimeSpan.FromSeconds(10));
        Assert.Contains("shelf_summary_v1", (await api.OrganizationInfoAsync(Ct)).Capabilities);
        var page = await api.ShelfSummaryAsync(1, false, Ct); ShelfSummaries.RequirePage(page!, 1, false);
        var row = Assert.Single(page!.Items); Assert.Equal(item.Id, row.Item.Id); Assert.Equal(2, row.PlayCount);
        Assert.True(row.State!.Progress.HasPlayed); Assert.False(row.State.Progress.NeedsReview);
        Assert.Null(row.Item.Details.Notes); Assert.Null(row.Metadata!.Novel!.Description); Assert.Equal("会社一", row.Metadata.Developers[0].Original);
        Assert.Equal("Long private notes", (await api.ItemAsync(item.Id, Ct)).Item.Details.Notes);
        Assert.Equal(100_000, (await api.ItemsAsync(1, Ct)).Items[0].Metadata!.Novel!.Description!.Length);
        var head = Assert.Single((await api.ItemHeadsAsync(1, false, Ct))!.Items); Assert.Equal(item.Revision, head.Revision);
        store.SetItemRemoved(item.Id, new(item.Revision), true, "remove");
        Assert.Empty((await api.ItemHeadsAsync(1, false, Ct))!.Items); Assert.True(Assert.Single((await api.ItemHeadsAsync(1, true, Ct))!.Items).Deleted);
        using var filtered = await http.GetAsync("/api/v1/shelf?progress=played", Ct);
        Assert.Empty((await filtered.Content.ReadFromJsonAsync<ShelfSummaryPage>(VndbJson.Options, Ct))!.Items);
        using var invalid = await http.GetAsync("/api/v1/shelf?page_size=101", Ct); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
    private sealed class Negotiation(bool modern, HttpStatusCode infoStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath; Paths.Add(path);
            object value = path switch
            {
                "/api/v1/info" => new ServiceInfo("test", [1], "test", 1, false, modern ? ["shelf_summary_v1", "item_heads_v1"] : [], [1], true),
                "/api/v1/shelf" => new ShelfSummaryPage([], 1, 100, 0, false),
                "/api/v1/item-heads" => new ItemHeadPage([], 1, 100, 0, false),
                _ => throw new InvalidOperationException("Unexpected synthetic route")
            };
            return Task.FromResult(new HttpResponseMessage(path == "/api/v1/info" ? infoStatus : HttpStatusCode.OK) { Content = JsonContent.Create(value, options: VndbJson.Options) });
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CapabilityAbsenceIsTheOnlyLegacyFallbackAndDiscoveryIsReused(bool modern)
    {
        using var handler = new Negotiation(modern); using var http = new HttpClient(handler); var api = new WebLibraryClient(http, new("http://summary.test/"), null, TimeSpan.FromSeconds(2));
        Assert.Equal(modern, await api.ShelfSummaryAsync(1, false, Ct) is not null);
        Assert.Equal(modern, await api.ItemHeadsAsync(1, false, Ct) is not null);
        Assert.Equal(1, handler.Paths.Count(p => p == "/api/v1/info"));
    }
    [Fact]
    public async Task AuthenticationFailureCannotSilentlySelectLegacyRead()
    {
        using var http = new HttpClient(new Negotiation(false, HttpStatusCode.Unauthorized)); var api = new WebLibraryClient(http, new("http://summary.test/"), null, TimeSpan.FromSeconds(2));
        Assert.Equal(401, (await Assert.ThrowsAsync<ClientApiException>(() => api.ShelfSummaryAsync(1, false, Ct))).Status);
    }
    [Fact]
    public async Task SummaryShelfNeverFetchesAllPlaysAndLazyDetailsRetainNotesAndOfflineScope()
    {
        using var dir = new TestDirectory(); var playsRead = 0; var detailsRead = 0; var full = M5Tests.Item() with { Item = M5Tests.Item().Item with { Details = new("Synthetic", "v17", Notes: "keep") } };
        var remote = new LibraryReadOperations((_, _) => throw new InvalidOperationException("No full list"),
            (id, _) => { Assert.NotNull(id); playsRead++; return Task.FromResult<IReadOnlyList<PersonalPlayView>>([]); },
            Summaries: (_, deleted, _) => Task.FromResult<ShelfSummaryPage?>(new(deleted ? [] : [Summary()], 1, 100, deleted ? 0 : 1, false)),
            Item: (_, _) => { detailsRead++; return Task.FromResult(full); });
        var reader = new LocalShelfReader(new(dir.Path), dir.Path, "http://summary.test/", remote);
        var snapshot = (await reader.ShelfAsync(Ct)).Snapshot; Assert.True(snapshot.SummaryOnly); Assert.Empty(snapshot.Plays); Assert.Equal(0, playsRead);
        var book = new BookViewModel(Summary(), Summary().Metadata, [], summaryOnly: true);
        Assert.True(book.HasPlayed); Assert.False(book.IsUnplayed); Assert.False(book.NeedsPlayReview); Assert.Equal("未游玩", book.ShelfState); Assert.True(book.DetailsPending);
        var unknown = Summary() with { PlayCount = 0, State = new(PersonalProgress.FromStatuses([]), false) };
        Assert.Contains("未记录游玩", new BookViewModel(unknown, unknown.Metadata, [], summaryOnly: true).PlaySummary);
        book.ApplyDetails(await reader.ItemAsync("item-17", Ct)); Assert.Equal("keep", book.Item!.Item.Details.Notes); Assert.False(book.DetailsPending); Assert.Equal(1, detailsRead);
        Assert.True(book.HasPlayed); // Metadata completion is not a claim that absent play details mean unplayed.
        await reader.PlaysAsync("item-17", Ct); Assert.Equal(1, playsRead);
        var offline = new LocalShelfReader(new(dir.Path), dir.Path, "http://summary.test/"); Assert.Empty(await offline.PlaysAsync("item-17", Ct));
        Assert.Equal("keep", (await offline.ItemAsync("item-17", Ct)).Item.Details.Notes);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalShelfReader(new(dir.Path), dir.Path, "http://other.test/").ItemAsync("item-17", Ct));
    }
    [Fact]
    public async Task InterruptedOrInconsistentSummaryPagesNeverReplaceExistingSnapshot()
    {
        using var dir = new TestDirectory(); var file = Path.Combine(dir.Path, "shelf.json");
        await JsonSnapshotStore.AtomicWriteAsync(file, new ShelfSnapshot("http://summary.test/", [Summary()], [], [], DateTimeOffset.UtcNow, SummaryOnly: true), false, Ct);
        var before = await File.ReadAllBytesAsync(file, Ct);
        var remote = new LibraryReadOperations((_, _) => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException(),
            Summaries: (page, _, _) => page == 1 ? Task.FromResult<ShelfSummaryPage?>(new(Enumerable.Range(1, 100).Select(Summary).ToArray(), 1, 100, 101, true)) : throw new InvalidDataException("incomplete"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalShelfReader(new(dir.Path), dir.Path, "http://summary.test/", remote).ShelfAsync(Ct));
        Assert.Equal(before, await File.ReadAllBytesAsync(file, Ct));
    }
    [Fact]
    public async Task OfflineUnloadedPersonalSummaryCannotBeEditedAsAnEmptyPlayList()
    {
        using var dir = new TestDirectory(); var service = new QuickPlayService(new(dir.Path), () => "http://summary.test/", () => null, (_, _) => throw new WebUnavailableException());
        await Assert.ThrowsAsync<ArgumentException>(() => service.LoadAsync(new("item-17", "Synthetic", Summary(), [], PlaysPending: true), Ct));
        Assert.Empty(await new PersonalEditOutbox(dir.Path).PendingAsync("http://summary.test/", Ct));
    }
    [Fact]
    public async Task MalformedSummaryCacheIsARecoverableFailureAndNeverOverwritten()
    {
        using var dir = new TestDirectory(); var path = Path.Combine(dir.Path, "shelf.json");
        const string bad = "{\"origin\":\"http://summary.test/\",\"items\":null,\"summary_only\":true}";
        await File.WriteAllTextAsync(path, bad, Ct);
        var failure = await Record.ExceptionAsync(() => new LocalShelfReader(new(dir.Path), dir.Path, "http://summary.test/").ShelfAsync(Ct));
        Assert.True(failure is InvalidDataException or JsonException); // Nullable JSON may be rejected by the serializer before shape validation.
        Assert.Equal(bad, await File.ReadAllTextAsync(path, Ct));
    }
}
