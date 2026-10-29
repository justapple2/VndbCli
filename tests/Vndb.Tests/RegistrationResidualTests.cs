using Vndb.Contracts;
using Vndb.Core;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class RegistrationResidualTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Origin = "http://registration.test/";

    [Theory]
    [InlineData("valid")]
    [InlineData("legacy-hash")]
    [InlineData("batch")]
    [InlineData("hash")]
    [InlineData("instance")]
    [InlineData("binding")]
    [InlineData("origin")]
    [InlineData("status")]
    [InlineData("duplicate-row")]
    [InlineData("key")]
    [InlineData("incomplete")]
    [InlineData("no-intent")]
    [InlineData("no-binding")]
    [InlineData("bad-json")]
    [InlineData("changed-seed")]
    [InlineData("missing-target")]
    [InlineData("removed-target")]
    [InlineData("different-work")]
    [InlineData("foreign-device")]
    public async Task OfflineMappingRequiresCompleteBoundProofAndRetainsOriginals(string fault)
    {
        using var root = new TestDirectory(); var store = new LocalLibraryStore(Path.Combine(root.Path, "local"));
        var now = DateTimeOffset.UtcNow; var pendingId = LocalInstallationRegistration.PendingPrefix + Guid.NewGuid().ToString("D");
        var original = M5Tests.Item() with { Item = M5Tests.Item().Item with { Id = pendingId } };
        var mapped = original with { Item = original.Item with { Id = "server-item" } };
        var batch = new ActivityBatch { BatchId = Guid.NewGuid().ToString("D"), DeviceId = "synthetic-device", Device = new("Synthetic", "test"), Items = [new(pendingId, original.Item.Details)] };
        var intent = new LocalRegistrationIntent(Origin, batch); var binding = new ActivityBinding(Origin, "synthetic-instance");
        var receipt = new ActivityReceipt(batch.BatchId, LocalArchiveFormat.Hash(batch), binding.ServiceInstanceId, [new(pendingId, "items", "applied", mapped.Item.Id)], true, now);
        var journal = Path.Combine(store.DirectoryPath, "local-registrations", pendingId);
        var intentFile = Path.Combine(journal, "intent.json"); var bindingFile = Path.Combine(journal, "binding.json"); var receiptFile = Path.Combine(journal, "receipt.json");
        var savedBinding = binding;
        switch (fault)
        {
            case "legacy-hash": receipt = receipt with { PayloadSha256 = LocalArchiveFormat.LegacyWindowsHash(batch) }; break;
            case "batch": receipt = receipt with { BatchId = Guid.NewGuid().ToString("D") }; break;
            case "hash": receipt = receipt with { PayloadSha256 = new string('0', 64) }; break;
            case "instance": receipt = receipt with { ServiceInstanceId = "other-instance" }; break;
            case "binding": savedBinding = binding with { ServiceInstanceId = "other-instance" }; break;
            case "origin": savedBinding = binding with { Origin = "http://other.test/" }; break;
            case "status": receipt = receipt with { Results = [receipt.Results[0] with { Status = "conflict" }] }; break;
            case "duplicate-row": receipt = receipt with { Results = [receipt.Results[0], receipt.Results[0]] }; break;
            case "key": receipt = receipt with { Results = [receipt.Results[0] with { Key = "other-key" }] }; break;
            case "incomplete": receipt = receipt with { Complete = false }; break;
            case "changed-seed": intent = intent with { Batch = batch with { Items = [new(pendingId, original.Item.Details with { Title = "Changed" })] } }; break;
            case "removed-target": mapped = mapped with { Item = mapped.Item with { DeletedAt = now } }; break;
            case "different-work": mapped = mapped with { Item = mapped.Item with { Details = mapped.Item.Details with { NovelId = "v999" } } }; break;
        }
        if (fault != "no-intent") await JsonSnapshotStore.AtomicWriteAsync(intentFile, intent, false, Ct);
        if (fault != "no-binding") await JsonSnapshotStore.AtomicWriteAsync(bindingFile, savedBinding, false, Ct);
        await JsonSnapshotStore.AtomicWriteAsync(receiptFile, new LocalRegistrationReceipt(binding, receipt), false, Ct);
        if (fault == "bad-json") await File.WriteAllTextAsync(receiptFile, "{", Ct);
        var installation = new LocalInstallationView(new("installation", new(fault == "foreign-device" ? "other-device" : batch.DeviceId, root.Path, "Synthetic"), "present", now, 1, now, now),
            [new("component", "installation", new(mapped.Item.Id, ".", "game"), 1, now, now)]);
        await store.SaveAsync(new([installation], fault == "missing-target" ? [] : [mapped]), Ct);
        var play = new PersonalPlayView("play-original", new("Synthetic", original.Item.Details.NovelId, "played", null, null, null, null, null, null, null, null), 1, now, now, ItemId: pendingId);
        var cache = Path.Combine(root.Path, "cache"); var shelfFile = Path.Combine(cache, "shelf.json");
        await JsonSnapshotStore.AtomicWriteAsync(shelfFile, new ShelfSnapshot(Origin, [original], [play], [installation], now), false, Ct);
        var files = Directory.EnumerateFiles(journal).Append(shelfFile).Append(store.FilePath).ToDictionary(f => f, File.ReadAllBytes);
        var reader = new LocalShelfReader(store, cache, Origin); var result = await reader.ShelfAsync(Ct);
        var valid = fault is "valid" or "legacy-hash";
        Assert.Equal(!valid, reader.RegistrationReviewNeeded);
        Assert.Equal(valid ? mapped.Item.Id : pendingId, Assert.Single(result.Snapshot.Items).Item.Id);
        Assert.Equal(valid ? mapped.Item.Id : pendingId, Assert.Single(result.Snapshot.Plays).ItemId);
        Assert.Equal(play.Id, Assert.Single(result.Snapshot.Plays).Id);
        foreach (var (file, bytes) in files) Assert.Equal(bytes, await File.ReadAllBytesAsync(file, Ct));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    public async Task ReconfirmedExistingDirectoryRestoresPresenceWithoutChangingIds(string status)
    {
        using var root = new TestDirectory(); var game = Path.Combine(root.Path, "game"); Directory.CreateDirectory(game);
        var store = new LocalLibraryStore(Path.Combine(root.Path, "local")); var device = await LocalDeviceIdentity.LoadOrCreateAsync(store.DirectoryPath, Ct);
        var registration = new LocalInstallationRegistration(store, device);
        var request = new InstallationRegistration(game, "Synthetic", [new(new("Synthetic", M5Tests.Item()))]);
        var preview = await registration.PreviewAsync(request, Ct); var initial = await registration.CommitAsync(preview, LocalArchiveFormat.Hash(preview), Ct);
        await store.UpdateAsync(l => l with { Installations = [initial.Installation with { Installation = initial.Installation.Installation with { Status = status } }] }, Ct);
        var restored = await registration.PreviewAsync(request, Ct); Assert.False(restored.Reused);
        var saved = await registration.CommitAsync(restored, LocalArchiveFormat.Hash(restored), Ct);
        Assert.Equal("present", saved.Installation.Installation.Status);
        Assert.Equal(initial.Installation.Installation.Id, saved.Installation.Installation.Id);
        Assert.Equal(initial.Installation.Components, saved.Installation.Components);
        Assert.Equal(initial.Installation.Installation.Revision + 1, saved.Installation.Installation.Revision);
        var bytes = await File.ReadAllBytesAsync(store.FilePath, Ct);
        var repeat = await registration.PreviewAsync(request, Ct); Assert.True(repeat.Reused);
        await registration.CommitAsync(repeat, LocalArchiveFormat.Hash(repeat), Ct);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(store.FilePath, Ct));
    }
}
