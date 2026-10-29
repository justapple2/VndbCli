using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class DeviceIdentityLeaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task IdentityWaitsForItsResourceLeaseBeforeCreatingOrReading()
    {
        using var root = new TestDirectory(); var lease = await LocalFileLease.AcquireAsync(root.Path, "device-identity", Ct);
        Task<ClientDeviceIdentity> read;
        try { read = LocalDeviceIdentity.LoadOrCreateAsync(root.Path, Ct); await Task.Yield(); Assert.False(read.IsCompleted); Assert.False(File.Exists(Path.Combine(root.Path, "device.json"))); }
        finally { lease.Dispose(); }
        var identity = await read; var original = await File.ReadAllBytesAsync(Path.Combine(root.Path, "device.json"), Ct);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 48).Select(_ => LocalDeviceIdentity.LoadOrCreateAsync(root.Path, Ct)));
        Assert.All(concurrent, found => Assert.Equal(identity, found)); Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(root.Path, "device.json"), Ct));
    }
    [Fact]
    public async Task CancelledIdentityWaitDoesNotCreateAReplacementOrRewriteCorruptBytes()
    {
        using var root = new TestDirectory(); var path = Path.Combine(root.Path, "device.json"); await File.WriteAllTextAsync(path, "{}", Ct);
        using (var lease = await LocalFileLease.AcquireAsync(root.Path, "device-identity", Ct))
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct); var pending = LocalDeviceIdentity.LoadOrCreateAsync(root.Path, cancel.Token); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalDeviceIdentity.LoadOrCreateAsync(root.Path, Ct)); Assert.Equal("{}", await File.ReadAllTextAsync(path, Ct));
    }
}
