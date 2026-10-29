using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vndb.Admin;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

public sealed class SnapshotTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Factory(string path) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b) => b.UseSetting("Library:DataDirectory", path)
            .UseSetting("Library:WriteKey", "snapshot-test-key").UseSetting("CatalogJobs:Enabled", "false");
    }

    [Fact]
    public async Task CompleteRestoreKeepsDatabaseCoversIdentityPersonalDataAndBoundRetry()
    {
        using var dir = new TestDirectory();
        var live = Path.Combine(dir.Path, "live"); var snapshot = Path.Combine(dir.Path, "snapshot"); var restored = Path.Combine(dir.Path, "restored");
        var key = Path.Combine(dir.Path, "key.txt"); await File.WriteAllTextAsync(key, "snapshot-test-key", Ct);
        PresenceBatch batch; PresenceReceipt receipt; string instance; string playId;
        await using (var app = new Factory(live))
        {
            using var http = app.CreateClient(); var api = new WebLibraryClient(http, http.BaseAddress!, "snapshot-test-key", TimeSpan.FromSeconds(10));
            var store = app.Services.GetRequiredService<SqliteLibraryStore>();
            var bytes = new byte[] { 137, 80, 78, 71, 13, 10 }; var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            Directory.CreateDirectory(Path.Combine(live, "covers")); await File.WriteAllBytesAsync(Path.Combine(live, "covers", hash + ".png"), bytes, Ct);
            store.Save(StorageTests.Entry() with { Cover = new(hash, "covers/" + hash + ".png", "image/png", bytes.Length, "https://example.test/cover", DateTimeOffset.UtcNow), CoverStatus = "ready" });
            store.ImportPlays(PlayTests.Batch()); playId = store.Plays().Results[0].Id;
            var item = store.SaveItem(new(new("Synthetic item", "v17")), null, "snapshot-item");
            instance = (await api.OrganizationInfoAsync(Ct)).ServiceInstanceId;
            batch = new(Guid.NewGuid().ToString("D"), instance, Guid.NewGuid().ToString("D"), 0, [new(item.Id, true)]);
            receipt = await api.SubmitPresenceAsync(batch, Ct);
            // Snapshot while live SQLite is in WAL mode. Identity and every registered asset are required.
            var manifest = LibrarySnapshot.Create(live, snapshot, "sha256:synthetic-test-image", "1654:1654", key, Ct);
            Assert.Equal(instance, manifest.ServiceInstanceId); Assert.Equal(3, manifest.Files.Count);
            Assert.DoesNotContain("snapshot-test-key", await File.ReadAllTextAsync(Path.Combine(snapshot, "snapshot-manifest.json"), Ct));
            Assert.False(File.Exists(Path.Combine(snapshot, "library.db-wal")));
        }
        var before = await File.ReadAllBytesAsync(Path.Combine(snapshot, "library.db"), Ct);
        LibrarySnapshot.Restore(snapshot, restored, key, Ct);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(restored, "library.db"), Ct));
        await using (var app = new Factory(restored))
        {
            using var http = app.CreateClient(); var api = new WebLibraryClient(http, http.BaseAddress!, "snapshot-test-key", TimeSpan.FromSeconds(10));
            Assert.Equal(instance, (await api.OrganizationInfoAsync(Ct)).ServiceInstanceId);
            Assert.Equal(receipt, await api.SubmitPresenceAsync(batch, Ct));
            var store = app.Services.GetRequiredService<SqliteLibraryStore>();
            Assert.Equal(1, store.Statistics().Novels); Assert.Equal(2, store.Plays().Total); Assert.NotNull(store.GetPlay(playId)!.Source);
            Assert.True(store.PresenceHead(batch.DeviceId).Items[0].IsLocal); Assert.Equal(1, store.PresenceHead(batch.DeviceId).Revision);
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(snapshot, "library.db"), Ct));
        Assert.Throws<IOException>(() => LibrarySnapshot.Restore(snapshot, restored, key, Ct));
    }

    private static (string Source, string Snapshot, string Key) CreateFixture(TestDirectory dir)
    {
        var source = Path.Combine(dir.Path, "source"); var snapshot = Path.Combine(dir.Path, "snapshot"); var key = Path.Combine(dir.Path, "key.txt");
        var store = new SqliteLibraryStore(source); store.Save(StorageTests.Entry());
        File.WriteAllText(Path.Combine(source, "service-instance-id"), Guid.NewGuid().ToString("D") + "\n");
        File.WriteAllText(key, "snapshot-test-key"); LibrarySnapshot.Create(source, snapshot, "synthetic-test-image", keyFile: key, cancellationToken: Ct);
        return (source, snapshot, key);
    }

    [Fact]
    public void MissingKeyModifiedContentAndExtraFilesCannotRestore()
    {
        using var dir = new TestDirectory(); var (_, snapshot, key) = CreateFixture(dir); var target = Path.Combine(dir.Path, "target");
        Assert.Throws<InvalidDataException>(() => LibrarySnapshot.Restore(snapshot, target, cancellationToken: Ct)); Assert.False(Directory.Exists(target));
        File.WriteAllText(key, "wrong-test-key"); Assert.Throws<InvalidDataException>(() => LibrarySnapshot.Restore(snapshot, target, key, Ct));
        File.WriteAllText(key, "snapshot-test-key"); File.WriteAllText(Path.Combine(snapshot, "unexpected.json"), "{}");
        Assert.Throws<InvalidDataException>(() => LibrarySnapshot.Restore(snapshot, target, key, Ct)); File.Delete(Path.Combine(snapshot, "unexpected.json"));
        File.AppendAllText(Path.Combine(snapshot, "service-instance-id"), "changed");
        Assert.Throws<InvalidDataException>(() => LibrarySnapshot.Restore(snapshot, target, key, Ct)); Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void MissingIdentityOrCoverAndInterruptedOrExistingTargetNeverSucceed()
    {
        using var dir = new TestDirectory(); var source = Path.Combine(dir.Path, "source"); var store = new SqliteLibraryStore(source);
        var destination = Path.Combine(dir.Path, "target"); Assert.Throws<FileNotFoundException>(() => LibrarySnapshot.Create(source, destination, "test-image", cancellationToken: Ct));
        Assert.False(Directory.Exists(destination)); File.WriteAllText(Path.Combine(source, "service-instance-id"), Guid.NewGuid().ToString("D"));
        var hash = new string('a', 64); store.Save(StorageTests.Entry() with { Cover = new(hash, "covers/" + hash + ".jpg", "image/jpeg", 10, "https://example.test/cover", DateTimeOffset.UtcNow), CoverStatus = "ready" });
        Assert.ThrowsAny<IOException>(() => LibrarySnapshot.Create(source, destination, "test-image", cancellationToken: Ct)); Assert.False(Directory.Exists(destination));
        Assert.Throws<IOException>(() => LibrarySnapshot.Create(source, destination, "test-image", cancellationToken: Ct));
        Directory.CreateDirectory(destination); Assert.Throws<IOException>(() => LibrarySnapshot.Create(source, destination, "test-image", cancellationToken: Ct));
    }

    [Fact]
    public async Task AdminRestoreRequiresMaintenanceAndNeverCreatesIdentityOrOverwrites()
    {
        using var dir = new TestDirectory(); var (_, snapshot, key) = CreateFixture(dir);
        using var output = new StringWriter(); using var error = new StringWriter(); var admin = new AdminApplication(output, error);
        var target = Path.Combine(dir.Path, "target");
        Assert.NotEqual(0, await admin.RunAsync(["library", "restore", snapshot, target, "--key-file", key], Ct)); Assert.False(Directory.Exists(target));
        Assert.Equal(0, await admin.RunAsync(["library", "restore", snapshot, target, "--key-file", key, "--offline-maintenance"], Ct));
        Assert.NotEqual(0, await admin.RunAsync(["library", "restore", snapshot, target, "--key-file", key, "--offline-maintenance"], Ct));
    }
}
