using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.Services;
using Vndb.Desktop.ViewModels;
using Vndb.Library;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class K2BrowsingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static BookViewModel Book(int n, params string[] statuses)
    {
        var row = M5Tests.Item(n); var now = DateTimeOffset.UtcNow;
        return new(row, row.Metadata, statuses.Select((s, index) => new PersonalPlayView($"play-{n}-{index}",
            new(row.Item.Details.Title, row.Item.Details.NovelId, s, null, null, null, null, null, null, null, null), 1, now, now.AddMinutes(index), ItemId: row.Item.Id)).ToArray());
    }
    [Fact]
    public void AggregateExperienceFiltersMatchServerAndSurviveOwnershipChanges()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        var item = store.SaveItem(new(new("Synthetic")), null, "k2-item");
        foreach (var status in new[] { "playing", "completed" })
            store.SavePersonalPlay(new(new("Synthetic", null, status, null, null, null, null, null, null, null, null), item.Id), null, "k2-" + status);
        var summary = Assert.Single(store.ReadShelfSummary(status: "playing").Items);
        var book = new BookViewModel(summary, summary.Metadata, summaryOnly: true); var shelf = new ShelfBrowserViewModel(); shelf.SetLibrary([book]);
        shelf.ShelfState = "游玩中"; Assert.Same(book, Assert.Single(shelf.Books)); // Latest is completed; any exact experience matches.
        book.SetOwnership(false, []); book.SetResourceState(true, true, false, true); shelf.Refresh();
        Assert.Same(book, Assert.Single(shelf.Books)); Assert.True(book.HasPlayed); Assert.True(book.HasLocalProblem);
        Assert.False(book.CanQuickLaunch); Assert.Contains("缺失", book.ResourceLabel);
        shelf.ShelfState = "未游玩"; Assert.Empty(shelf.Books);
    }
    [Theory]
    [InlineData("本机已安装", "全部状态", "本机已安装", false)]
    [InlineData("仅有安装包", "全部状态", "仅有安装包", false)]
    [InlineData("缺失／待核对", "全部状态", "缺失／待核对", false)]
    [InlineData("本机已有且未玩", "未游玩", "全部本机资源", true)]
    [InlineData("游玩中", "游玩中", "全部本机资源", false)]
    [InlineData("未记录", "未记录", "全部本机资源", false)]
    public void QuickViewsAreExactlyManualPredicates(string quick, string status, string resource, bool local)
    {
        var installed = Book(1, "playing"); installed.SetOwnership(true, []); installed.SetResourceState(true, false, false, true);
        var package = Book(2, "not_started"); package.SetOwnership(false, ["synthetic.zip"]);
        var missing = Book(3, "completed"); missing.SetResourceState(true, true, false, true);
        var unknown = Book(4); unknown.SetResourceState(true, false, true, false);
        var empty = Book(5); var books = new[] { installed, package, missing, unknown, empty };
        var shortcut = new ShelfBrowserViewModel(); shortcut.SetLibrary(books); shortcut.QuickView = quick;
        var manual = new ShelfBrowserViewModel(); manual.SetLibrary(books); manual.ShelfState = status; manual.ResourceFilter = resource; manual.LocalOnly = local;
        Assert.Equal(manual.Books.Select(b => b.Id), shortcut.Books.Select(b => b.Id)); Assert.Equal(quick, shortcut.QuickView);
        if (quick == "本机已有且未玩") Assert.Equal(package.Id, Assert.Single(shortcut.Books).Id);
    }
    [Fact]
    public void FiltersDoNotSilentlyWidenAfterRefreshAndPositionsRemainPerViewPageAndLayout()
    {
        var shelf = new ShelfBrowserViewModel(); shelf.SetLibrary([Book(1)]); shelf.CompanyFilter = "Missing studio";
        shelf.SetLibrary([Book(2)]); Assert.Equal("Missing studio", shelf.CompanyFilter); Assert.Empty(shelf.Books);
        Assert.Single(shelf.Conditions); shelf.RemoveFilterCommand.Execute("company"); Assert.Single(shelf.Books);
        var home = shelf.ViewKey; shelf.RememberPosition(home, 321); shelf.Activate(2); var query = shelf.ViewKey; shelf.RememberPosition(query, 99);
        shelf.ListMode = true; Assert.Equal(0, shelf.Position(shelf.ViewKey)); shelf.Activate(0);
        Assert.Equal(home, shelf.ViewKey); Assert.Equal(321, shelf.Position(home)); Assert.Equal(99, shelf.Position(query));
        shelf.Clear(); Assert.Equal(0, shelf.Position(home));
    }
    [Fact]
    public void WebReportedPresenceIsNotALocalInstallationOrUnplayedClaim()
    {
        var row = M5Tests.Item() with { State = new(PersonalProgress.FromStatuses([]), true) };
        var book = new BookViewModel(row, row.Metadata, []);
        Assert.Contains("已有", book.ReportedPresence); Assert.Contains("未登记", book.ResourceLabel);
        Assert.False(book.HasInstallation); Assert.False(book.IsUnplayed); Assert.True(book.IsUnrecorded);
        book.SetOwnership(false, ["synthetic.zip"]); Assert.Contains("仅有安装包", book.ResourceLabel); Assert.False(book.CanQuickLaunch);
        book.SetOwnership(true, []); book.SetResourceState(true, false, false, true);
        book.SetObservation([new("install", "root", "synthetic", null, [], "present", "entry_missing", "entry_missing")]);
        Assert.True(book.IsLocal); Assert.False(book.CanQuickLaunch); Assert.Contains("待核对", book.LaunchLabel);
    }
    [Fact]
    public async Task HistoryIsScopedBoundedClearableAndDoesNotOverwriteCorruptOriginals()
    {
        using var dir = new TestDirectory();
        var scope = LocalQueryScope.Resolve(dir.Path, new("https://server-a.test/"), environment: _ => null);
        var other = LocalQueryScope.Resolve(dir.Path, new("https://server-b.test/"), environment: _ => null);
        var history = new SearchHistoryStore(scope, "lookup");
        for (var n = 0; n < 25; n++) await history.RecordAsync("Query " + n, Ct);
        Assert.Equal(20, (await history.ReadAsync(Ct)).Count);
        Assert.Equal("query 5", (await history.RecordAsync(" query 5 ", Ct))[0]);
        Assert.Empty(await new SearchHistoryStore(other, "lookup").ReadAsync(Ct));
        Assert.Empty(await new SearchHistoryStore(scope, "catalog").ReadAsync(Ct));
        await history.RemoveAsync("query 5", Ct); Assert.Equal(19, (await history.ReadAsync(Ct)).Count);
        await history.RemoveAsync(null, Ct); Assert.Empty(await history.ReadAsync(Ct));
        var path = Path.Combine(scope.DirectoryPath, "search-history-lookup.json"); await File.WriteAllTextAsync(path, "{synthetic-broken", Ct);
        await Assert.ThrowsAsync<JsonException>(() => history.RecordAsync("New", Ct)); Assert.Equal("{synthetic-broken", await File.ReadAllTextAsync(path, Ct));
    }
    private sealed class Library : IDesktopLibrary
    {
        public int QueryCalls, CatalogCalls, ProducerCalls;
        public TaskCompletionSource<MetadataResponse> QueryReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<CatalogProducerChoice>> ProducerReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ShelfResult> ShelfAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<MetadataResponse> QueryAsync(string text, string source, CancellationToken ct) { QueryCalls++; return QueryReply.Task; }
        public Task<IReadOnlyList<PersonalPlayView>> PlaysAsync(string item, CancellationToken ct) => Task.FromResult<IReadOnlyList<PersonalPlayView>>([]);
        public Task<IReadOnlyList<CatalogProducerChoice>> CatalogProducersAsync(string text, bool cacheOnly, CancellationToken ct) { ProducerCalls++; return ProducerReply.Task; }
        public Task<PublicCatalogPage> BrowseCatalogAsync(PublicCatalogQuery q, bool cacheOnly, CancellationToken ct) { CatalogCalls++; throw new NotSupportedException(); }
    }
    private sealed class Dialogs : IDesktopDialogs
    {
        public Task<string?> ExecutableAsync() => Task.FromResult<string?>(null);
        public Task<string?> FolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveJsonAsync(string title) => Task.FromResult<string?>(null);
    }
    [Fact]
    public async Task EmptyQueryDoesNotReachTransportOrHistory()
    {
        var library = new Library(); var queries = new QueryFeatureViewModel(library, new Dialogs(), work => work(Ct), _ => { });
        await Assert.ThrowsAsync<ArgumentException>(() => queries.SearchCommand.ExecuteAsync(null));
        Assert.Empty(queries.History.Entries); Assert.Equal(0, library.QueryCalls);
    }
    [Fact]
    public async Task ScopeInvalidationDuringHistoryWriteDoesNotBeginTransportInNewScope()
    {
        using var dir = new TestDirectory(); var scope = LocalQueryScope.Resolve(dir.Path, new("https://server.test/"), environment: _ => null);
        var library = new Library(); QueryFeatureViewModel? queries = null; var reads = 0;
        queries = new(library, new Dialogs(), work => work(Ct), _ => { }, () => { if (++reads == 2) queries!.Clear(); return scope; });
        queries.Query = "Synthetic"; await queries.SearchCommand.ExecuteAsync(null);
        Assert.Equal(0, library.QueryCalls); Assert.Null(queries.Result);
    }
    [Fact]
    public async Task HistoryRecallOnlyFillsDraftAndLateLookupCannotRestoreClearedScope()
    {
        using var dir = new TestDirectory(); var scope = LocalQueryScope.Resolve(dir.Path, new("https://server.test/"), environment: _ => null);
        var library = new Library(); var queries = new QueryFeatureViewModel(library, new Dialogs(), work => work(Ct), _ => { }, () => scope);
        queries.Query = "Submitted"; var pending = queries.SearchCommand.ExecuteAsync(null);
        while (library.QueryCalls == 0) { Ct.ThrowIfCancellationRequested(); await Task.Delay(1, Ct); }
        Assert.Equal("Submitted", Assert.Single(queries.History.Entries));
        queries.History.Selected = "Submitted"; queries.Query = "Other draft"; queries.History.RecallCommand.Execute(null);
        Assert.Equal("Submitted", queries.Query); Assert.Equal(1, library.QueryCalls);
        queries.Clear(); library.QueryReply.SetResult(new([], "synthetic", null, 1, 20, 0, false)); await pending;
        Assert.Null(queries.Result); Assert.Equal(1, library.QueryCalls);
    }
    [Fact]
    public async Task ProducerSelectionIsExplicitReadOnlyAndOldScopeReplyIsDiscarded()
    {
        var library = new Library(); var catalog = new PublicCatalogViewModel(library, work => work(Ct), _ => { }, _ => { });
        catalog.ProducerQuery = "Synthetic studio"; Assert.Equal(0, library.ProducerCalls);
        var pending = catalog.FindProducersCommand.ExecuteAsync(null); catalog.Clear();
        library.ProducerReply.SetResult([new("p1", "Synthetic", "合成会社", 2)]); await pending;
        Assert.Empty(catalog.Producers); Assert.Equal(0, library.QueryCalls); Assert.Equal(0, library.CatalogCalls);
        catalog.Producer = new("p1", "Synthetic", "合成会社", 2); Assert.Equal("p1", catalog.ProducerId);
        Assert.Equal(0, library.CatalogCalls); catalog.ResetFiltersCommand.Execute(null); Assert.Empty(catalog.ProducerId);
    }
    [Fact]
    public async Task ProducerOptionsCachePreservesScopeAndRejectsMalformedReplacement()
    {
        using var dir = new TestDirectory(); var invalid = false; var reads = 0;
        var source = new PublicCatalogReader(dir.Path, "https://server.test/", null, (_, _) =>
        { reads++; return Task.FromResult<IReadOnlyList<CatalogProducerChoice>>(invalid ? [new("v17", "Invalid", null, 0)] : [new("p1", "Synthetic", "合成会社", 2)]); });
        Assert.Single(await source.ProducersAsync("合成", false, Ct)); invalid = true;
        await Assert.ThrowsAsync<ArgumentException>(() => source.ProducersAsync("合成", false, Ct));
        var offline = new PublicCatalogReader(dir.Path, "https://server.test/", null);
        Assert.Equal("p1", Assert.Single(await offline.ProducersAsync("合成", true, Ct)).Id); Assert.Equal(2, reads);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PublicCatalogReader(dir.Path, "https://other.test/", null).ProducersAsync("合成", true, Ct));
    }
}
