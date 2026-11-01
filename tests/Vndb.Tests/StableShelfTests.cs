using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.Services;
using Vndb.Desktop.ViewModels;
using Vndb.Library;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class StableShelfTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Origin = "http://library.test/";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = StableShelfTests.Now; public override DateTimeOffset GetUtcNow() => Now; }
    private static LibraryItemView Item(string id, string? vn = null, int count = 1, bool removed = false) =>
        new(new(id, new("同名作品", vn), 1, Now, Now, removed ? Now : null), null, count);
    private static PersonalPlayView Play(string id, string? item = null, string? vn = null, string status = "played", long revision = 1) =>
        new(id, StablePlayProjectionTests.Details(vn, status), revision, Now, Now, ItemId: item);
    private static ShelfSnapshot Snapshot(LibraryItemView[] items, PersonalPlayView[] plays, LibraryItemView[]? removed = null) => new(Origin, items, plays, [], Now, removed);

    [Fact]
    public void SameTitlesAndSameNovelEditionsKeepEveryReplayAndStateOnTheActualItem()
    {
        var snapshot = Snapshot([Item("a", "v17", 2), Item("b", "v17"), Item("c"), Item("d")],
            [Play("a1", "a", "v17"), Play("a2", "a", "v17", "completed"), Play("b1", "b", "v17", "not_started"), Play("c1", "c"), Play("d1", "d", status: "on_hold")], []);
        var rows = ShelfPlayResolver.Resolve(snapshot).ToDictionary(i => i.Item.Item.Id);
        Assert.Equal(["a1", "a2"], rows["a"].Plays.Select(p => p.Id)); Assert.Equal("b1", Assert.Single(rows["b"].Plays).Id);
        Assert.Equal("c1", Assert.Single(rows["c"].Plays).Id); Assert.Equal("d1", Assert.Single(rows["d"].Plays).Id);
        Assert.All(rows.Values, r => Assert.False(r.NeedsReview));
        Assert.False(new BookViewModel(rows["b"].Item, null, rows["b"].Plays).HasPlayed);
    }
    [Theory]
    [InlineData("not_started", false)]
    [InlineData("unknown", false)]
    [InlineData("playing", true)]
    [InlineData("played", true)]
    [InlineData("completed", true)]
    [InlineData("on_hold", true)]
    [InlineData("dropped", true)]
    public void PlayedUsesKnownStateRatherThanCountAndUnknownNeverBecomesUnplayed(string status, bool played)
    {
        var book = new BookViewModel(Item("a", count: 99), null, [Play("p", "a", status: status)]);
        Assert.Equal(played, book.HasPlayed);
        if (status == "unknown") { Assert.True(book.NeedsPlayReview); Assert.False(book.IsUnplayed); Assert.Equal("关联待核对", book.ShelfState); }
        var countOnly = new BookViewModel(Item("a", count: 1), null); Assert.False(countOnly.HasPlayed); Assert.True(countOnly.NeedsPlayReview);
        var removed = Play("p", "a") with { DeletedAt = Now }; Assert.False(new BookViewModel(Item("a"), null, [removed]).HasPlayed);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyAssociationRequiresCompleteUniqueNovelOrDeterministicItemNotTitle(bool complete)
    {
        var rows = ShelfPlayResolver.Resolve(Snapshot([Item("a", "v17"), Item("b")], [Play("a1", vn: "v17"), Play("b1")], complete ? [] : null));
        Assert.Equal(complete ? 1 : 0, rows[0].Plays.Count); Assert.Equal(!complete, rows[0].NeedsReview);
        Assert.Empty(rows[1].Plays); Assert.True(rows[1].NeedsReview); // Even a unique title alone is not proof.
        var seeded = ShelfPlayResolver.Resolve(Snapshot([Item("item-p")], [Play("p")])); Assert.Single(Assert.Single(seeded).Plays);
        var withRemoved = ShelfPlayResolver.Resolve(Snapshot([Item("a", "v17")], [Play("p", vn: "v17")], [Item("removed", "v17", removed: true)]));
        Assert.Empty(Assert.Single(withRemoved).Plays); Assert.True(Assert.Single(withRemoved).NeedsReview);
    }
    [Fact]
    public void ExplicitRemovedOrMismatchedItemNeverFallsBackByNovelOrTitle()
    {
        var rows = ShelfPlayResolver.Resolve(Snapshot([Item("a", "v17", 0)], [Play("p", "removed", "v17")], [Item("removed", "v17", removed: true)]));
        Assert.Empty(Assert.Single(rows).Plays); Assert.False(Assert.Single(rows).NeedsReview);
        var mismatch = ShelfPlayResolver.Resolve(Snapshot([Item("a", "v18")], [Play("p", "a", "v17")], []));
        Assert.Empty(Assert.Single(mismatch).Plays); Assert.True(Assert.Single(mismatch).NeedsReview);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactItemReadSurvivesRestartAndOverridesOlderShelfWithoutRewritingIt(bool empty)
    {
        using var dir = new TestDirectory(); var clock = new Clock(); var local = new LocalLibraryStore(dir.Path);
        var old = Play("p", "a", "v17", "playing"); var current = Play("p", "a", "v17", "completed", 2);
        var remote = new LibraryReadOperations((_, _) => Task.FromResult(new ItemPage([Item("a", "v17")], 1, 100, 1, false)),
            (id, _) => Task.FromResult<IReadOnlyList<PersonalPlayView>>(id is null ? [old] : empty ? [] : [current]),
            (_, _) => Task.FromResult(new ItemPage([], 1, 100, 0, false)));
        var online = new LocalShelfReader(local, dir.Path, Origin, remote, clock); await online.ShelfAsync(Ct);
        var shelfFile = Path.Combine(dir.Path, "shelf.json"); var bytes = await File.ReadAllBytesAsync(shelfFile, Ct);
        clock.Now = Now.AddMinutes(1); var exact = await online.PlaysAsync("a", Ct); Assert.Equal(empty ? 0 : 1, exact.Count);
        var offline = new LocalShelfReader(new(dir.Path), dir.Path, Origin, time: clock); var restored = await offline.ShelfAsync(Ct);
        var row = Assert.Single(ShelfPlayResolver.Resolve(restored.Snapshot)); Assert.False(row.NeedsReview); Assert.Equal(empty ? 0 : 1, row.Plays.Count);
        if (!empty) Assert.Equal("completed", row.Plays[0].Details.Status);
        Assert.Equal(row.Plays.Select(p => p.Id), (await offline.PlaysAsync("a", Ct)).Select(p => p.Id));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(shelfFile, Ct));
        clock.Now = Now.AddMinutes(2); await online.ShelfAsync(Ct);
        Assert.Equal("playing", Assert.Single(await offline.PlaysAsync("a", Ct)).Details.Status); // A newer full refresh wins.
    }
    [Fact]
    public async Task LegacyItemEndpointAndArrayCacheGainOnlyInMemoryAssociationAndKeepOriginalBytes()
    {
        using var dir = new TestDirectory(); var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("a")));
        var file = Path.Combine(dir.Path, "item-plays", hash + ".json"); await JsonSnapshotStore.AtomicWriteAsync(file, new[] { Play("p") }, false, Ct);
        var bytes = File.ReadAllBytes(file); var reader = new LocalShelfReader(new(dir.Path), dir.Path, Origin);
        Assert.Equal("a", Assert.Single(await reader.PlaysAsync("a", Ct)).ItemId); Assert.Equal(bytes, File.ReadAllBytes(file));
        var legacy = new LibraryReadOperations((_, _) => throw new InvalidOperationException(), (_, _) => Task.FromResult<IReadOnlyList<PersonalPlayView>>([Play("p")]));
        Assert.Equal("a", Assert.Single(await new LocalShelfReader(new(dir.Path), dir.Path, Origin, legacy).PlaysAsync("a", Ct)).ItemId);
        Assert.Equal(bytes, File.ReadAllBytes(file));
    }
    [Fact]
    public async Task WrongItemResponseDoesNotCreateAnAcceptedCache()
    {
        using var dir = new TestDirectory(); var remote = new LibraryReadOperations((_, _) => throw new InvalidOperationException(), (_, _) => Task.FromResult<IReadOnlyList<PersonalPlayView>>([Play("p", "other")]));
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalShelfReader(new(dir.Path), dir.Path, Origin, remote).PlaysAsync("a", Ct));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "item-plays")));
    }
    [Fact]
    public async Task SlowItemReadDoesNotHoldTheFileLeaseOrOverwriteANewerClientSnapshot()
    {
        using var dir = new TestDirectory(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new LibraryReadOperations((_, _) => throw new InvalidOperationException(), async (_, ct) =>
        { started.SetResult(); await release.Task.WaitAsync(ct); return [Play("p", "a", revision: 1)]; });
        var fast = new LibraryReadOperations((_, _) => throw new InvalidOperationException(), (_, _) => Task.FromResult<IReadOnlyList<PersonalPlayView>>([Play("p", "a", revision: 2)]));
        var pending = new LocalShelfReader(new(dir.Path), dir.Path, Origin, slow).PlaysAsync("a", Ct);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await new LocalShelfReader(new(dir.Path), dir.Path, Origin, fast).PlaysAsync("a", Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<InvalidDataException>(() => pending);
        Assert.Equal(2, Assert.Single(await new LocalShelfReader(new(dir.Path), dir.Path, Origin).PlaysAsync("a", Ct)).Revision);
    }
    private sealed class Factory(string path) : WebApplicationFactory<Program>
    { protected override void ConfigureWebHost(IWebHostBuilder b) => b.UseSetting("Library:DataDirectory", path).UseSetting("Library:WriteKey", "shelf-test-key").UseSetting("CatalogJobs:Enabled", "false"); }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ItemCacheOverlaysFollowReadOrderAndEmptyOldParentDoesNotEraseRelinkedPlay(bool reversed, bool sameClock)
    {
        using var dir = new TestDirectory(); var clock = new Clock(); var afterMove = false;
        var items = new[] { Item("a", "v17"), Item("b", "v17", 0) }; if (reversed) Array.Reverse(items);
        var remote = new LibraryReadOperations((_, _) => Task.FromResult(new ItemPage(items, 1, 100, 2, false)),
            (id, _) => Task.FromResult<IReadOnlyList<PersonalPlayView>>(id is null || id == "a" && !afterMove ? [Play("p", "a", "v17")] : id == "b" ? [Play("p", "b", "v17", revision: 2)] : []),
            (_, _) => Task.FromResult(new ItemPage([], 1, 100, 0, false)));
        var online = new LocalShelfReader(new(dir.Path), dir.Path, Origin, remote, clock); await online.ShelfAsync(Ct);
        clock.Now = sameClock ? Now : Now.AddMinutes(1); await online.PlaysAsync("a", Ct); afterMove = true;
        clock.Now = sameClock ? Now : Now.AddMinutes(2); await online.PlaysAsync("b", Ct);
        var offline = new LocalShelfReader(new(dir.Path), dir.Path, Origin, time: clock);
        var first = ShelfPlayResolver.Resolve((await offline.ShelfAsync(Ct)).Snapshot);
        Assert.Empty(Assert.Single(first, i => i.Item.Item.Id == "a").Plays); Assert.Equal("b", Assert.Single(Assert.Single(first, i => i.Item.Item.Id == "b").Plays).ItemId);
        clock.Now = Now.AddMinutes(3); await online.PlaysAsync("a", Ct);
        var second = ShelfPlayResolver.Resolve((await offline.ShelfAsync(Ct)).Snapshot);
        Assert.Empty(Assert.Single(second, i => i.Item.Item.Id == "a").Plays); Assert.False(Assert.Single(second, i => i.Item.Item.Id == "a").NeedsReview);
        Assert.Equal("p", Assert.Single(Assert.Single(second, i => i.Item.Item.Id == "b").Plays).Id);
    }
    [Fact]
    public async Task NewerCorrectedShelfCannotFallBackToAnOldItemCacheAndCorruptCachesStayIntact()
    {
        using var dir = new TestDirectory(); var clock = new Clock();
        var remote = new LibraryReadOperations((_, _) => throw new InvalidOperationException(), (_, _) => Task.FromResult<IReadOnlyList<PersonalPlayView>>([Play("p", "a", "v17")]));
        var online = new LocalShelfReader(new(dir.Path), dir.Path, Origin, remote, clock); await online.PlaysAsync("a", Ct);
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(dir.Path, "shelf.json"), Snapshot([Item("a", "v18")], []) with { FetchedAt = Now.AddMinutes(1) }, false, Ct);
        await Assert.ThrowsAsync<CatalogUnavailableException>(() => new LocalShelfReader(new(dir.Path), dir.Path, Origin).PlaysAsync("a", Ct));
        var cache = Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "item-plays"), "*.v2.json")); await File.WriteAllTextAsync(cache, "{broken", Ct);
        await Assert.ThrowsAsync<JsonException>(() => online.PlaysAsync("a", Ct)); Assert.Equal("{broken", File.ReadAllText(cache));
    }
    private sealed class Dialogs : IDesktopDialogs
    {
        public Task<string?> ExecutableAsync() => Task.FromResult<string?>(null);
        public Task<string?> FolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveJsonAsync(string name) => Task.FromResult<string?>(null);
    }
    [Fact]
    public async Task DesktopCardsDetailEditorAndSessionSelectTheSameStableReplay()
    {
        using var db = new TestDirectory(); using var client = new TestDirectory(); await using var app = new Factory(db.Path); using var http = app.CreateClient();
        var store = app.Services.GetRequiredService<SqliteLibraryStore>(); store.Save(StorageTests.Entry());
        var a = store.SaveItem(new(new("同名作品", "v17")), null, "a"); var b = store.SaveItem(new(new("同名作品", "v17")), null, "b");
        var a1 = store.SavePersonalPlay(new(StablePlayProjectionTests.Details("v17", minutes: 10), a.Id), null, "a1");
        var a2 = store.SavePersonalPlay(new(StablePlayProjectionTests.Details("v17", minutes: 20), a.Id), null, "a2");
        var b1 = store.SavePersonalPlay(new(StablePlayProjectionTests.Details("v17", "not_started"), b.Id), null, "b1");
        var installation = new LocalInstallationView(new("synthetic-install", new("synthetic-device", @"E:\Synthetic\StableItemA", "Synthetic"), "present", Now, 1, Now, Now),
            [new("synthetic-component", "synthetic-install", new(a.Id, "."), 1, Now, Now)]);
        await new LocalLibraryStore(client.Path).SaveAsync(new([installation], [store.ReadItem(a.Id)!, store.ReadItem(b.Id)!]), Ct);
        using var services = new DesktopServices(client.Path, http); await services.InitializeAsync(Ct); await services.ConfigureAsync(new(http.BaseAddress!.AbsoluteUri), "shelf-test-key", false, Ct);
        var vm = new MainViewModel(services.Library, services.Launcher, new Dialogs(), services); await vm.RefreshCommand.ExecuteAsync(null);
        var book = Assert.Single(vm.Shelf.Books, i => i.Id == a.Id); Assert.Empty(book.Plays); Assert.Equal(2, book.Item!.PlayCount); Assert.True(book.HasPlayed); Assert.False(Assert.Single(vm.Shelf.Books, i => i.Id == b.Id).HasPlayed);
        Assert.True(book.IsLocal); Assert.False(Assert.Single(vm.Shelf.Books, i => i.Id == b.Id).IsLocal);
        vm.SelectedBook = book; Assert.Empty(vm.SessionPlays); await vm.DetailCommand.ExecuteAsync(null); Assert.Equal(2, book.Plays.Count); Assert.Equal(2, vm.SessionPlays.Count);
        await vm.EditBookPlayCommand.ExecuteAsync(book); var editor = Assert.IsType<QuickPlayEditorViewModel>(vm.QuickEditor); Assert.Equal(2, editor.Plays.Count); Assert.False(editor.HasSelection);
        editor.SelectedPlay = Assert.Single(editor.Plays, p => p.Play.Id == a2.Id); editor.DurationKnown = true; editor.Hours = 1; editor.Minutes = 30;
        await editor.SaveCommand.ExecuteAsync(null); Assert.True(editor.Applied); Assert.Equal(90, store.GetPlay(a2.Id)!.Details.DurationMinutes);
        Assert.Equal(10, store.GetPlay(a1.Id)!.Details.DurationMinutes); Assert.Null(store.GetPlay(b1.Id)!.Details.DurationMinutes);
        Assert.Equal(a.Id, vm.SelectedBook!.Id); Assert.Equal(new[] { a1.Id, a2.Id }.Order(), vm.SessionPlays.Select(p => p.Id).Order());
        vm.Section = 1; Assert.Single(vm.Shelf.Books); Assert.Equal(a.Id, vm.Shelf.Books[0].Id); Assert.Empty(store.Installations());
    }
    [Fact]
    public async Task LocalActionsRejectAnotherItemsPlayEvenWhenTheNovelMatches()
    {
        using var dir = new TestDirectory(); var outbox = new ActivityOutbox(dir.Path); var identity = new ClientDeviceIdentity(Guid.NewGuid().ToString("D"), "Synthetic", "windows", Now);
        await Assert.ThrowsAsync<ArgumentException>(() => outbox.CreateTargetAsync(identity, "Synthetic", null, "a", Play("p", "b"), Ct));
        Assert.Empty(await outbox.TargetsAsync(Ct));
        var edits = new PersonalEditOutbox(dir.Path); var quick = new QuickPlayService(edits, () => Origin, () => null, (_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAsync<ArgumentException>(() => quick.QueueAsync(new("a", "Synthetic", Item("a"), []), Play("p", "b"), StablePlayProjectionTests.Details(), Ct));
        Assert.Empty(await edits.PendingAsync(Origin, Ct));
    }
}
