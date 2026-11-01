using System.Diagnostics;
using System.Text.Json;
using Vndb.Core;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

[Collection("ShelfScale")]
public sealed class ObservationScaleTests
{
    [Theory]
    [InlineData(1000)] [InlineData(5000)]
    public async Task RepeatedLargeMetadataPassHasNoAuthorityWritesAndNoGameContentHashes(int count)
    {
        var ct = TestContext.Current.CancellationToken; using var f = new ObservationTests.Fixture(); var now = f.Clock.GetUtcNow();
        var installations = new List<LocalInstallationView>(); var profiles = new List<LaunchProfile>();
        for (var n = 0; n < count; n++)
        {
            var path = Path.Combine(f.Company, "Synthetic-" + n); var identity = f.Files.Put(path, true).Identity;
            var exe = Path.Combine(path, "game.exe"); f.Files.Put(exe, false); f.Files.Put(Path.Combine(path, "data.dat"), false);
            var id = "installation-" + n; var component = "component-" + n;
            installations.Add(new(new(id, new(f.Device, path, "Synthetic " + n), "present", now, 1, now, now) { DirectoryIdentity = identity }, [new(component, id, new("item-" + n, "."), 1, now, now)]));
            profiles.Add(new("profile-" + n, "Synthetic", exe, [], path, new('0', 64), id, component));
        }
        await f.Store.SaveAsync(new(installations, []), ct);
        var preview = await f.Store.PreviewIndexAsync(ct); await f.Store.ApplyIndexAsync(preview, LocalArchiveFormat.Hash(preview), ct);
        var observer = f.Observer(); var watch = Stopwatch.StartNew(); var baseline = await observer.CheckAsync(f.Settings, profiles, discover: false, ct: ct); var baselineMs = watch.Elapsed.TotalMilliseconds;
        var index = await File.ReadAllBytesAsync(f.Store.IndexPath, ct); var bytes = new FileInfo(Path.Combine(f.Data, "local-observations.json")).Length;
        watch.Restart(); var current = await observer.CheckAsync(f.Settings, profiles, discover: false, ct: ct); var steadyMs = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(0, current.Metrics.JsonWrites); Assert.Equal(0, current.Metrics.ContentHashBytes); Assert.Equal(0, current.Metrics.ChangedInstallations);
        Assert.Equal(index, await File.ReadAllBytesAsync(f.Store.IndexPath, ct)); Assert.True(current.Metrics.Probes <= count * 5 + 2);
        var metrics = new { count, baseline_ms = baselineMs, steady_ms = steadyMs, index_bytes = index.LongLength, observation_bytes = bytes, baseline = baseline.Metrics, steady = current.Metrics,
            note = "Synthetic in-memory filesystem metadata plus real bounded JSON. Not a disk or NAS latency benchmark." };
        TestContext.Current.TestOutputHelper!.WriteLine(JsonSerializer.Serialize(metrics));
        if (Environment.GetEnvironmentVariable("VN_OBSERVATION_RESULTS") is { Length: > 0 } output)
        { Directory.CreateDirectory(output); await File.WriteAllTextAsync(Path.Combine(output, "observation-scale-" + count + ".json"), JsonSerializer.Serialize(metrics, VndbJson.Options), ct); }
    }
}
