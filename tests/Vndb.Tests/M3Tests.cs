using Vndb.Local;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

public sealed class M3Tests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static LibraryDevice Device(SqliteLibraryStore store, string name = "Client") => store.SaveDevice(new(Guid.NewGuid().ToString("D"), new(name, "windows")), "register-" + name);
    private static void V6(string directory)
    {
        using var db = new SqliteConnection("Data Source=" + Path.Combine(directory, "library.db") + ";Pooling=False"); db.Open();
        using var command = db.CreateCommand(); command.CommandText = """
            PRAGMA foreign_keys=OFF;
            DROP TABLE archive_evidence; DROP TABLE archive_unit_leases; DROP TABLE archive_runs;
            DROP TABLE installation_legacy_links; DROP TABLE installation_components; DROP TABLE installations;
            DROP TABLE device_legacy_names; DROP TABLE devices; PRAGMA user_version=6;
            """; command.ExecuteNonQuery();
    }
    private static string Raw(string directory, string table)
    {
        using var db = new SqliteConnection("Data Source=" + Path.Combine(directory, "library.db") + ";Pooling=False"); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT * FROM " + table + " ORDER BY 1";
        using var reader = command.ExecuteReader(); var rows = new List<object[]>();
        while (reader.Read()) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row); }
        return JsonSerializer.Serialize(rows);
    }
    [Fact]
    public void MigrationMapsActualAndOriginalPathsPreservesLegacyAndPersonalRowsAndKeepsIdsAfterRename()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); store.Save(StorageTests.Entry());
        var archive = store.RegisterArchive(ArchiveTests.Registration("archived"));
        store.ImportLocalGames([new(archive.Registration.Machine, archive.Registration.OriginalPath, "Old title", null, DateTimeOffset.UtcNow, "synthetic", new string('a', 64))]);
        store.ImportPlays(PlayTests.Batch());
        var before = new[] { "archive_locations", "archive_events", "local_games", "play_records", "play_events", "play_import_batches" }.ToDictionary(t => t, t => Raw(dir.Path, t));
        V6(dir.Path); store = new SqliteLibraryStore(dir.Path);
        var installation = Assert.Single(store.Installations()); var component = Assert.Single(store.Components());
        Assert.Equal(2, store.LegacyLocations().Count); Assert.All(store.LegacyLocations(), l => Assert.Equal(component.Id, l.ComponentId));
        Assert.Equal("recorded", installation.Status); Assert.True(Assert.Single(store.Devices()).Legacy);
        foreach (var table in before) Assert.Equal(table.Value, Raw(dir.Path, table.Key));
        var edited = store.SaveInstallation(new(installation.Details with { RootPath = @"E:\New company\New title" }, installation.Revision), installation.Id, "rename");
        Assert.Equal(installation.Id, edited.Id); Assert.Equal(component, Assert.Single(store.Components())); Assert.Equal("unverified", edited.Status); Assert.Null(edited.ObservedAt);
        Assert.Equal(edited.Details.RootPath, Assert.Single(store.Archives()).Registration.CurrentPath);
        Assert.Equal(archive.Registration.OriginalPath, Assert.Single(store.Archives()).Registration.OriginalPath);
        Assert.Equal(edited.Details.RootPath, Assert.Single(store.LocalGames()).Details.Path);
        foreach (var table in before) Assert.Equal(table.Value, Raw(dir.Path, table.Key));
        Assert.Throws<PlayConflictException>(() => store.RegisterArchive(archive.Registration));
        Assert.Single(new SqliteLibraryStore(dir.Path).Installations());
    }
    [Fact]
    public void AmbiguousSamePathIsNotAssignedAndPlannedDestinationIsNeverClaimedPresent()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); store.Save(StorageTests.Entry());
        store.Save(StorageTests.Entry() with { Novel = StorageTests.Entry().Novel with { Id = "v4" } });
        store.RegisterArchive(ArchiveTests.Registration("archived"));
        store.RegisterArchive(ArchiveTests.Registration("archived") with { NovelId = "v4", OriginalPath = @"E:\other-original" });
        V6(dir.Path); store = new SqliteLibraryStore(dir.Path);
        Assert.Empty(store.Installations()); Assert.All(store.LegacyLocations(), l => { Assert.Equal("needs_review", l.State); Assert.Null(l.ComponentId); });
        var pending = store.RegisterArchive(ArchiveTests.Registration() with { OriginalPath = @"E:\actual", CurrentPath = @"E:\future", RunId = "another" });
        var installation = Assert.Single(store.Installations()); Assert.Equal(pending.Registration.OriginalPath, installation.Details.RootPath); Assert.Equal("unverified", installation.Status);
    }
    [Fact]
    public void TwoDevicesAndUnknownWorksAreIndependentAndClaimIsExplicitAndCollisionSafe()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        var first = Device(store, "First"); var second = Device(store, "Second");
        var a = store.SaveInstallation(new(new(first.Id, @"E:\Games\Title", "Unknown")), null, "install-a");
        var b = store.SaveInstallation(new(new(second.Id, @"E:\Games\Title", "Unknown")), null, "install-b");
        Assert.NotEqual(a.Id, b.Id); Assert.Equal(2, store.Installations().Count);
        var item = store.SaveItem(new(new("未收录", Notes: "keep")), null, "item"); var component = store.SaveComponent(a.Id, new(new(item.Id, ".")), null, "component");
        Assert.Null(store.GetItem(component.Details.ItemId)!.Details.NovelId);
        Assert.Equal(component, store.SaveComponent(a.Id, new(new(item.Id, ".")), null, "component"));
        store.Save(StorageTests.Entry()); var legacy = store.RegisterArchive(ArchiveTests.Registration("archived"));
        var oldDevice = store.Devices().Single(d => d.Legacy);
        Assert.NotEqual(first.Id, oldDevice.Id);
        store.SaveInstallation(new(new(first.Id, legacy.Registration.CurrentPath, "Collision")), null, "collision");
        Assert.Throws<PlayConflictException>(() => store.ClaimLegacyDevice(oldDevice.Id, new(first.Id, oldDevice.Revision), "bad-claim"));
        Assert.Null(store.Devices().Single(d => d.Id == oldDevice.Id).MappedToId);
        var claimed = store.ClaimLegacyDevice(oldDevice.Id, new(second.Id, oldDevice.Revision), "claim");
        Assert.Equal(second.Id, claimed.MappedToId);
        var legacyInstallation = store.GetInstallation(store.LegacyLocations().Single().InstallationId!)!;
        Assert.Equal(second.Id, legacyInstallation.Details.DeviceId); Assert.Equal(item.Details, store.GetItem(item.Id)!.Details);
    }
    [Fact]
    public void ExplicitBundlePreservesThreeComponentIdsLegacyLinksAndOuterContainerOnRename()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); store.Save(StorageTests.Entry());
        var archiveIds = new List<string>();
        for (var i = 1; i <= 3; i++) archiveIds.Add(store.RegisterArchive(ArchiveTests.Registration("archived") with { OriginalPath = @"E:\old" + i, CurrentPath = @"E:\Whole bundle\game" + i }).Id);
        var before = Raw(dir.Path, "archive_locations"); var components = store.Components().Select(c => c.Id).Order().ToArray();
        var childIds = store.Installations().Select(i => new InstallationReference(i.Id, i.Revision)).ToArray();
        var request = new InstallationBundle(new(store.Devices()[0].Id, @"E:\Whole bundle", "完整外层合集"), childIds);
        var result = store.BundleInstallations(request, "bundle");
        Assert.Equivalent(result, store.BundleInstallations(request, "bundle"));
        Assert.Equal(components, result.Components.Select(c => c.Id).Order().ToArray()); Assert.Equal(3, result.Components.Count);
        Assert.Equal(new[] { "game1", "game2", "game3" }, result.Components.Select(c => c.Details.RelativePath).Order().ToArray());
        Assert.Single(store.Installations(), i => i.RemovedAt is null);
        Assert.All(store.LegacyLocations(), l => Assert.Equal(result.Installation.Id, l.InstallationId));
        var renamed = store.SaveInstallation(new(result.Installation.Details with { RootPath = @"E:\Company\Bundle" }, result.Installation.Revision), result.Installation.Id, "rename-bundle");
        Assert.All(store.Archives(), a => Assert.StartsWith(renamed.Details.RootPath, a.Registration.CurrentPath));
        Assert.Equal(before, Raw(dir.Path, "archive_locations")); Assert.Equal(3, store.Components(renamed.Id).Count);
        store.ImportLocalGames([new(store.Devices()[0].Details.Name, renamed.Details.RootPath, "Outer observation", null, DateTimeOffset.UtcNow, "synthetic", new string('b', 64))]);
        Assert.Single(store.LegacyLocations(), l => l.State == "needs_review");
        Assert.Single(store.Installations(), i => i.RemovedAt is null);
        var merged = store.Installations().First(i => i.MergedIntoId is not null);
        Assert.Throws<PlayConflictException>(() => store.SetInstallationRemoved(merged.Id, new(merged.Revision), false, "unmerge"));
    }
    [Fact]
    public void BundleCanExplicitlyReuseOuterObservationWithCasAndPreserveAllSourceIdentities()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); var device = Device(store);
        var outer = store.SaveInstallation(new(new(device.Id, @"E:\Bundle", "Outer")), null, "outer");
        var item = store.SaveItem(new(new("Unmatched observation")), null, "item");
        var observation = store.SaveComponent(outer.Id, new(new(item.Id, ".", "other")), null, "outer-component");
        var children = Enumerable.Range(1, 3).Select(n => store.SaveInstallation(new(new(device.Id, @"E:\Bundle\game" + n, "Chapter")), null, "child" + n)).ToArray();
        foreach (var child in children) store.SaveComponent(child.Id, new(new(item.Id, ".")), null, child.Id);
        var request = new InstallationBundle(outer.Details, children.Select(i => new InstallationReference(i.Id, i.Revision)).ToArray(), new(outer.Id, outer.Revision));
        Assert.Throws<PlayConflictException>(() => store.BundleInstallations(request with { Container = new(outer.Id, 99) }, "stale"));
        Assert.Equal(4, store.Installations().Count(i => i.RemovedAt is null));
        var result = store.BundleInstallations(request, "bundle");
        Assert.Equal(outer.Id, result.Installation.Id); Assert.Equal(4, result.Components.Count);
        Assert.Contains(observation, result.Components); Assert.Single(store.Installations(), i => i.RemovedAt is null);
        Assert.All(children, child => Assert.Equal(outer.Id, store.GetInstallation(child.Id)!.MergedIntoId));
    }
    [Fact]
    public void ObservationsAndSoftRemovalUseRevisionsWithoutInferringOnlineOrPlaying()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); store.Save(StorageTests.Entry());
        var device = Device(store); var item = store.SaveItem(new(new("Personal", "v17", Notes: "备注")), null, "item");
        var installation = store.SaveInstallation(new(new(device.Id, @"E:\Games\Title", "Title")), null, "install");
        var component = store.SaveComponent(installation.Id, new(new(item.Id, ".")), null, "component");
        var now = DateTimeOffset.UtcNow; var observed = store.ObserveInstallation(installation.Id, new("present", now, installation.Revision), "observe");
        var location = Assert.Single(Assert.Single(store.Presence(device.Id)).Locations); Assert.Equal("current_device", location.Scope); Assert.Equal("unknown", location.DeviceState);
        Assert.Throws<PlayConflictException>(() => store.ObserveInstallation(installation.Id, new("missing", now.AddMinutes(-1), observed.Revision), "old-observe"));
        var seen = store.DeviceSeen(device.Id, new(device.Revision), "seen"); Assert.NotNull(seen.LastSeenAt);
        Assert.Equal("recently_seen", Assert.Single(Assert.Single(store.Presence(device.Id)).Locations).DeviceState);
        var missing = store.ObserveInstallation(installation.Id, new("missing", now.AddSeconds(1), observed.Revision), "missing");
        var removed = store.SetInstallationRemoved(installation.Id, new(missing.Revision), true, "remove"); Assert.Empty(store.Presence());
        store.SetInstallationRemoved(installation.Id, new(removed.Revision), false, "restore"); Assert.Single(store.Presence());
        Assert.Equal(component, Assert.Single(store.Components())); Assert.Empty(store.ItemPlays()); Assert.Equal(item, store.GetItem(item.Id));
    }
    [Theory]
    [InlineData(@"E:\..\outside")]
    [InlineData("relative/game")]
    [InlineData(@"\\?\C:\device")]
    [InlineData(@"E:\Games\\Title")]
    [InlineData(@"E:\Games\Title.")]
    public void UnsafeAbsolutePathsAreRejected(string path) => Assert.Throws<ArgumentException>(() => InstallationRules.PathKey(path));
    [Theory]
    [InlineData("../outside")]
    [InlineData(@"C:\outside")]
    [InlineData("/outside")]
    [InlineData("game/../../outside")]
    public void ComponentsCannotEscapePhysicalContainer(string path) => Assert.Throws<ArgumentException>(() => InstallationRules.Relative(path));
    [Fact]
    public void NativePathsAndUncKeepTheirIdentityRules()
    {
        Assert.Equal("/", InstallationRules.PathKey("/")); Assert.Equal("/games/Title", InstallationRules.PathKey("/games/Title"));
        Assert.NotEqual(InstallationRules.PathKey("/games/Title"), InstallationRules.PathKey("/games/title"));
        Assert.Equal(InstallationRules.PathKey(@"\\server\share\Game"), InstallationRules.PathKey("//SERVER/share/game/"));
    }
    [Fact]
    public async Task DeviceIdentityIsRandomDurableAndCorruptFileIsNotOverwritten()
    {
        using var first = new TestDirectory(); using var second = new TestDirectory();
        var identities = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => LocalDeviceIdentity.LoadOrCreateAsync(first.Path, Ct)));
        Assert.All(identities, identity => Assert.Equal(identities[0], identity));
        Assert.NotEqual(identities[0].Id, (await LocalDeviceIdentity.LoadOrCreateAsync(second.Path, Ct)).Id);
        var path = Path.Combine(first.Path, "device.json"); await File.WriteAllTextAsync(path, "{}", Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalDeviceIdentity.LoadOrCreateAsync(first.Path, Ct)); Assert.Equal("{}", await File.ReadAllTextAsync(path, Ct));
    }
    private sealed class Factory(string directory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Library:DataDirectory", directory).UseSetting("Library:WriteKey", "m3-test-key").UseSetting("CatalogJobs:Enabled", "false");
    }
    [Fact]
    public async Task VersionedEndpointsAreAuthenticatedIdempotentAndExposeComponentRelationships()
    {
        using var dir = new TestDirectory(); await using var factory = new Factory(dir.Path); using var http = factory.CreateClient();
        var write = new DeviceWrite(Guid.NewGuid().ToString("D"), new("Synthetic", "windows"));
        using var denied = await http.PostAsJsonAsync("/api/v1/devices", write, VndbJson.Options, Ct); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        http.DefaultRequestHeaders.Add("X-Library-Request", "1"); http.DefaultRequestHeaders.Add("X-Library-Key", "m3-test-key"); http.DefaultRequestHeaders.Add("Idempotency-Key", "register");
        using var created = await http.PostAsJsonAsync("/api/v1/devices", write, VndbJson.Options, Ct); Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var repeated = await http.PostAsJsonAsync("/api/v1/devices", write, VndbJson.Options, Ct); Assert.Equal(await created.Content.ReadAsStringAsync(Ct), await repeated.Content.ReadAsStringAsync(Ct));
        var info = await http.GetFromJsonAsync<ServiceInfo>("/api/v1/info", VndbJson.Options, Ct); Assert.Contains("boolean_presence", info!.Capabilities); Assert.Equal(13, info.SchemaVersion);
        Assert.Equal(HttpStatusCode.Gone, (await http.GetAsync("/api/v1/installations", Ct)).StatusCode);
        foreach (var path in new[] { "/installations.html", "/installations.js", "/local-presence.js" })
        { using var response = await http.GetAsync(path, Ct); Assert.Equal(HttpStatusCode.OK, response.StatusCode); }
    }
}
