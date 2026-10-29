using System.Text.Json;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class GovernanceLocalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public void LocalAndHttpModulesHaveNoReferenceToEachOtherAndPrivateTypesAreLocal()
    {
        Assert.DoesNotContain(typeof(LocalLauncher).Assembly.GetReferencedAssemblies(), a => a.Name == "Vndb.Client");
        Assert.DoesNotContain(typeof(WebLibraryClient).Assembly.GetReferencedAssemblies(), a => a.Name == "Vndb.Local");
        foreach (var type in new[] { typeof(LocalInstallationView), typeof(LocalArchivePlan), typeof(LocalInstallationAudit), typeof(LocalClientSettings), typeof(IOrganizationServer), typeof(JsonSnapshotStore), typeof(LocalDeviceIdentity) })
            Assert.Equal("Vndb.Local", type.Assembly.GetName().Name);
        Assert.Null(typeof(WebLibraryClient).GetMethod("SubmitAuditAsync"));
        Assert.Null(typeof(WebLibraryClient).GetMethod("UploadEvidenceAsync"));
        Assert.DoesNotContain(typeof(LocalClientSettings).GetProperties(), p => p.Name.Contains("Theme", StringComparison.Ordinal));
    }
    [Fact]
    public void NonemptyLegacyInstallationAndPlanKeepExactJsonAndPortableOrWindowsHashes()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var installation = new Installation("test-install", new("test-device", @"X:\Synthetic\Scope\Old", "Synthetic"), "present", now, 3, now, now);
        var view = new InstallationView(installation, [new("test-component", installation.Id, new("test-item", "."), 1, now, now)]);
        var local = JsonSerializer.Deserialize<LocalInstallationView>(JsonSerializer.Serialize(view, VndbJson.Options), VndbJson.Options)!;
        Assert.Equal(ArchiveFormat.Hash(view), LocalArchiveFormat.Hash(local));
        Assert.Equal(ArchiveFormat.LegacyWindowsHash(view), LocalArchiveFormat.LegacyWindowsHash(local));
        Assert.Equal(installation.Details.RootPath, local.Installation.Details.RootPath);
        var identity = new PhysicalIdentity("test-volume", "test-file", 1);
        var entries = new[] { new FileProof("story.dat", false, identity, 4, 1, 0, new('a', 64)) };
        var tree = new TreeProof(identity, entries, ArchiveFormat.TreeHash(identity, entries), 1, 4);
        var plan = new ArchivePlan("test-plan", "test-device", installation.Id, 3, installation.Details.RootPath, @"X:\Synthetic\Target\Company\Title",
            new(@"X:\Synthetic\Scope", @"X:\Synthetic\Target"), identity, identity, tree, [new("test-component", ".")], [], "Company", "Title", now);
        var localPlan = JsonSerializer.Deserialize<LocalArchivePlan>(JsonSerializer.Serialize(plan, VndbJson.Options), VndbJson.Options)!;
        LocalArchiveFormat.Validate(localPlan.Before);
        Assert.Equal(ArchiveFormat.Hash(plan), LocalArchiveFormat.Hash(localPlan));
        Assert.Equal(ArchiveFormat.LegacyWindowsHash(plan), LocalArchiveFormat.LegacyWindowsHash(localPlan));
        Assert.True(WirePrivacy.ContainsPrivateData(JsonSerializer.SerializeToElement(localPlan, VndbJson.Options)));
    }
    [Fact]
    public async Task StaleLibraryAndSettingsWritesAreRejectedWhileAtomicUpdatesMerge()
    {
        using var directory = new TestDirectory(); var seed = new LocalLibraryStore(directory.Path); await seed.SaveAsync(new([], []), Ct);
        var first = new LocalLibraryStore(directory.Path); var second = new LocalLibraryStore(directory.Path);
        var one = await first.ReadAsync(Ct); var two = await second.ReadAsync(Ct);
        await first.SaveAsync(one with { Items = [M5Tests.Item()] }, Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => second.SaveAsync(two with { Items = [M5Tests.Item(18)] }, Ct));
        await Task.WhenAll(first.UpdateAsync(v => v with { Items = [.. v.Items, M5Tests.Item(18)] }, Ct), second.UpdateAsync(v => v with { Items = [.. v.Items, M5Tests.Item(19)] }, Ct));
        Assert.Equal(["item-17", "item-18", "item-19"], (await seed.ReadAsync(Ct)).Items.Select(i => i.Item.Id).Order(StringComparer.Ordinal));
        var settingsOne = new LocalClientSettingsStore(directory.Path); var settingsTwo = new LocalClientSettingsStore(directory.Path);
        var a = await settingsOne.LoadAsync(Ct); var b = await settingsTwo.LoadAsync(Ct);
        await settingsOne.SaveAsync(a with { SourceScope = "test scope A" }, Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => settingsTwo.SaveAsync(b with { TargetRoot = "test scope B" }, Ct));
    }
    [Fact]
    public async Task SavingEquivalentLegacyJsonPreservesItsOriginalBytesAndUnknownFields()
    {
        using var directory = new TestDirectory(); var file = Path.Combine(directory.Path, "local-library.json");
        const string original = "{\r\n\"installations\": [], \"items\": [], \"format\": \"vn-local-library\", \"format_version\": 1, \"extension\": \"retained\"\r\n}";
        await File.WriteAllTextAsync(file, original, Ct);
        var store = new LocalLibraryStore(directory.Path); await store.SaveAsync(await store.ReadAsync(Ct), Ct);
        Assert.Equal(original, await File.ReadAllTextAsync(file, Ct));
    }
    [Fact]
    public async Task CorruptStateCannotBeReinitializedByAnUpdateOrSavedOver()
    {
        using var directory = new TestDirectory(); var file = Path.Combine(directory.Path, "local-library.json");
        await File.WriteAllTextAsync(file, "null", Ct);
        var store = new LocalLibraryStore(directory.Path);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateAsync(_ => new([], []), Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new([], []), Ct));
        Assert.Equal("null", await File.ReadAllTextAsync(file, Ct));
    }
    [Fact]
    public async Task TwoProfileProcessesPreserveBothEntriesWithoutRunningTheirExecutable()
    {
        using var directory = new TestDirectory(); var executable = Path.Combine(directory.Path, "synthetic.exe");
        await File.WriteAllBytesAsync(executable, [0x4d, 0x5a, 0, 0], Ct);
        await GovernanceSyncTests.WorkersAsync(directory.Path, "profile", executable);
        var profiles = await new LocalLauncher(directory.Path).ReadAsync(Ct);
        Assert.Equal(2, profiles.Count); Assert.All(profiles, p => Assert.Equal(executable, p.Executable));
    }
    [Fact]
    public async Task ConcurrentLegacyMigrationsPreserveOriginalFilesAndWriteOneMarker()
    {
        using var directory = new TestDirectory(); var legacy = Path.Combine(directory.Path, "legacy"); Directory.CreateDirectory(legacy);
        var original = Path.Combine(legacy, "sample.json"); await File.WriteAllTextAsync(original, "{\"test\":1}", Ct);
        await GovernanceSyncTests.WorkersAsync(directory.Path, "migrate", "unused");
        Assert.Equal(await File.ReadAllBytesAsync(original, Ct), await File.ReadAllBytesAsync(Path.Combine(directory.Path, "current", "sample.json"), Ct));
        Assert.True(File.Exists(Path.Combine(directory.Path, "current", "legacy-migration.json")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory.Path, "current"), "*.tmp", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task ReadOnlyScanDoesNotCreateDeviceIdentityOrClientData()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TestDirectory(); var scope = Path.Combine(directory.Path, "scope"); Directory.CreateDirectory(scope);
        var home = Path.Combine(directory.Path, "not-created");
        var scan = await LocalOrganizationWorkspace.ScanAsync(home, scope, 1, null, Ct);
        Assert.Empty(scan.Candidates); Assert.False(Directory.Exists(home));
    }
}
