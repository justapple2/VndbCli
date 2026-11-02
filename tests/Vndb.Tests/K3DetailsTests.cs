using System.Text.Json;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.Services;
using Vndb.Desktop.ViewModels;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class K3DetailsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static VisualNovel Novel(string id = "v17") => new()
    {
        Id = id, Title = "Synthetic Latin", AltTitle = "原文", OriginalLanguage = "en", Rating = 84, VoteCount = 12,
        Titles = [new() { Lang = "ja", Title = "合成作品", Latin = "Gousei", Main = false }, new() { Lang = "en", Title = "Original English", Latin = "Latin English", Main = true }],
        Relations = [new("v18", "seq", true)], Languages = ["ja"], Platforms = ["win"]
    };
    private static MetadataSnapshot Metadata(VisualNovel? novel = null) => new((novel ??= Novel()).Id, novel.Title, "vn", novel, [], null, new("none"), DateTimeOffset.UtcNow, "vndb_dump");
    private static LibraryItemView Item(VisualNovel? novel = null)
    { var item = M5Tests.Item(); return item with { Metadata = Metadata(novel) }; }

    [Theory]
    [InlineData("japanese", "合成作品", "")]
    [InlineData("original", "Original English", "合成作品")]
    [InlineData("latin", "Latin English", "合成作品")]
    public void DisplayPreferencesNeverAlterArchiveTitleOrPersistedIdentity(string preference, string title, string subtitle)
    {
        var row = Item(); var before = JsonSerializer.Serialize(row, VndbJson.Options); var book = new BookViewModel(row, row.Metadata);
        book.SetTitlePreference(preference, true);
        Assert.Equal(title, book.Title); Assert.Equal(subtitle, book.JapaneseSubtitle); Assert.Equal("合成作品", book.ArchiveTitle);
        Assert.Equal(before, JsonSerializer.Serialize(book.Item, VndbJson.Options));
        book.SetTitlePreference(preference, false); Assert.Empty(book.JapaneseSubtitle);
    }
    [Fact]
    public void OverridesAndUnknownFallbacksRemainExplicit()
    {
        var row = Item(); row = row with { Item = row.Item with { Details = row.Item.Details with { TitleOverride = "My title", CompanyOverride = "My company" } } };
        var book = new BookViewModel(row, row.Metadata); book.SetTitlePreference("latin", true);
        Assert.Equal("My title", book.Title); Assert.Equal("My company", book.GroupCompany);
        Assert.Equal("合成作品", book.JapaneseSubtitle);
        Assert.Equal("Fallback", NovelPresentation.Title(new() { Id = "v1", Title = "", AltTitle = " " }, "Fallback").Title);
        Assert.Equal("未命名", NovelPresentation.Title(null, " ").Title);
        Assert.Equal("8.40 / 10 · 12 票", NovelPresentation.PublicRating(84, 12)); Assert.Equal("暂无评分", NovelPresentation.PublicRating(null, 0));
        Assert.Contains("future-code", NovelPresentation.Relation("future-code"));
    }
    [Fact]
    public async Task KanaRelationsMapAtApiBoundaryWithoutChangingSnapshotFieldNames()
    {
        using var handler = new StubHandler("""{"results":[{"id":"v17","title":"Synthetic","relations":[{"id":"v18","relation":"seq","relation_official":true}]}],"more":false}""");
        using var http = new HttpClient(handler); using var client = new VndbClient(http, VndbClientTests.FastOptions);
        var novel = await client.GetNovelAsync("v17", Ct); Assert.True(Assert.Single(novel!.Relations!).Official);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!); Assert.Contains("relations{id,relation,relation_official}", body.RootElement.GetProperty("fields").GetString());
        var saved = JsonSerializer.Serialize(novel, VndbJson.Options); using var savedDocument = JsonDocument.Parse(saved);
        Assert.True(savedDocument.RootElement.GetProperty("relations")[0].GetProperty("official").GetBoolean()); Assert.DoesNotContain("relation_official", saved);
        Assert.True(Assert.Single(JsonSerializer.Deserialize<VisualNovel>(saved, VndbJson.Options)!.Relations!).Official);
        var legacy = JsonSerializer.Serialize(Novel() with { Relations = null }, VndbJson.Options); Assert.DoesNotContain("\"relations\"", legacy);
        using var emptyDocument = JsonDocument.Parse(JsonSerializer.Serialize(Novel() with { Relations = [] }, VndbJson.Options)); Assert.Equal(0, emptyDocument.RootElement.GetProperty("relations").GetArrayLength());
    }
    [Theory]
    [InlineData("\"official\":true")]
    [InlineData("\"relation_official\":null")]
    public async Task KanaMissingOfficialFieldIsNotSilentlyConvertedToFalse(string field)
    {
        using var handler = new StubHandler("{\"results\":[{\"id\":\"v17\",\"title\":\"Synthetic\",\"relations\":[{\"id\":\"v18\",\"relation\":\"seq\"," + field + "}]}],\"more\":false}");
        using var http = new HttpClient(handler); using var client = new VndbClient(http, VndbClientTests.FastOptions);
        await Assert.ThrowsAsync<JsonException>(() => client.GetNovelAsync("v17", Ct));
    }
    private sealed class Source(Func<CatalogQuery, CatalogResult> read) : ICatalogSource
    {
        public List<CatalogQuery> Queries { get; } = [];
        public Task<CatalogResult> QueryAsync(CatalogQuery query, CancellationToken ct) { Queries.Add(query); return Task.FromResult(read(query)); }
    }
    private sealed class Snapshots(CatalogResult? cached = null) : ICatalogSnapshots
    {
        public CatalogQuery? ReadQuery, SavedQuery;
        public Task<CatalogResult?> ReadAsync(CatalogQuery query, CancellationToken ct) { ReadQuery = query; return Task.FromResult(cached); }
        public Task SaveAsync(CatalogQuery query, CatalogResult result, CancellationToken ct) { SavedQuery = query; return Task.CompletedTask; }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RelatedLocalReadNeverFallsThroughToVndbAndKeepsPreviouslyCachedData(bool unavailable)
    {
        var upstream = new Source(_ => throw new InvalidOperationException("Must not call VNDB"));
        var empty = new CatalogResult([], "web_local", null, 1, 10, 0, false);
        var previous = empty with { Items = [new("v17", "Cached", "vn", Novel(), [], null, new("none"), DateTimeOffset.UtcNow, "client_vndb")] };
        var remote = new Source(_ => unavailable ? throw new CatalogUnavailableException() : empty); var snapshots = new Snapshots(previous);
        var service = new ClientCatalogQuery(upstream, snapshots, remote);
        Assert.Same(previous, await service.ReadLocalAsync(new("vn_get", Id: "v17", Mode: "explicit_refresh"), Ct));
        Assert.Equal("local_only", Assert.Single(remote.Queries).Mode); Assert.Equal("local_first", snapshots.ReadQuery!.Mode); Assert.Null(snapshots.SavedQuery); Assert.Empty(upstream.Queries);
        if (unavailable) { await service.ReadLocalAsync(new("vn_get", Id: "v17"), Ct); Assert.Single(remote.Queries); }
        await Assert.ThrowsAsync<CatalogCacheMissException>(() => new ClientCatalogQuery(upstream, new Snapshots()).ReadLocalAsync(new("vn_get", Id: "v17"), Ct));
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadLocalAsync(new("vn_get", Id: "v17"), canceled.Token));
    }
    [Fact]
    public async Task OfflineRelatedReadReusesNormalDesktopJsonWithoutLosingPendingImport()
    {
        using var dir = new TestDirectory(); var snapshots = new JsonSnapshotStore(dir.Path);
        await snapshots.SaveAsync(ClientQuery.FromText("v17", 20), new([Metadata()], "client_vndb", DateTimeOffset.UtcNow, 1, 20, 1, false, PendingImport: true), Ct);
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("No VNDB access from related navigation"));
        using var http = new HttpClient(handler); using var core = new VndbClient(http, VndbClientTests.FastOptions);
        var response = await new QueryRouter(core, snapshots).ReadLocalAsync("v17", Ct);
        Assert.Equal("v17", Assert.Single(response.Items).VndbId); Assert.True(response.PendingImport); Assert.Empty(handler.Requests);
        Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "pending"), "*.json"));
    }
    [Fact]
    public void ReleaseDetailsAreBoundedAndUseTheExactNovelTypeOfABundle()
    {
        var releases = Enumerable.Range(1, 45).Select(n => new NovelRelease { Id = "r" + n, Title = "Release " + n, Official = false, Patch = true, Released = "2099-12",
            Languages = [new(n == 45 ? "en" : "zh-Hans", null, null, true, true)], Platforms = ["win"], Vns = [new("v18", "complete"), new("v17", "trial")] }).ToArray();
        var book = new BookViewModel(null, Metadata(Novel() with { ReleaseCatalog = new(releases, DateTimeOffset.UtcNow) }));
        var details = new BookDetailsViewModel(); details.Update(book, [], [], null);
        Assert.False(details.IsPersonal); Assert.Empty(details.PersonalFields); Assert.Empty(details.Plays); Assert.False(details.LaunchConfigured);
        Assert.Equal(20, details.ReleaseBrowser.Rows.Count); Assert.True(details.ReleaseBrowser.HasMore);
        Assert.All(details.ReleaseBrowser.Rows, r => { Assert.Contains("试玩", r.Summary); Assert.Contains("机翻", r.Summary); Assert.Contains("非官方", r.Summary); Assert.Contains("补丁", r.Summary); Assert.Contains("2099-12", r.Summary); });
        details.ReleaseBrowser.MoreCommand.Execute(null); Assert.Equal(40, details.ReleaseBrowser.Rows.Count);
        details.ReleaseBrowser.Filter = "英语"; Assert.Equal("r45", Assert.Single(details.ReleaseBrowser.Rows).Id); Assert.Contains("本机具体版本未知", details.ReleaseBrowser.Summary);
        details.Update(new(null, Metadata(Novel() with { ReleaseCatalog = new([], DateTimeOffset.UtcNow), Relations = [] })), [], [], null);
        Assert.Contains("未返回", details.ReleaseBrowser.Summary); Assert.Empty(details.Relations.Rows); Assert.Contains("未列出", details.Relations.Summary);
        details.Update(new(null, Metadata()), [], [], null); Assert.Contains("尚未查询", details.ReleaseBrowser.Summary);
    }
    [Fact]
    public async Task LegacyPreferencesKeepUnknownFieldsAndNormalizeInvalidDisplayOptions()
    {
        using var dir = new TestDirectory(); var path = Path.Combine(dir.Path, "ui-preferences.json");
        await File.WriteAllTextAsync(path, """{"theme":"Dark","reduced_motion":true,"future_setting":{"value":17},"title_language":"invalid"}""", Ct);
        var store = new UiPreferencesStore(dir.Path); var prefs = await store.ReadAsync(Ct);
        Assert.Equal("japanese", prefs.TitleLanguage); Assert.True(prefs.ShowJapaneseSubtitle); Assert.Equal("Dark", prefs.Theme); Assert.True(prefs.ReducedMotion);
        await store.SaveAsync(prefs with { TitleLanguage = "latin" }, Ct); var saved = await store.ReadAsync(Ct);
        Assert.Equal(17, saved.Extensions!["future_setting"].GetProperty("value").GetInt32()); Assert.Equal("latin", saved.TitleLanguage);
    }
    [Fact]
    public void LoadingLegacyDetailsReplacesPendingStateEvenWhenOptionalCatalogsStayUnknown()
    {
        var item = Item(Novel() with { Relations = null }); var book = new BookViewModel(item, item.Metadata, summaryOnly: true);
        var details = new BookDetailsViewModel(); details.Update(book, [], [], null); Assert.True(details.DetailsPending);
        Assert.Contains("尚未加载", details.ReleaseBrowser.Summary); Assert.Contains("尚未加载", details.Relations.Summary);
        book.ApplyDetails(item); details.Update(book, [], [], null);
        Assert.False(details.DetailsPending); Assert.Contains("尚未查询", details.ReleaseBrowser.Summary); Assert.Contains("旧快照", details.Relations.Summary);
    }
    private sealed class Library : IDesktopLibrary
    {
        public int LocalReads, Queries, PlayReads;
        public Func<string, Task<MetadataResponse>> Local { get; set; } = id => Task.FromResult(new MetadataResponse([Metadata(Novel(id))], "web_local", null, 1, 10, 1, false));
        public Task<ShelfResult> ShelfAsync(CancellationToken ct) => Task.FromResult(new ShelfResult(new("http://synthetic.test/", [Item(), M5Tests.Item(18)], [], [], DateTimeOffset.UtcNow), "synthetic"));
        public Task<MetadataResponse> LocalNovelAsync(string id, CancellationToken ct) { LocalReads++; return Local(id); }
        public Task<MetadataResponse> QueryAsync(string text, string source, CancellationToken ct) { Queries++; throw new InvalidOperationException("No upstream"); }
        public Task<IReadOnlyList<PersonalPlayView>> PlaysAsync(string id, CancellationToken ct) { PlayReads++; return Task.FromResult<IReadOnlyList<PersonalPlayView>>([]); }
    }
    private sealed class Dialogs : IDesktopDialogs
    {
        public Task<string?> ExecutableAsync() => Task.FromResult<string?>(null);
        public Task<string?> FolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveJsonAsync(string title) => Task.FromResult<string?>(null);
    }
    [Fact]
    public async Task RelatedNavigationDoesNotBorrowSameNovelPersonalItemOrLaunchDraft()
    {
        using var dir = new TestDirectory(); var library = new Library(); var vm = new MainViewModel(library, new(dir.Path), new Dialogs()); await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedBook = vm.Shelf.Books.Single(b => b.Metadata!.VndbId == "v17"); var parent = vm.SelectedBook;
        vm.Executable = "unsaved.exe"; vm.ArgumentsJson = "[\"unsaved\"]"; vm.Shelf.RememberPosition(vm.Shelf.ViewKey, 127);
        await vm.OpenRelatedCommand.ExecuteAsync(Assert.Single(vm.BookDetails.Relations.Rows));
        Assert.Same(parent, vm.SelectedBook); Assert.Same(parent, vm.Shelf.SelectedBook); Assert.Equal("v18", vm.BookDetails.Book!.Metadata!.VndbId);
        Assert.Null(vm.BookDetails.Book.Item); Assert.False(vm.BookDetails.IsPersonal); Assert.False(vm.BookDetails.LaunchConfigured); Assert.Empty(vm.BookDetails.Plays);
        Assert.Equal("unsaved.exe", vm.Executable); Assert.Equal("[\"unsaved\"]", vm.ArgumentsJson); Assert.Equal(127, vm.Shelf.Position(vm.Shelf.ViewKey));
        vm.BackRelatedCommand.Execute(null); Assert.Same(parent, vm.BookDetails.Book); Assert.False(vm.CanBackRelated); Assert.Equal(1, library.LocalReads); Assert.Equal(0, library.Queries); Assert.Equal(0, library.PlayReads);
    }
    [Fact]
    public async Task MissingRelatedOnlyFillsQueryAndLateResponseCannotOverwriteChangedSelection()
    {
        using var dir = new TestDirectory(); var library = new Library(); var vm = new MainViewModel(library, new(dir.Path), new Dialogs()); await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedBook = vm.Shelf.Books.Single(b => b.Metadata!.VndbId == "v17"); var parent = vm.SelectedBook;
        library.Local = _ => Task.FromResult(new MetadataResponse([], "web_local", null, 1, 10, 0, false));
        await vm.OpenRelatedCommand.ExecuteAsync(Assert.Single(vm.BookDetails.Relations.Rows)); Assert.Same(parent, vm.BookDetails.Book); Assert.True(vm.HasMissingRelated);
        vm.PrepareRelatedQueryCommand.Execute(null); Assert.Equal("v18", vm.Discovery.Query); Assert.Equal(2, vm.Section); Assert.Equal(0, library.Queries);
        vm.Section = 0; vm.SelectedBook = parent;
        var reply = new TaskCompletionSource<MetadataResponse>(TaskCreationOptions.RunContinuationsAsynchronously); library.Local = _ => reply.Task;
        var pending = vm.OpenRelatedCommand.ExecuteAsync(Assert.Single(vm.BookDetails.Relations.Rows));
        vm.SelectedBook = vm.Shelf.Books.Single(b => b.Metadata!.VndbId == "v18"); var next = vm.SelectedBook;
        reply.SetResult(new([Metadata(Novel("v18"))], "web_local", null, 1, 10, 1, false)); await pending;
        Assert.Same(next, vm.BookDetails.Book); Assert.False(vm.CanBackRelated); Assert.Equal(0, library.Queries);
    }
}
