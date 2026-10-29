using Vndb.Contracts;
using Vndb.Desktop.ViewModels;
using Xunit;

namespace Vndb.Tests;

public sealed class ShelfBrowserTests
{
    [Fact]
    public void QueryPreservesServerPriorityAndPersonalShelfCanApplyTierOrderBeforePaging()
    {
        var played = Book(99, "completed"); var owned = Book(88, "not_started"); owned.SetOwnership(false, ["game.zip"]);
        var rest = Enumerable.Range(1, 70).Select(n => Book(n, "not_started")).ToArray();
        var browser = new ShelfBrowserViewModel();
        browser.Activate(2); browser.SetQuery([played, owned, rest[0]], "Web personal order");
        Assert.Equal("原始结果顺序", browser.ShelfSort); Assert.Same(played, browser.Books[0]); Assert.Same(owned, browser.Books[1]);
        browser.Activate(0); browser.SetLibrary([..rest, owned, played]); browser.ShelfSort = "个人优先";
        Assert.Same(played, browser.Books[0]); Assert.Same(owned, browser.Books[1]); Assert.Equal(60, browser.Books.Count);
    }
    private static BookViewModel Book(int n, string status = "not_started", string? vn = null)
    {
        var item = M5Tests.Item(n);
        item = item with { Item = item.Item with { Details = item.Item.Details with { TitleOverride = $"合成 {n:000}", NovelId = vn ?? item.Item.Details.NovelId } } };
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        PersonalPlayView play = new("play-" + n, new(item.Item.Details.Title, item.Item.Details.NovelId, status, null, null, null, null, null, null, null, null), 1, now, now, ItemId: item.Item.Id);
        return new(item, item.Metadata, [play]);
    }
    [Fact]
    public void ShelfPlayedAndQueryKeepIndependentFiltersSelectionAndLayout()
    {
        var a = Book(1, "completed"); var b = Book(2); var browser = new ShelfBrowserViewModel();
        browser.SetLibrary([a, b], "library"); browser.Filter = "001"; browser.ListMode = true; browser.SelectedBook = a;
        browser.ShelfSort = "标题"; browser.SortDescending = false;
        browser.Activate(1); Assert.Empty(browser.Filter); Assert.False(browser.ListMode); Assert.Same(a, Assert.Single(browser.Books));
        browser.Filter = "合成"; browser.SelectedBook = a;
        browser.SetQuery([Book(9)], "query"); browser.Activate(2);
        Assert.Empty(browser.Filter); Assert.False(browser.FiltersExpanded); Assert.Null(browser.SelectedBook); Assert.Equal("query", browser.Source);
        browser.Filter = "absent"; Assert.Empty(browser.Books);
        browser.SetQuery([Book(10)], "new query"); Assert.Empty(browser.Filter); Assert.Single(browser.Books);
        browser.Activate(0); Assert.Equal("001", browser.Filter); Assert.True(browser.ListMode); Assert.Same(a, browser.SelectedBook);
        Assert.Equal("标题", browser.ShelfSort); Assert.False(browser.SortDescending); Assert.Equal("library", browser.Source);
        browser.Activate(1); Assert.Equal("合成", browser.Filter); Assert.Same(a, browser.SelectedBook);
    }
    [Fact]
    public void SelectionTracksItemIdentityAcrossSortResizeAndViewSwitch()
    {
        var a = Book(1, vn: "v17"); var b = Book(2, vn: "v17"); var browser = new ShelfBrowserViewModel();
        browser.SetLibrary([a, b]); browser.SelectCommand.Execute(b);
        browser.ShelfSort = "标题"; browser.SortDescending = false; browser.SetColumns(1); browser.ToggleViewCommand.Execute(null);
        Assert.Same(b, browser.SelectedBook); Assert.True(b.Selected); Assert.False(a.Selected);
        Assert.Equal(2, browser.Books.Count); Assert.Equal(2, browser.Rows.Count);
        browser.ShowDetailsCommand.Execute(new BookViewModel(a.Item, a.Metadata));
        Assert.False(browser.DetailsOpen); Assert.Same(b, browser.SelectedBook); // An obsolete template cannot open another item.
    }
    [Fact]
    public void KeyboardSelectionCrossesPagesAndPageCommandsRespectBounds()
    {
        var browser = new ShelfBrowserViewModel(); browser.SetLibrary(Enumerable.Range(1, 125).Select(n => Book(n)).ToArray());
        browser.ShelfSort = "标题"; browser.SortDescending = false;
        Assert.False(browser.PreviousCommand.CanExecute(null)); Assert.True(browser.NextCommand.CanExecute(null));
        var last = browser.Books[59]; browser.SelectCommand.Execute(last); browser.MoveSelection(1);
        Assert.Equal(2, browser.Page); Assert.Equal("合成 061", browser.SelectedBook!.Title);
        browser.NextCommand.Execute(null); Assert.Equal(3, browser.Page); Assert.Equal(5, browser.Books.Count); Assert.Null(browser.SelectedBook);
        Assert.False(browser.NextCommand.CanExecute(null)); browser.PreviousCommand.Execute(null); Assert.Equal(2, browser.Page);
        browser.Activate(2); browser.Activate(0); Assert.Equal(2, browser.Page);
    }
    [Fact]
    public void RefreshPreservesSelectionByIdAndRemovalClosesDetailsWithoutBorrowingAnEdition()
    {
        var browser = new ShelfBrowserViewModel(); var a = Book(1); browser.SetLibrary([a]); browser.SelectedBook = a; browser.OpenDetailsCommand.Execute(null);
        var refreshed = Book(1); browser.SetLibrary([refreshed]); Assert.Same(refreshed, browser.SelectedBook); Assert.True(browser.DetailsOpen);
        browser.SetLibrary([Book(2)]); Assert.Null(browser.SelectedBook); Assert.False(browser.DetailsOpen); Assert.False(browser.OpenDetailsCommand.CanExecute(null));
    }
    [Fact]
    public void OldHomePreferencesAreCompatibleAndNeverNarrowQueryOrPlayedPages()
    {
        var browser = new ShelfBrowserViewModel(); browser.ApplyHomePreferences(new("标题", false, "Second", "已通关", false, true));
        browser.SetLibrary([Book(1, "completed"), Book(2)]);
        Assert.Single(browser.Books); Assert.Equal("本次完成", browser.ShelfState); Assert.Equal("Second", browser.CompanyFilter);
        browser.Activate(1); Assert.Equal("全部会社", browser.CompanyFilter); Assert.Equal("全部状态", browser.ShelfState);
        browser.SetQuery([Book(3)], "cache"); browser.Activate(2); Assert.Single(browser.Books); Assert.False(browser.ListMode);
        browser.Clear(); Assert.Empty(browser.Books); Assert.Null(browser.SelectedBook); Assert.Empty(browser.Filter);
        browser.Activate(0); Assert.False(browser.ListMode); Assert.Equal("全部状态", browser.ShelfState); Assert.Equal("尚未加载", browser.Source);
    }
    [Fact]
    public async Task SavingHomePreferencesDoesNotPersistQueryFiltersOrSelection()
    {
        Vndb.Desktop.Services.ShelfPreferences? saved = null;
        var browser = new ShelfBrowserViewModel(p => { saved = p; return Task.CompletedTask; });
        browser.SetLibrary([Book(1)]); browser.Filter = "001"; browser.ListMode = true; browser.SelectedBook = browser.Books[0];
        await browser.SavePreferencesCommand.ExecuteAsync(null); Assert.True(saved!.ListMode); Assert.Equal("全部状态", saved.State);
        browser.Activate(2); Assert.False(browser.SavePreferencesCommand.CanExecute(null));
    }
    [Fact]
    public void MovingWithinAPagePreservesTheRealizedRowModels()
    {
        var browser = new ShelfBrowserViewModel(); browser.SetLibrary(Enumerable.Range(1, 100).Select(n => Book(n)).ToArray());
        var row = browser.Rows[0]; browser.SelectedBook = browser.Books[0];
        browser.MoveSelection(1); browser.MoveSelection(1);
        Assert.Same(row, browser.Rows[0]); Assert.Same(browser.Books[2], browser.SelectedBook);
    }
    [Fact]
    public async Task SlowDetailResponseNeverReplacesTheNewlySelectedBook()
    {
        using var directory = new TestDirectory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<PersonalPlayView>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var library = new Library([Book(1).Item!, Book(2).Item!], async () => { started.SetResult(); return await release.Task; });
        var vm = new MainViewModel(library, new(directory.Path), new Dialogs()); await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedBook = vm.Shelf.Books[0]; var pending = vm.DetailCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var next = vm.Shelf.Books[1]; vm.SelectedBook = next;
        release.SetResult([]); await pending;
        Assert.Same(next, vm.SelectedBook); Assert.Same(next, vm.Shelf.SelectedBook); Assert.Same(next, vm.BookDetails.Book);
    }
    private sealed class Library(LibraryItemView[] items, Func<Task<IReadOnlyList<PersonalPlayView>>> read) : Vndb.Desktop.Services.IDesktopLibrary
    {
        public Task<Vndb.Desktop.Services.ShelfResult> ShelfAsync(CancellationToken ct) => Task.FromResult(new Vndb.Desktop.Services.ShelfResult(new("http://test.local/", items, [], [], DateTimeOffset.UtcNow), "synthetic"));
        public Task<MetadataResponse> QueryAsync(string text, string source, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PersonalPlayView>> PlaysAsync(string itemId, CancellationToken ct) => read();
    }
    private sealed class Dialogs : Vndb.Desktop.Services.IDesktopDialogs
    {
        public Task<string?> ExecutableAsync() => Task.FromResult<string?>(null);
        public Task<string?> FolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveJsonAsync(string name) => Task.FromResult<string?>(null);
    }
}
