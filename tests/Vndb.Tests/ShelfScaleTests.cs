using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Library;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

// Deterministic, synthetic scale fixture; timings are diagnostic, never pass/fail thresholds.
[CollectionDefinition("ShelfScale", DisableParallelization = true)]
public sealed class ShelfScaleCollection;

[Collection("ShelfScale")]
public sealed class ShelfScaleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(1000)] [InlineData(5000)] [InlineData(10000)]
    public async Task LightShelfKeepsHeavyTextAndReleaseCataloguesOffTheStartupPath(int count)
    {
        using var dbRoot = new TestDirectory(); using var localRoot = new TestDirectory(); var store = new SqliteLibraryStore(dbRoot.Path);
        var entry = StorageTests.Entry(); var now = DateTimeOffset.UtcNow;
        entry = entry with { Novel = entry.Novel with { Description = new string('x', 8192), ReleaseCatalog = new(
            Enumerable.Range(1, 40).Select(n => new NovelRelease { Id = "r" + n, Title = new string('r', 80), Vns = [new("v17", "complete")], Languages = [new("ja", "Synthetic", null, false, true)] }).ToArray(), now) } };
        store.Save(entry);
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(dbRoot.Path, "library.db") + ";Pooling=False"))
        {
            db.Open(); using var tx = db.BeginTransaction(); using var insert = db.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = "INSERT INTO library_items(id,novel_id,record_json) VALUES($id,'v17',$json)";
            var id = insert.Parameters.Add("$id", SqliteType.Text); var json = insert.Parameters.Add("$json", SqliteType.Text);
            for (var n = 0; n < count; n++)
            {
                id.Value = "scale-" + n.ToString("D5"); json.Value = JsonSerializer.Serialize(new LibraryItem((string)id.Value, new("Synthetic " + n, "v17", Notes: new string('n', 2048)), 1, now, now), VndbJson.Options);
                insert.ExecuteNonQuery();
            }
            tx.Commit();
        }
        store.ReadItems(pageSize: 100); store.ReadShelfSummary(); // Warm JIT/SQLite, no timing assertion.
        var before = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
        var old = store.ReadItems(pageSize: 100); var oldBytes = JsonSerializer.SerializeToUtf8Bytes(old, VndbJson.Options).LongLength;
        var oldMs = watch.Elapsed.TotalMilliseconds; var oldAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread(); watch.Restart();
        var light = store.ReadShelfSummary(); var lightBytes = JsonSerializer.SerializeToUtf8Bytes(light, VndbJson.Options).LongLength;
        var lightMs = watch.Elapsed.TotalMilliseconds; var lightAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(count, light.Total); Assert.Equal(100, light.Items.Count); Assert.True(lightBytes < oldBytes / 5);
        double? legacyStartupMs = null; long? legacyStartupAllocated = null; long? legacySnapshotBytes = null;
        if (count == 1000) // Larger heavy aggregates exceed the existing 64MiB cap; do not allocate hundreds of MiB to reproduce it.
        {
            using var legacyRoot = new TestDirectory(); var start = GC.GetTotalAllocatedBytes(); watch.Restart();
            var legacy = new LocalShelfReader(new(legacyRoot.Path), legacyRoot.Path, "http://scale.test/", new(
                (page, _) => Task.FromResult(store.ReadItems(page: page, pageSize: 100)), (_, _) => Task.FromResult<IReadOnlyList<PersonalPlayView>>([])));
            Assert.Equal(count, (await legacy.ShelfAsync(Ct)).Snapshot.Items.Count);
            legacyStartupMs = watch.Elapsed.TotalMilliseconds; legacyStartupAllocated = GC.GetTotalAllocatedBytes() - start;
            legacySnapshotBytes = new FileInfo(Path.Combine(legacyRoot.Path, "shelf.json")).Length;
        }
        var fullReads = 0; var playReads = 0;
        var reader = new LocalShelfReader(new(localRoot.Path), localRoot.Path, "http://scale.test/", new(
            (_, _) => { fullReads++; throw new InvalidOperationException(); }, (_, _) => { playReads++; throw new InvalidOperationException(); },
            Summaries: (page, deleted, _) => Task.FromResult<ShelfSummaryPage?>(store.ReadShelfSummary(page: page, deleted: deleted))));
        var startupBefore = GC.GetTotalAllocatedBytes(); watch.Restart(); var snapshot = await reader.ShelfAsync(Ct); var startupMs = watch.Elapsed.TotalMilliseconds;
        var startupAllocated = GC.GetTotalAllocatedBytes() - startupBefore;
        Assert.Equal(count, snapshot.Snapshot.Items.Count); Assert.Empty(snapshot.Snapshot.Plays); Assert.Equal(0, fullReads); Assert.Equal(0, playReads);
        var snapshotBytes = new FileInfo(Path.Combine(localRoot.Path, "shelf.json")).Length;
        Assert.True(snapshotBytes < 32L * 1024 * 1024);
        var metrics = new { count, page_size = 100, old_page_bytes = oldBytes, summary_page_bytes = lightBytes, old_page_ms = oldMs, summary_page_ms = lightMs,
            old_page_allocated = oldAllocated, summary_page_allocated = lightAllocated, summary_startup_ms = startupMs, summary_startup_allocated = startupAllocated, snapshot_bytes = snapshotBytes,
            legacy_startup_ms = legacyStartupMs, legacy_startup_allocated = legacyStartupAllocated, legacy_snapshot_bytes = legacySnapshotBytes,
            old_estimated_shelf_bytes = oldBytes * (count / 100), note = "Synthetic shared metadata; allocations are cumulative, not retained/peak memory. Legacy startup measured at1000 only; larger payload is estimated, not timed." };
        TestContext.Current.TestOutputHelper!.WriteLine(JsonSerializer.Serialize(metrics));
        if (Environment.GetEnvironmentVariable("VN_SCALE_RESULTS") is { Length: > 0 } output)
        { Directory.CreateDirectory(output); await File.WriteAllTextAsync(Path.Combine(output, "shelf-scale-" + count + ".json"), JsonSerializer.Serialize(metrics, VndbJson.Options), Ct); }
    }
}
