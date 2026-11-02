using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.ViewModels;
using Vndb.Desktop.Services;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class K123ReviewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Watches : IObservationWatchFactory
    {
        public int Created, Disposed;
        public Action<string>? Creating;
        public IDisposable Create(string path, Action<string?, bool> changed)
        { Interlocked.Increment(ref Created); Creating?.Invoke(path); return new Handle(() => Interlocked.Increment(ref Disposed)); }
        private sealed class Handle(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    }
    [Fact]
    public async Task PreCanceledHostStartDoesNotAcquireWatchersOrLeaveABackgroundLoop()
    {
        using var f = new ObservationTests.Fixture(); await f.InitializeAsync(); var watches = new Watches();
        await using var host = new ObservationHost(f.Store, f.Device, _ => Task.FromResult<IReadOnlyList<LaunchProfile>>([f.Profile]), f.Files, watches, f.Clock);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StartAsync(f.Settings, cancel.Token));
        Assert.Equal(0, watches.Created); Assert.False(host.Running);
    }
    [Fact]
    public async Task ShutdownDuringWatchCreationClosesLateHandleAndPublishesNoLateResult()
    {
        using var f = new ObservationTests.Fixture(); await f.InitializeAsync(); var watches = new Watches();
        var profiles = new TaskCompletionSource<IReadOnlyList<LaunchProfile>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Task? shutdown = null; var changed = 0;
        await using var host = new ObservationHost(f.Store, f.Device, _ => profiles.Task, f.Files, watches, f.Clock);
        host.Changed += _ => changed++;
        watches.Creating = path => { if (path == f.Company) { shutdown = host.DisposeAsync().AsTask(); stopping.TrySetResult(); } };
        await host.StartAsync(f.Settings, Ct); profiles.SetResult([f.Profile]);
        await stopping.Task.WaitAsync(Ct); await shutdown!.WaitAsync(Ct);
        Assert.Equal(watches.Created, watches.Disposed); Assert.Equal(0, host.WatcherCount); Assert.Equal(0, changed); Assert.False(host.Running);
    }
    [Fact]
    public async Task LocalPendingItemsUseTheSameLightProjectionWithoutLosingFullNotes()
    {
        using var dir = new TestDirectory(); var local = new LocalLibraryStore(dir.Path); var row = M5Tests.Item();
        row = row with { Item = row.Item with { Id = LocalInstallationRegistration.PendingPrefix + Guid.NewGuid().ToString("D"), Details = row.Item.Details with { Notes = new string('x', 50000), ExtraNotes = "Synthetic second note" } }, PlayCount = 0 };
        await local.SaveAsync(new([], [row]), Ct);
        var operations = new LibraryReadOperations((_, _) => throw new InvalidOperationException("Summary only"), (_, _) => throw new InvalidOperationException("No play prefetch"),
            Summaries: (_, _, _) => Task.FromResult<ShelfSummaryPage?>(new([], 1, 100, 0, false)));
        var online = new LocalShelfReader(local, dir.Path, "http://review.test/", operations);
        var summary = Assert.Single((await online.ShelfAsync(Ct)).Snapshot.Items);
        Assert.Null(summary.Item.Details.Notes); Assert.Null(summary.Item.Details.ExtraNotes); Assert.Null(summary.Metadata!.Novel!.Description);
        var offline = new LocalShelfReader(local, dir.Path, "http://review.test/");
        Assert.Null(Assert.Single((await offline.ShelfAsync(Ct)).Snapshot.Items).Item.Details.Notes);
        Assert.Equal(row.Item.Details.Notes, (await offline.ItemAsync(row.Item.Id, Ct)).Item.Details.Notes);
        Assert.Equal(row.Item.Details.ExtraNotes, Assert.Single((await local.ReadAsync(Ct)).Items).Item.Details.ExtraNotes);
    }
    [Theory]
    [InlineData("playing")]
    [InlineData("unknown-status")]
    public void ShelfSummaryRejectsLatestStatusOutsideTheAggregate(string latest)
    {
        var row = ShelfSummaryTests.Summary() with { State = ShelfSummaryTests.Summary().State! with { LatestStatus = latest } };
        Assert.Throws<InvalidDataException>(() => ShelfSummaries.RequirePage(new([row], 1, 100, 1, false), 1, false));
    }
    [Fact]
    public void DetailsAndPlayUpdatesNotifyAndInvalidateTheSameDerivedProgress()
    {
        var row = ShelfSummaryTests.Summary(); var book = new BookViewModel(row, row.Metadata, [], summaryOnly: true);
        Assert.True(book.Progress.HasPlayed); var changes = new List<string?>(); book.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        var details = row with { Item = row.Item with { Revision = row.Item.Revision + 1 }, PlayCount = 1, State = new(PersonalProgress.FromStatuses(["not_started"]), false, "not_started") };
        book.ApplyDetails(details); Assert.Contains(nameof(book.Progress), changes); Assert.True(book.IsUnplayed); Assert.False(book.HasPlayed);
        var now = DateTimeOffset.UtcNow;
        book.ApplyPlays([new("synthetic-play", new("Synthetic", "v17", "completed", null, null, null, null, null, null, null, null), 1, now, now, ItemId: row.Item.Id)]);
        Assert.True(book.HasPlayed); Assert.False(book.IsUnplayed);
    }
    [Fact]
    public void RepeatedFullRecordFilteringDoesNotReallocateProgressOnEveryPredicate()
    {
        var row = M5Tests.Item(); var now = DateTimeOffset.UtcNow;
        var book = new BookViewModel(row, row.Metadata, [new("synthetic-play", new("Synthetic", "v17", "completed", null, null, null, null, null, null, null, null), 1, now, now, ItemId: row.Item.Id)]);
        _ = book.Progress.HasPlayed; var before = GC.GetAllocatedBytesForCurrentThread(); var count = 0;
        for (var n = 0; n < 10000; n++) if (book.Progress.HasPlayed) count++;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(10000, count); Assert.True(allocated < 65536, $"Repeated progress allocation: {allocated} bytes");
        var changed = new List<string?>(); book.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        book.LastOpenedAt = now; Assert.Contains(nameof(book.LastOpenedLabel), changed); Assert.DoesNotContain("尚未", book.LastOpenedLabel);
    }
    [Fact]
    public void PublicDetailsDoNotContainPersonalIdentityOrLocalObservations()
    {
        var row = M5Tests.Item(); var book = new BookViewModel(row, row.Metadata, []);
        book.SetObservation([new("install", "root", "Synthetic", null, [], "unknown", "root_unavailable", "unknown")]);
        var details = new BookDetailsViewModel(); details.Update(book, [], [], null);
        Assert.DoesNotContain(details.Metadata, f => f.Label is "书架条目" or "本机观测");
        Assert.Contains(details.PersonalFields, f => f.Label == "书架条目" && f.Value == row.Item.Id);
        Assert.Contains(details.LocalFields, f => f.Label == "本机观测" && f.Value == book.LocalObservation);
    }
    [Fact]
    public void UnknownLegacyPlayStatusRemainsReviewableInsteadOfBecomingUnplayed()
    {
        var row = ShelfSummaryTests.Summary() with { PlayCount = 1, State = new(PersonalProgress.FromStatuses(["legacy-unknown"]), false, "legacy-unknown") };
        ShelfSummaries.RequirePage(new([row], 1, 100, 1, false), 1, false);
        var book = new BookViewModel(row, row.Metadata, summaryOnly: true); Assert.True(book.NeedsPlayReview); Assert.False(book.IsUnplayed);
    }
    [Fact]
    public void SharedCommandChoicesKeepLegacyPairsTogetherButSeparateArgumentsAndActualPrograms()
    {
        var original = new LaunchProfile("direct", "Synthetic", "wrapper.exe", ["game.exe", "a b"], "synthetic", new('0', 64));
        var legacy = original with { Id = "locale", Title = "Locale", Sha256 = new('1', 64) };
        var argument = original with { Id = "argument", Arguments = ["game.exe a b"] };
        var actual = original with { Id = "actual", TrackedExecutable = "second.exe" };
        var work = original with { Id = "work", WorkingDirectory = "second" };
        var choices = LaunchConfiguration.CommandChoices([original, legacy, argument, actual, work]);
        Assert.Equal(new[] { "direct", "argument", "actual", "work" }, choices.Select(p => p.Id)); Assert.Same(original, choices[0]);
    }
    private sealed class CountedComponents(IReadOnlyList<LocalInstallationComponent> values, Action read) : IReadOnlyList<LocalInstallationComponent>
    {
        public int Count => values.Count;
        public LocalInstallationComponent this[int index] => values[index];
        public IEnumerator<LocalInstallationComponent> GetEnumerator() { read(); return values.GetEnumerator(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class ShelfLibrary(ShelfSnapshot snapshot) : IDesktopLibrary
    {
        public Task<ShelfResult> ShelfAsync(CancellationToken ct) => Task.FromResult(new ShelfResult(snapshot, "synthetic"));
        public Task<MetadataResponse> QueryAsync(string text, string source, CancellationToken ct) => throw new InvalidOperationException("No queries");
        public Task<IReadOnlyList<PersonalPlayView>> PlaysAsync(string item, CancellationToken ct) => throw new InvalidOperationException("No detail prefetch");
    }
    private sealed class Dialogs : IDesktopDialogs
    {
        public Task<string?> ExecutableAsync() => Task.FromResult<string?>(null);
        public Task<string?> FolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveJsonAsync(string title) => Task.FromResult<string?>(null);
    }
    [Fact]
    public async Task StartupHistoryReusesExactItemIndexInsteadOfEnumeratingEveryInstallationPerBook()
    {
        using var dir = new TestDirectory(); const int count = 1200; var now = DateTimeOffset.UtcNow; var reads = 0;
        var items = Enumerable.Range(1, count).Select(n => M5Tests.Item(n) with { PlayCount = 0 }).ToArray();
        var installations = items.Select(i => new LocalInstallationView(new("install-" + i.Item.Id, new("synthetic-device", "synthetic-root", "Synthetic"), "present", now, 1, now, now),
            new CountedComponents([new("component-" + i.Item.Id, "install-" + i.Item.Id, new(i.Item.Id, "."), 1, now, now)], () => reads++))).ToArray();
        var vm = new MainViewModel(new ShelfLibrary(new("http://review.test/", items, [], installations, now)), new(dir.Path), new Dialogs());
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(count, vm.Shelf.TotalPages * 60); Assert.All(vm.Shelf.Books, b => Assert.Null(b.LastOpenedAt));
        Assert.True(reads < count * 8, $"Repeated component enumeration: {reads}");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedCandidatesDoNotPermanentlyBlockNewDiscoveryAndIgnoresArePreserved(bool initiallyPending)
    {
        using var f = new ObservationTests.Fixture(); await f.InitializeAsync(); var root = f.Settings.Roots![0]; var now = f.Clock.GetUtcNow();
        var prior = Enumerable.Range(0, 5000).Select(n => new ObservationCandidate("synthetic-" + n, root.Id, n == 0 ? "Studio/Game" : "Studio/Old-" + n, "directory", new("synthetic-volume", "old-" + n, 1), 0, now.UtcDateTime, now,
            n == 1 ? "ignored" : initiallyPending ? "pending" : n % 2 == 0 ? "registered" : "missing")).ToArray();
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(f.Data, "local-observations.json"), new ObservationSnapshot(f.Device, [], [], prior), false, Ct);
        var added = Path.Combine(f.Company, "NewGame"); f.Files.Put(added, true);
        var result = await f.CheckAsync();
        if (initiallyPending)
        {
            Assert.True(result.Degraded); Assert.Contains(result.Snapshot.Candidates, c => c.Id == "synthetic-0" && c.State == "registered");
            result = await f.CheckAsync();
        }
        Assert.InRange(result.Snapshot.Candidates.Count, 1, 5000);
        Assert.Contains(result.Snapshot.Candidates, c => c.RelativePath == "Studio/NewGame");
        Assert.Contains(result.Snapshot.Candidates, c => c.Id == "synthetic-1" && c.State == "ignored");
        Assert.Equal("present", Assert.Single((await f.Store.ReadAsync(Ct)).Installations).Installation.Status);
    }
    [Fact]
    public async Task UiPreferenceWritesRespectTheirReadBudgetAndPreserveTheLastReadableFile()
    {
        using var dir = new TestDirectory(); var store = new UiPreferencesStore(dir.Path); await store.SaveAsync(new("Dark"), Ct);
        var path = Path.Combine(dir.Path, "ui-preferences.json"); var original = await File.ReadAllBytesAsync(path, Ct);
        var value = (await store.ReadAsync(Ct)) with { Extensions = new() { ["future_setting"] = System.Text.Json.JsonSerializer.SerializeToElement(new string('x', 65536)) } };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(value, Ct));
        Assert.Equal(original, await File.ReadAllBytesAsync(path, Ct)); Assert.Equal("Dark", (await store.ReadAsync(Ct)).Theme);
    }
    [Fact]
    public async Task PublicQuerySelectionCannotBorrowInstallationOrAutoSelectAProfileByNovelId()
    {
        using var f = new ObservationTests.Fixture(); await f.InitializeAsync();
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(f.Data, "device.json"), new ClientDeviceIdentity(f.Device, "Synthetic", "windows", f.Clock.GetUtcNow()), false, Ct);
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(f.Data, "launch-profiles.json"), new[] { f.Profile }, false, Ct);
        using var services = new DesktopServices(f.Data);
        var vm = new MainViewModel(services.Library, services.Launcher, new Dialogs(), services);
        await vm.InitializeAsync(); await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedBook = new(null, Assert.Single(vm.Shelf.Books).Metadata);
        Assert.Empty(vm.Installations); Assert.Null(vm.SelectedInstallation); Assert.Null(vm.SelectedProfile); Assert.Empty(vm.Executable);
    }
}
