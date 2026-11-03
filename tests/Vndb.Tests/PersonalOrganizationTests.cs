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
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class PersonalOrganizationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Factory(string path) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b) => b.UseSetting("Library:DataDirectory", path)
            .UseSetting("Library:WriteKey", "test-only-library-key").UseSetting("CatalogJobs:Enabled", "false");
    }
    [Fact]
    public async Task WebClientEditsExactEditionsFiltersBeforePagingAndKeepsPlayStateIndependent()
    {
        using var dir = new TestDirectory(); await using var app = new Factory(dir.Path); using var http = app.CreateClient();
        var store = app.Services.GetRequiredService<SqliteLibraryStore>(); store.Save(StorageTests.Entry());
        var first = store.SaveItem(new(new("Synthetic A", "v17")), null, "a"); var second = store.SaveItem(new(new("Synthetic B", "v17")), null, "b");
        var api = new WebLibraryClient(http, http.BaseAddress!, "test-only-library-key", TimeSpan.FromSeconds(10));
        Assert.Contains("personal_organization", (await api.OrganizationInfoAsync(Ct)).Capabilities);
        var label = new PersonalLabel("group-a", "想玩", "collection");
        var state = await api.SavePersonalOrganizationAsync(new(0, label), "create", Ct);
        var edit = new PersonalOrganizationChange(state.Revision, Items: [new(first.Id, true, [label.Id]), new(second.Id, false, [label.Id])]);
        state = await api.SavePersonalOrganizationAsync(edit, "batch", Ct);
        Assert.Equal(LocalArchiveFormat.Hash(state), LocalArchiveFormat.Hash(await api.SavePersonalOrganizationAsync(edit, "batch", Ct)));
        var filtered = await http.GetFromJsonAsync<ShelfSummaryPage>("/api/v1/shelf?favorite=true&label=group-a&page_size=1", VndbJson.Options, Ct);
        Assert.Equal(1, filtered!.Total); Assert.Equal(first.Id, Assert.Single(filtered.Items).Item.Id); Assert.False(filtered.More);
        Assert.Equal(2, store.ReadItems(label: label.Id, pageSize: 1).Total); Assert.Single(store.ReadItems(favorite: false).Items);
        Assert.Equal(2, store.CollectionStatistics().Unrecorded); Assert.Equal(0, store.Plays().Total);
        Assert.Equal(2, (await api.PersonalOrganizationAsync(Ct))!.Revision);
        state = await api.SavePersonalOrganizationAsync(new(state.Revision, RemoveLabel: label.Id), "remove-label", Ct);
        Assert.Empty(state.Labels); Assert.True(Assert.Single(state.Items).Favorite); Assert.Equal(2, store.ReadItems().Total);
        await Assert.ThrowsAsync<ClientApiException>(() => api.SavePersonalOrganizationAsync(new(state.Revision, new("later", "Later", "tag")), "wrong-instance", Ct, "wrong-service"));
        Assert.Equal(state.Revision, store.ReadPersonalOrganization().Revision);
        using var unauthorized = await http.PostAsJsonAsync("/api/v1/personal-organization", new PersonalOrganizationChange(state.Revision, new("tag", "test", "tag")), VndbJson.Options, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }
    [Fact]
    public void LabelsCannotCollideOrReferenceMissingLabelsAndBatchFailureIsAtomic()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        var item = store.SaveItem(new(new("Synthetic")), null, "create-item");
        var state = store.SavePersonalOrganization(new(0, new("a", "Favorite theme", "tag")), "label");
        Assert.Throws<ArgumentException>(() => store.SavePersonalOrganization(new(state.Revision, new("b", "favorite theme", "tag")), "collision"));
        Assert.Throws<ArgumentException>(() => store.SavePersonalOrganization(new(state.Revision, Items: [new(item.Id, true, ["missing"])]), "missing-label"));
        Assert.Throws<ArgumentException>(() => store.SavePersonalOrganization(new(state.Revision, Items: [new(item.Id, true, ["a"]), new("not-an-item", false, [])]), "bad-member"));
        Assert.Empty(store.ReadPersonalOrganization().Items); Assert.Equal(1, store.ReadPersonalOrganization().Revision);
        Assert.Throws<ArgumentException>(() => store.SavePersonalOrganization(new(state.Revision, new("path", @"E:\Synthetic\Game", "tag")), "private-name"));
        Assert.Throws<PlayConflictException>(() => store.SavePersonalOrganization(new(0, new("b", "Other", "tag")), "stale"));
        store = new(dir.Path); Assert.Equal("a", Assert.Single(store.ReadPersonalOrganization().Labels).Id);
    }
    [Fact]
    public void SchemaThirteenUpgradesAdditivelyAndSurvivesReopen()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        var item = store.SaveItem(new(new("Synthetic retained")), null, "item");
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(dir.Path, "library.db") + ";Pooling=False"))
        { db.Open(); using var query = db.CreateCommand(); query.CommandText = "DROP TABLE personal_organization; PRAGMA user_version=13;"; query.ExecuteNonQuery(); }
        store = new(dir.Path); Assert.Equal(item, store.GetItem(item.Id)); Assert.Equal(0, store.ReadPersonalOrganization().Revision);
        store.SavePersonalOrganization(new(0, Items: [new(item.Id, true, [])]), "favorite");
        Assert.True(Assert.Single(new SqliteLibraryStore(dir.Path).ReadPersonalOrganization().Items).Favorite);
    }
    [Fact]
    public async Task LostReplyRetriesTheSameOperationAndConflictRetainsTheOriginalDraft()
    {
        using var server = new TestDirectory(); using var local = new TestDirectory(); using var other = new TestDirectory();
        var db = new SqliteLibraryStore(server.Path); var item = db.SaveItem(new(new("Synthetic")), null, "item");
        using var handler = new OrganizationHandler(db); using var http = new HttpClient(handler);
        var api = new WebLibraryClient(http, new("http://organization.test/"), null, TimeSpan.FromSeconds(10));
        var first = new PersonalOrganizationStore(local.Path, "http://organization.test/"); var second = new PersonalOrganizationStore(other.Path, "http://organization.test/");
        await first.RefreshAsync(api, ct: Ct); await second.RefreshAsync(api, ct: Ct);
        await first.EnqueueAsync(new(0, Items: [new(item.Id, true, [])]), Ct); handler.LoseReply = true;
        await Assert.ThrowsAsync<WebUnavailableException>(() => first.SyncAsync(api, Ct));
        Assert.Single((await first.ReadAsync(Ct)).Pending); Assert.Equal(1, db.ReadPersonalOrganization().Revision);
        Assert.Equal(0, await first.SyncAsync(api, Ct)); Assert.Equal(handler.Keys[0], handler.Keys[1]);
        await second.EnqueueAsync(new(0, new("group", "Synthetic offline", "collection")), Ct);
        Assert.Equal(1, await second.SyncAsync(api, Ct)); var conflict = await second.ReadAsync(Ct); Assert.True(conflict.Conflict); Assert.Single(conflict.Pending);
        await second.RefreshAsync(api, ct: Ct); Assert.Single((await second.ReadAsync(Ct)).Pending);
        var recovered = await second.RefreshAsync(api, true, Ct); Assert.Empty(recovered.Pending); Assert.False(recovered.Conflict); Assert.True(Assert.Single(recovered.Confirmed.Items).Favorite);
        Assert.Single(Directory.GetFiles(Path.Combine(other.Path, "personal-organization-conflicts"), "*.json"));
        Assert.DoesNotContain("root_path", JsonSerializer.Serialize(recovered.Confirmed, VndbJson.Options));
    }
    [Fact]
    public async Task OfflineQueueIsOrderedAndCannotRebindToAnotherServiceInstance()
    {
        using var server = new TestDirectory(); using var local = new TestDirectory(); var db = new SqliteLibraryStore(server.Path);
        using var handler = new OrganizationHandler(db); using var http = new HttpClient(handler); var api = new WebLibraryClient(http, new("http://organization.test/"), null, TimeSpan.FromSeconds(10));
        var store = new PersonalOrganizationStore(local.Path, "http://organization.test/"); await store.RefreshAsync(api, ct: Ct);
        await store.EnqueueAsync(new(0, new("a", "A", "collection")), Ct); await store.EnqueueAsync(new(1, new("b", "B", "tag")), Ct);
        Assert.Equal(2, PersonalOrganizationStore.View(await new PersonalOrganizationStore(local.Path, "http://organization.test/").ReadAsync(Ct)).Labels.Count);
        handler.Instance = Guid.NewGuid().ToString("D"); await Assert.ThrowsAsync<InvalidDataException>(() => store.SyncAsync(api, Ct)); Assert.Empty(handler.Keys);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PersonalOrganizationStore(local.Path, "http://other.test/").ReadAsync(Ct));
    }
    [Fact]
    public void DesktopClassificationFiltersKeepScopesAndDoNotBorrowSameNovelMembership()
    {
        var a = new BookViewModel(M5Tests.Item(), M5Tests.Item().Metadata, []); var b = new BookViewModel(M5Tests.Item() with { Item = M5Tests.Item().Item with { Id = "another-edition" } }, a.Metadata, []);
        var labels = new[] { new PersonalLabel("a", "想玩", "collection"), new PersonalLabel("b", "短篇", "tag") };
        a.SetPersonalOrganization(new(a.Id, true, ["a", "b"]), labels);
        var browser = new ShelfBrowserViewModel(); browser.SetLibrary([a, b]); browser.SetPersonalLabels(labels);
        browser.FavoritesOnly = true; browser.LabelFilter = "b"; Assert.Same(a, Assert.Single(browser.Books)); Assert.True(a.IsUnrecorded); Assert.False(b.IsFavorite);
        browser.Activate(2); Assert.False(browser.FavoritesOnly); browser.Activate(0); Assert.True(browser.FavoritesOnly); Assert.Equal("b", browser.LabelFilter);
        browser.ResetFiltersCommand.Execute(null); Assert.Equal(2, browser.Books.Count);
    }
    [Fact]
    public async Task DesktopCommandsKeepMultipleOfflineEditsAndInvalidateChangedPagePreviews()
    {
        using var dir = new TestDirectory(); var store = new PersonalOrganizationStore(dir.Path, "http://organization.test/");
        var book = new BookViewModel(M5Tests.Item(), M5Tests.Item().Metadata, []); IReadOnlyList<BookViewModel> page = [book];
        var vm = new PersonalOrganizationViewModel(() => store, () => null, () => page, state => book.SetPersonalOrganization(state.Items.SingleOrDefault(i => i.ItemId == book.Id), state.Labels), () => { });
        await vm.LoadAsync(false, Ct); vm.Select(book); vm.Name = "想玩"; await vm.CreateLabelCommand.ExecuteAsync(null);
        vm.Favorite = true; vm.MemberLabels[0].Selected = true; await vm.SaveMemberCommand.ExecuteAsync(null);
        Assert.True(book.IsFavorite); Assert.Single(book.PersonalLabelIds); Assert.Equal(2, (await store.ReadAsync(Ct)).Pending.Count);
        vm.SelectedLabel = vm.Labels[0]; vm.PreviewPageCommand.Execute(null); page = [];
        await vm.ApplyPageCommand.ExecuteAsync(null); Assert.Contains("重新预览", vm.Message); Assert.Equal(2, (await store.ReadAsync(Ct)).Pending.Count);
    }
    private sealed class OrganizationHandler(SqliteLibraryStore db) : HttpMessageHandler
    {
        public string Instance = Guid.NewGuid().ToString("D"); public bool LoseReply; public List<string> Keys { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object result;
            if (request.RequestUri!.AbsolutePath.EndsWith("/info", StringComparison.Ordinal)) result = new ServiceInfo(Instance, [1], "test", 14, false, ["personal_organization"], [1], true);
            else if (request.Method == HttpMethod.Get) result = db.ReadPersonalOrganization();
            else
            {
                var key = request.Headers.GetValues("Idempotency-Key").Single(); Keys.Add(key);
                try
                {
                    var write = (await request.Content!.ReadFromJsonAsync<PersonalOrganizationWrite>(VndbJson.Options, ct))!;
                    if (write.ServiceInstanceId != Instance) throw new PlayConflictException("Synthetic instance changed");
                    result = db.SavePersonalOrganization(write.Change, key);
                }
                catch (PlayConflictException) { return new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new ApiProblem("conflict", "Synthetic conflict", 409), options: VndbJson.Options) }; }
                if (LoseReply) { LoseReply = false; throw new HttpRequestException("Synthetic lost reply"); }
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(result, options: VndbJson.Options) };
        }
    }
}
