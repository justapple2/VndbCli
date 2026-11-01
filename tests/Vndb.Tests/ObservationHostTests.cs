using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class ObservationHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal sealed class Watches : IObservationWatchFactory
    {
        private int _created, _disposed;
        public int Created => Volatile.Read(ref _created);
        public int Disposed => Volatile.Read(ref _disposed);
        public IDisposable Create(string path, Action<string?, bool> changed) { Interlocked.Increment(ref _created); return new Handle(() => Interlocked.Increment(ref _disposed)); }
        private sealed class Handle(Action dispose) : IDisposable { private int _done; public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) dispose(); } }
    }
    [Fact]
    public async Task WatcherBudgetHintsOverflowSingleOwnerAndShutdownStayBounded()
    {
        using var f = new ObservationTests.Fixture(); await f.InitializeAsync(); var watches = new Watches();
        var settings = f.Settings with { WatcherLimit = 1 };
        var host = new ObservationHost(f.Store, f.Device, _ => Task.FromResult<IReadOnlyList<LaunchProfile>>([f.Profile]), f.Files, watches, f.Clock);
        await host.StartAsync(settings, Ct); var initial = await host.CheckNowAsync(Ct); Assert.NotNull(initial); Assert.Equal(1, host.WatcherCount); Assert.True(initial.Degraded);
        await host.CheckNowAsync(Ct); host.Hint(Path.Combine(f.Root, "unrelated"));
        await host.CheckNowAsync(Ct, f.Game); Assert.True(host.PendingCount > 0); // Starting one game cannot consume another directory's pending hint.
        await using (var second = new ObservationHost(f.Store, f.Device, _ => Task.FromResult<IReadOnlyList<LaunchProfile>>([]), f.Files, watches, f.Clock))
        { await second.StartAsync(settings, Ct); Assert.Equal(0, second.WatcherCount); Assert.Contains("已有观测实例", second.Status); }
        for (var n = 0; n < 10000; n++) host.Hint(Path.Combine(f.Root, "event-" + n));
        Assert.InRange(host.PendingCount, 0, 256); Assert.True(host.Hints >= 10000);
        var created = watches.Created; host.Hint(null, true); f.Clock.Advance(15);
        var recovered = await host.CheckNowAsync(Ct); Assert.NotNull(recovered); Assert.True(watches.Created > created); Assert.Equal(1, host.WatcherCount);
        Assert.Equal(0, recovered.Metrics.ContentHashBytes); Assert.True(recovered.Metrics.Probes < 1000);
        await host.DisposeAsync(); Assert.Equal(0, host.WatcherCount); Assert.Equal(watches.Created, watches.Disposed); Assert.False(host.Running);
        host.Hint(f.Root); Assert.Equal(0, host.PendingCount); await host.DisposeAsync();
    }
    [Fact]
    public async Task RealShallowWatcherSeesNewDirectoriesAndIgnoresDeepSaveWrites()
    {
        using var dir = new TestDirectory(); var company = Path.Combine(dir.Path, "company"); var game = Path.Combine(company, "game"); Directory.CreateDirectory(game);
        var fired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (new ObservationWatchFactory().Create(company, (path, error) => { if (!error && path is not null) fired.TrySetResult(path); }))
        {
            await File.WriteAllTextAsync(Path.Combine(game, "synthetic-save.dat"), "synthetic", Ct);
            await Task.Delay(200, Ct); Assert.False(fired.Task.IsCompleted);
            var added = Path.Combine(company, "new-game"); Directory.CreateDirectory(added);
            Assert.Equal(added, await fired.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        }
        // Disposed watcher must leave no handle that prevents normal fixture cleanup.
    }
    [Fact]
    public async Task InterruptedOldArchiveIsSeededAsADurableBarrierAndDoesNotBecomeMissing()
    {
        using var f = new ObservationTests.Fixture(); await f.InitializeAsync(); f.Files.RemoveTree(f.Game);
        var folder = Path.Combine(f.Data, "organization-state"); Directory.CreateDirectory(folder);
        var id = Guid.NewGuid().ToString("D");
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(folder, id + ".json"), new { plan = new { installation_id = "install", before = new { synthetic = true } }, receipt = new { state = "planned" } }, false, Ct);
        var result = await f.CheckAsync(); Assert.True(result.Deferred); Assert.Equal("archive_pending", Assert.Single(result.Snapshot.Installations).Reason);
        f.Clock.Advance(1000); result = await f.CheckAsync(); Assert.Equal("present", Assert.Single(result.Snapshot.Installations).Status);
        Assert.Equal("present", (await f.Store.ReadAsync(Ct)).Installations[0].Installation.Status);
        Assert.True(Directory.GetFiles(Path.Combine(f.Data, "local-resource-intents"), "*.json").Length >= 2);
    }
    [Fact]
    public async Task CancellationLeavesRegistrationAndDerivedSnapshotUnchanged()
    {
        using var f = new ObservationTests.Fixture(); await f.InitializeAsync(); await f.CheckAsync();
        var before = await File.ReadAllBytesAsync(f.Store.FilePath, Ct); var state = Path.Combine(f.Data, "local-observations.json"); var snapshot = await File.ReadAllBytesAsync(state, Ct);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Observer().CheckAsync(f.Settings, [f.Profile], ct: stop.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(f.Store.FilePath, Ct)); Assert.Equal(snapshot, await File.ReadAllBytesAsync(state, Ct));
    }
    [Fact]
    public async Task NativeMetadataProbeWorksOnOrdinarySyntheticNtfsFilesWithoutRunningThem()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); if (!new DriveInfo(Path.GetPathRoot(dir.Path)!).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) return;
        var root = Path.Combine(dir.Path, "games"); var game = Path.Combine(root, "Studio", "Synthetic"); Directory.CreateDirectory(game);
        var exe = Path.Combine(game, "synthetic.exe"); var data = Path.Combine(game, "synthetic.dat");
        await File.WriteAllTextAsync(exe, "synthetic non-executable fixture", Ct); await File.WriteAllTextAsync(data, "synthetic content", Ct);
        var client = Path.Combine(dir.Path, "client"); var settings = new ObservationSettingsStore(client); var captured = settings.Capture(root, "games", 2);
        var device = Guid.NewGuid().ToString("D"); var now = DateTimeOffset.UtcNow; var store = new LocalLibraryStore(client);
        await store.SaveAsync(new([new(new("native-install", new(device, game, "Synthetic"), "present", now, 1, now, now) { DirectoryIdentity = NativeFiles.Identity(game) },
            [new("native-component", "native-install", new("item", "."), 1, now, now)])], []), Ct);
        var profile = new LaunchProfile("native-profile", "Synthetic", exe, [], game, new('0', 64), "native-install", "native-component");
        var result = await new LocalObservation(store, device).CheckAsync(new(true, [captured]), [profile], ct: Ct);
        Assert.Equal("ready", Assert.Single(result.Snapshot.Installations).LaunchState); Assert.Equal(0, result.Metrics.ContentHashBytes);
        Assert.Equal("synthetic non-executable fixture", await File.ReadAllTextAsync(exe, Ct)); Assert.Equal("synthetic content", await File.ReadAllTextAsync(data, Ct));
    }
}
