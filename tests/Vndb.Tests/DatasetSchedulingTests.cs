using Vndb.Contracts;
using Vndb.Core;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

public sealed class DatasetSchedulingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(false, 200)]
    [InlineData(true, 25)]
    public async Task LocalImportBurstStopsAtBatchOrTimeBudgetWithoutUpstreamRequests(bool advance, int expected)
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        File.WriteAllText(Path.Combine(dir.Path, "service-instance-id"), Guid.NewGuid().ToString("D"));
        var file = DumpPackageTests.Archive(dir.Path, files =>
        {
            var row = files["db/producers"];
            files["db/producers"] = string.Concat(Enumerable.Range(1, 300).Select(n => row.Replace("p1\t", "p" + n + "\t", StringComparison.Ordinal)));
        });
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("No upstream requests permitted")); using var http = new HttpClient(handler);
        using var datasets = new DatasetImports(store, http);
        await using (var stream = File.OpenRead(file)) await datasets.UploadAsync(stream, "burst", Ct);
        await datasets.StepAsync(Ct); // Preflight is separate; no test timing depends on disk speed.
        var clock = new BurstClock(advance);
        using var core = new VndbClient(http, VndbClientTests.FastOptions); var covers = new CoverDownloader(http, dir.Path);
        using var importer = new LibraryImporter(core, covers, store); using var metadata = new MetadataImports(store, core, importer, covers, clock, new ImportLimits());
        using var enricher = new CatalogEnricher(core, importer, store, store);
        var maintenance = new CatalogMaintenance(metadata, new ActivityImports(store, metadata), new CatalogJobs(enricher, store, clock), store, clock, datasets);
        await maintenance.StepAsync(Ct);
        Assert.Equal(expected, store.DatasetJobs()[0].Processed); Assert.Equal("producer", store.DatasetJobs()[0].Phase);
        Assert.Empty(handler.Requests); Assert.Empty(store.ReadItems().Items); Assert.Equal(0, store.Plays().Total);
        var job = store.DatasetJobs()[0]; datasets.Control(job.Id, "pause", new(job.Revision), "pause");
        await maintenance.StepAsync(Ct); Assert.Equal(expected, store.DatasetJobs()[0].Processed);
    }
    private sealed class BurstClock(bool advance) : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => advance ? Interlocked.Add(ref _timestamp, 1000) : 0;
    }
}
