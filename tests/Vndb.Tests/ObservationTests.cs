using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vndb.Cli;
using Vndb.Core;
using Vndb.Desktop.ViewModels;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class ObservationTests
{
    internal static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal sealed class Clock : TimeProvider
    { private DateTimeOffset _now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => _now; public void Advance(int seconds) => _now = _now.AddSeconds(seconds); }
    internal sealed class Files : IObservationFiles
    {
        private readonly ConcurrentDictionary<string, ObservationFacts> _rows = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _children = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private int _id;
        public Action<string>? Inspecting { get; set; }
        public int Probes, Enumerations;
        public List<string> Enumerated { get; } = [];
        public ObservationFacts Inspect(string path) { Interlocked.Increment(ref Probes); Inspecting?.Invoke(path); return _rows.GetValueOrDefault(path, new("missing")); }
        public IReadOnlyList<string> Children(string path, int limit, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref Enumerations); lock (Enumerated) Enumerated.Add(path);
            if (Inspect(path).State != "present") throw new UnauthorizedAccessException("Synthetic unreadable root");
            var rows = (_children.GetValueOrDefault(path)?.Keys ?? Enumerable.Empty<string>()).Order(StringComparer.Ordinal).Take(limit + 1).ToArray();
            if (rows.Length > limit) throw new IOException("Synthetic limit"); return rows;
        }
        public ObservationFacts Put(string path, bool directory, long length = 10)
        { var facts = new ObservationFacts("present", directory, new("synthetic-volume", "id-" + Interlocked.Increment(ref _id), 1), length, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)); Set(path, facts); return facts; }
        public void Set(string path, ObservationFacts value)
        { _rows[path] = value; _children.GetOrAdd(Path.GetDirectoryName(path)!, _ => new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))[path] = 0; }
        public Dictionary<string, ObservationFacts> RemoveTree(string path)
        { var old = _rows.Where(p => ObservationSettingsStore.Within(path, p.Key)).ToDictionary(p => p.Key, p => p.Value); foreach (var key in old.Keys) { _rows.TryRemove(key, out _); _children.GetValueOrDefault(Path.GetDirectoryName(key)!)?.TryRemove(key, out _); } return old; }
        public void Restore(Dictionary<string, ObservationFacts> rows) { foreach (var p in rows) Set(p.Key, p.Value); }
    }
    internal sealed class Fixture : IDisposable
    {
        public TestDirectory Directory { get; } = new();
        public Files Files { get; } = new(); public Clock Clock { get; } = new();
        public string Device { get; } = Guid.NewGuid().ToString("D");
        public string Data => Path.Combine(Directory.Path, "client");
        public string Root => Path.Combine(Directory.Path, "games");
        public string Company => Path.Combine(Root, "Studio");
        public string Game => Path.Combine(Company, "Game");
        public string Exe => Path.Combine(Game, "game.exe");
        public string Content => Path.Combine(Game, "data.dat");
        public LocalLibraryStore Store { get; }
        public ObservationSettings Settings { get; private set; }
        public LaunchProfile Profile => new("profile", "Synthetic", Exe, [], Game, new('0', 64), "install", "component");
        public Fixture()
        {
            Store = new(Data); Files.Put(Root, true); Files.Put(Company, true); Files.Put(Game, true); Files.Put(Exe, false); Files.Put(Content, false);
            Settings = new(true, [new ObservationSettingsStore(Data, Files).Capture(Root, "games", 2)]);
        }
        public async Task InitializeAsync(bool identity = true)
        {
            var now = Clock.GetUtcNow(); LocalInstallation installation = new("install", new(Device, Game, "Synthetic"), "present", now, 1, now, now) { DirectoryIdentity = identity ? Files.Inspect(Game).Identity : null };
            await Store.SaveAsync(new([new(installation, [new("component", "install", new("item-17", "."), 1, now, now)])], [M5Tests.Item()]), Ct);
        }
        public LocalObservation Observer() => new(Store, Device, Files, Clock);
        public Task<ObservationResult> CheckAsync(IReadOnlyList<string>? paths = null, bool discover = true) => Observer().CheckAsync(Settings, [Profile], paths, discover, Ct);
        public async Task<LocalOwnedPackage> AddPackageAsync()
        {
            var root = Path.Combine(Directory.Path, "packages"); Files.Put(root, true); var facts = Files.Put(Path.Combine(root, "Synthetic.7z"), false, 20);
            Settings = Settings with { Roots = Settings.Roots!.Append(new ObservationSettingsStore(Data, Files).Capture(root, "packages", 1)).ToArray() };
            var package = new LocalOwnedPackage(Guid.NewGuid().ToString("D"), Device, "item-17", root, "Synthetic.7z", facts.Length, facts.LastWriteUtc, "present", Clock.GetUtcNow(), 1, Clock.GetUtcNow());
            await Store.UpdateAsync(v => v with { Packages = [package] }, Ct); return package;
        }
        public void Dispose() => Directory.Dispose();
    }
    [Fact]
    public async Task StableMetadataNeverRewritesAuthorityOrObservationAndDoesNotHashContent()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var first = await f.CheckAsync();
        var before = await File.ReadAllBytesAsync(f.Store.FilePath, Ct); var state = Path.Combine(f.Data, "local-observations.json"); var stateBefore = await File.ReadAllBytesAsync(state, Ct);
        f.Clock.Advance(700); var second = await f.CheckAsync();
        Assert.Equal(0, second.Metrics.JsonWrites); Assert.Equal(0, second.Metrics.ChangedInstallations); Assert.Equal(0, second.Metrics.ContentHashBytes);
        Assert.Equal(before, await File.ReadAllBytesAsync(f.Store.FilePath, Ct)); Assert.Equal(stateBefore, await File.ReadAllBytesAsync(state, Ct));
        Assert.Equal("ready", Assert.Single(first.Snapshot.Installations).LaunchState);
        Assert.DoesNotContain(f.Exe, f.Files.Enumerated); // No file contents or PE inspection.
    }
    [Fact]
    public async Task MissingDirectoryRequiresTwoChecksThenOriginalIdentityCanRecoverWithoutLosingRegistration()
    {
        using var f = new Fixture(); await f.InitializeAsync(); await f.CheckAsync(); var removed = f.Files.RemoveTree(f.Game);
        var first = await f.CheckAsync(); Assert.Equal("confirming_missing", Assert.Single(first.Snapshot.Installations).Reason);
        Assert.Equal("unknown", (await f.Store.ReadAsync(Ct)).Installations[0].Installation.Status);
        f.Clock.Advance(11); var missing = await f.CheckAsync(); Assert.Equal("missing", Assert.Single(missing.Snapshot.Installations).Status);
        var recorded = (await f.Store.ReadAsync(Ct)).Installations[0]; Assert.Equal("component", recorded.Components[0].Id); Assert.Null(recorded.Installation.RemovedAt);
        f.Files.Restore(removed); var recovered = await f.CheckAsync(); Assert.Equal("present", Assert.Single(recovered.Snapshot.Installations).Status);
        Assert.Single((await f.Store.ReadAsync(Ct)).Items);
    }
    [Theory]
    [InlineData("unavailable")] [InlineData("volume")] [InlineData("directory")]
    public async Task OfflineRootsVolumeReuseAndReplacedDirectoriesStayUnknown(string kind)
    {
        using var f = new Fixture(); await f.InitializeAsync(); await f.CheckAsync();
        if (kind == "unavailable") f.Files.Set(f.Root, new("unknown"));
        else if (kind == "volume") f.Files.Put(f.Root, true);
        else f.Files.Put(f.Game, true);
        f.Clock.Advance(600); var result = await f.CheckAsync(); var row = Assert.Single(result.Snapshot.Installations);
        Assert.Equal("unknown", row.Status); Assert.Null(row.NegativeSince); Assert.NotEqual("ready", row.LaunchState);
        Assert.DoesNotContain(result.Snapshot.Candidates, c => c.RelativePath.Contains("game.exe", StringComparison.Ordinal));
    }
    [Fact]
    public async Task MissingEntryWithContentIsOwnedAndLeftoverSaveDirectoryCanBecomeMissing()
    {
        using var f = new Fixture(); await f.InitializeAsync(); await f.CheckAsync(); f.Files.RemoveTree(f.Exe);
        var result = await f.CheckAsync(); var row = Assert.Single(result.Snapshot.Installations); Assert.Equal("present", row.Status); Assert.Equal("entry_missing", row.LaunchState);
        f.Files.RemoveTree(f.Content); f.Files.Put(Path.Combine(f.Game, "savedata"), true);
        Assert.Equal("confirming_missing", Assert.Single((await f.CheckAsync()).Snapshot.Installations).Reason);
        f.Clock.Advance(11); Assert.Equal("missing", Assert.Single((await f.CheckAsync()).Snapshot.Installations).Status);
    }
    [Fact]
    public async Task RenamedOrUnrecognizedDataCannotBeMistakenForAnUninstall()
    {
        using var f = new Fixture(); await f.InitializeAsync(); await f.CheckAsync(); f.Files.RemoveTree(f.Exe); f.Files.RemoveTree(f.Content);
        f.Files.Put(Path.Combine(f.Game, "new-content.pak"), false); f.Clock.Advance(20);
        var row = Assert.Single((await f.CheckAsync()).Snapshot.Installations); Assert.Equal("unknown", row.Status); Assert.Equal("insufficient_evidence", row.Reason);
    }
    [Fact]
    public async Task SharedContainerWithMissingComponentStaysForManualReviewRatherThanClaimingCompleteOwnership()
    {
        using var f = new Fixture(); await f.InitializeAsync();
        await f.Store.UpdateAsync(v => v with { Installations = v.Installations.Select(i => i with { Components = i.Components.Append(new("bonus", i.Installation.Id, new("item-18", "bonus"), 1, f.Clock.GetUtcNow(), f.Clock.GetUtcNow())).ToArray() }).ToArray() }, Ct);
        var row = Assert.Single((await f.CheckAsync()).Snapshot.Installations); Assert.Equal("unknown", row.Status); Assert.Equal("insufficient_evidence", row.Reason);
    }
    [Fact]
    public async Task BrokenAlternativeProfileDoesNotDisableAnIndependentConfirmedEntry()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var alternative = Path.Combine(f.Game, "alternative.exe"); f.Files.Put(alternative, false);
        var other = f.Profile with { Id = "alternative", Executable = alternative };
        await f.Observer().CheckAsync(f.Settings, [f.Profile, other], ct: Ct); f.Files.RemoveTree(f.Exe);
        var row = Assert.Single((await f.Observer().CheckAsync(f.Settings, [f.Profile, other], ct: Ct)).Snapshot.Installations);
        Assert.Equal("ready", row.LaunchState); Assert.DoesNotContain(f.Profile.Id, row.ReadyProfiles!); Assert.Contains(other.Id, row.ReadyProfiles!);
    }
    [Fact]
    public async Task ChangedExecutableDoesNotRefreshLaunchTrustOrEraseLocalOwnership()
    {
        using var f = new Fixture(); await f.InitializeAsync(); await f.CheckAsync(); f.Files.Put(f.Exe, false, 99);
        var row = Assert.Single((await f.CheckAsync()).Snapshot.Installations); Assert.Equal("present", row.Status); Assert.Equal("changed", row.LaunchState);
        var book = new BookViewModel(M5Tests.Item(), M5Tests.Item().Metadata); book.SetObservation([row]); Assert.False(book.CanLaunchObserved);
        Assert.Equal(new string('0', 64), f.Profile.Sha256);
        var approved = f.Profile with { Sha256 = new('1', 64) };
        var renewed = await f.Observer().CheckAsync(f.Settings, [approved], ct: Ct);
        Assert.Equal("ready", Assert.Single(renewed.Snapshot.Installations).LaunchState); // Explicit launcher save can establish a new metadata baseline.
    }
    [Fact]
    public async Task ReplacingAllKnownContentsInsideSameContainerRequiresExplicitReconfirmation()
    {
        using var f = new Fixture(); await f.InitializeAsync(); await f.CheckAsync(); f.Files.Put(f.Exe, false, 100); f.Files.Put(f.Content, false, 100);
        var row = Assert.Single((await f.CheckAsync()).Snapshot.Installations); Assert.Equal("unknown", row.Status); Assert.Equal("file_changed", row.Reason);
        Assert.Equal(new string('0', 64), f.Profile.Sha256);
    }
    [Fact]
    public async Task V2AvailabilityTransactionsDoNotHydrateOrDamageMetadataAndPreserveUnknownFields()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var raw = JsonNode.Parse(await File.ReadAllBytesAsync(f.Store.FilePath, Ct))!;
        raw["installations"]![0]!["installation"]!["future"] = "keep"; await File.WriteAllTextAsync(f.Store.FilePath, raw.ToJsonString(), Ct);
        var preview = await f.Store.PreviewIndexAsync(Ct); await f.Store.ApplyIndexAsync(preview, LocalArchiveFormat.Hash(preview), Ct); await f.CheckAsync();
        var metadata = Assert.Single(Directory.GetFiles(Path.Combine(f.Data, "local-metadata"))); await File.WriteAllTextAsync(metadata, "{synthetic-corrupt", Ct);
        f.Files.RemoveTree(f.Game); await f.CheckAsync(); f.Clock.Advance(11); var result = await f.CheckAsync(); Assert.Equal("missing", Assert.Single(result.Snapshot.Installations).Status);
        var index = JsonNode.Parse(await File.ReadAllBytesAsync(f.Store.IndexPath, Ct))!; Assert.Equal("keep", (string?)index["library"]!["installations"]![0]!["installation"]!["future"]);
        Assert.Single((await f.Store.ReadSummaryAsync(Ct)).Items); await Assert.ThrowsAsync<InvalidDataException>(() => f.Store.ReadAsync(Ct));
        Assert.Equal("{synthetic-corrupt", await File.ReadAllTextAsync(metadata, Ct));
    }
    [Fact]
    public async Task ConcurrentRegistrationIsPreservedAndObservationCanRetry()
    {
        using var f = new Fixture(); await f.InitializeAsync(); await f.CheckAsync(); f.Files.RemoveTree(f.Exe); f.Files.RemoveTree(f.Content);
        var fired = false; f.Files.Inspecting = path =>
        {
            if (path != f.Game || fired) return; fired = true;
            f.Store.UpdateAsync(v => v with { Installations = v.Installations.Select(i => i with { Installation = i.Installation with { Revision = i.Installation.Revision + 1, Details = i.Installation.Details with { Title = "Concurrent" } } }).ToArray() }, Ct).GetAwaiter().GetResult();
        };
        var result = await f.CheckAsync(); Assert.Equal(1, result.Metrics.SkippedConcurrent);
        var view = (await f.Store.ReadAsync(Ct)).Installations[0]; Assert.Equal("Concurrent", view.Installation.Details.Title); Assert.Equal("present", view.Installation.Status);
        var row = Assert.Single(result.Snapshot.Installations); Assert.Equal("unknown", row.Status); Assert.Equal("concurrent_change", row.Reason); Assert.Null(row.ReadyProfiles);
    }
    [Fact]
    public async Task PackageReplacementAndMissingUseMetadataAndNeverUpdateExpectedVersion()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var package = await f.AddPackageAsync(); await f.CheckAsync(); var file = Path.Combine(package.ScanRoot, package.RelativePath);
        var original = f.Files.Inspect(file); f.Files.Put(file, false, 900);
        Assert.Equal("unknown", Assert.Single((await f.CheckAsync()).Snapshot.Packages).Status);
        Assert.Equal(20, (await f.Store.ReadAsync(Ct)).Packages![0].Length);
        f.Files.Set(file, original); Assert.Equal("present", Assert.Single((await f.CheckAsync()).Snapshot.Packages).Status);
        f.Files.RemoveTree(file); await f.CheckAsync(); f.Clock.Advance(11); Assert.Equal("missing", Assert.Single((await f.CheckAsync()).Snapshot.Packages).Status);
    }
    [Fact]
    public async Task ShallowDiscoveryFindsOfflineInstallsWithoutEnumeratingKnownGameInternals()
    {
        using var f = new Fixture(); await f.InitializeAsync(); f.Files.Put(Path.Combine(f.Game, "save"), true); f.Files.Put(Path.Combine(f.Game, "save", "session.sav"), false);
        var newCompany = Path.Combine(f.Root, "NewStudio"); f.Files.Put(newCompany, true); f.Files.Put(Path.Combine(newCompany, "NewGame"), true);
        var first = await f.CheckAsync(); Assert.Equal("checking", Assert.Single(first.Snapshot.Candidates).State);
        Assert.DoesNotContain(Path.Combine(f.Game, "save"), f.Files.Enumerated); Assert.Contains(newCompany, first.WatchPaths);
        f.Clock.Advance(16); var restarted = await f.CheckAsync(); var candidate = Assert.Single(restarted.Snapshot.Candidates); Assert.Equal("pending", candidate.State);
        Assert.Single((await f.Store.ReadAsync(Ct)).Items); await f.Observer().IgnoreAsync(candidate.Id, Ct);
        Assert.Equal("ignored", Assert.Single((await f.CheckAsync()).Snapshot.Candidates).State);
    }
    [Fact]
    public async Task ManualRenameProducesReadOnlyMappingHintAndDoesNotBecomeAnUninstall()
    {
        using var f = new Fixture(); await f.InitializeAsync(); await f.CheckAsync();
        var moved = f.Files.RemoveTree(f.Game); var target = Path.Combine(f.Company, "Renamed");
        foreach (var pair in moved) f.Files.Set(target + pair.Key[f.Game.Length..], pair.Value);
        await f.CheckAsync(); f.Clock.Advance(20); var result = await f.CheckAsync();
        var row = Assert.Single(result.Snapshot.Installations); Assert.Equal("unknown", row.Status); Assert.Equal("location_changed", row.Reason); Assert.Null(row.NegativeSince);
        var hint = Assert.Single(result.Snapshot.Candidates); Assert.Equal("install", hint.PreviousInstallationId); Assert.Equal(f.Game, hint.PreviousPath);
        Assert.Equal(f.Game, (await f.Store.ReadAsync(Ct)).Installations[0].Installation.Details.RootPath); // No implicit rebase or duplicate installation.
    }
    [Fact]
    public async Task RenamedPackageKeepsOwnershipUnknownAndPreservesOriginalRelativeRegistration()
    {
        using var f = new Fixture(); await f.InitializeAsync(); var package = await f.AddPackageAsync(); await f.CheckAsync();
        var file = Path.Combine(package.ScanRoot, package.RelativePath); var facts = f.Files.Inspect(file); f.Files.RemoveTree(file); f.Files.Set(Path.Combine(package.ScanRoot, "Renamed.7z"), facts);
        await f.CheckAsync(); f.Clock.Advance(20); var result = await f.CheckAsync();
        Assert.Equal("location_changed", Assert.Single(result.Snapshot.Packages).Reason); Assert.Equal("unknown", Assert.Single(result.Snapshot.Packages).Status);
        Assert.Equal(package.Id, Assert.Single(result.Snapshot.Candidates).PreviousPackageId);
        Assert.Equal(package.RelativePath, Assert.Single((await f.Store.ReadAsync(Ct)).Packages!).RelativePath);
    }
    [Fact]
    public async Task SettingsRejectOverbroadNestedOrClientDataRootsAndDoNotCreateMissingRoots()
    {
        using var f = new Fixture(); var store = new ObservationSettingsStore(f.Data, f.Files); await store.ReadAsync(Ct);
        Assert.Throws<ArgumentException>(() => store.Capture(Path.GetPathRoot(f.Root)!, "games", 2));
        Assert.Throws<ArgumentException>(() => store.Capture(f.Data, "games", 2));
        var root = f.Settings.Roots![0]; var nested = store.Capture(f.Company, "games", 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new(true, [root, nested]), Ct));
        Assert.False(Directory.Exists(f.Root));
    }
    [Fact]
    public async Task CliCheckAndCandidatesAreOneShotAndUseExplicitDataRootWithoutNetwork()
    {
        using var dir = new TestDirectory(); var data = Path.Combine(dir.Path, "client"); using var output = new StringWriter(); using var error = new StringWriter();
        var cli = new CliApplication(output, error, environment: _ => null, settings: new());
        Assert.Equal(0, await cli.RunAsync(["observe", "check", "--identity-dir", data, "--json"], Ct));
        Assert.Contains("content_hash_bytes", output.ToString()); Assert.False(File.Exists(Path.Combine(data, "local-library.json")));
        Assert.Equal(0, await cli.RunAsync(["observe", "candidates", "--identity-dir", data, "--json"], Ct));
    }
}
