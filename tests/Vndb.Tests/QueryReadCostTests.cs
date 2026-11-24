using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

public sealed class QueryReadCostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("Ｓｙｎｔｈｅｔｉｃ １２３＋ 特典")]
    [InlineData("が ガ か\u3099 ① Ⅰ 𐐨😀+!? ")]
    [InlineData("")]
    public void AllocationReductionPreservesTheOriginalRuneNormalization(string title)
    {
        var original = string.Concat(title.Normalize(System.Text.NormalizationForm.FormKC).EnumerateRunes()
            .Where(r => System.Text.Rune.IsLetterOrDigit(r) || r.Value == '+').Select(r => System.Text.Rune.ToUpperInvariant(r).ToString()));
        Assert.Equal(original, PlayRules.NormalizeTitle(title));
    }
    [Theory]
    [InlineData(1000)]
    [InlineData(10000)]
    public async Task LargeCatalogReadsRemainBoundedAndAssociationsMatchLegacySemantics(int shelfSize)
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); store.Save(StorageTests.Entry());
        var entry = StorageTests.Entry() with { Novel = StorageTests.Entry().Novel with { Description = new('d', 1024) } };
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(dir.Path, "library.db") + ";Pooling=False"))
        {
            db.Open(); using var query = db.CreateCommand();
            query.CommandText = """
                WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM seq WHERE x<67459)
                INSERT INTO novels SELECT 'v'||x,'Synthetic '||x,NULL,'2026-01-01','ja',NULL,'synthetic '||x,
                  json_set($entry,'$.novel.id','v'||x,'$.novel.title','Synthetic '||x),$now,$now,NULL,'none' FROM seq WHERE x<>17;
                INSERT INTO novel_developers SELECT id,'p1',0 FROM novels WHERE id<>'v17';
                INSERT INTO novel_titles SELECT id,'ja','作品 '||id,NULL,1,1 FROM novels WHERE id<>'v17';
                WITH RECURSIVE seq(x) AS (SELECT 3 UNION ALL SELECT x+1 FROM seq WHERE x<12000)
                INSERT INTO producers SELECT 'p'||x,'Studio '||x,NULL,'co','ja',json_set($producer,'$.id','p'||x,'$.name','Studio '||x),$now FROM seq;
                WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM seq WHERE x<$shelf)
                INSERT INTO library_items SELECT 'item-'||x,'v17',json_set($item,'$.id','item-'||x,'$.details.title','Personal Alias '||x,'$.details.notes',$notes) FROM seq;
                INSERT INTO local_presence SELECT 'synthetic-device',id,1 FROM library_items;
                """;
            query.Parameters.AddWithValue("$entry", JsonSerializer.Serialize(entry, VndbJson.Options));
            query.Parameters.AddWithValue("$producer", JsonSerializer.Serialize(entry.Producers[0], VndbJson.Options));
            query.Parameters.AddWithValue("$item", JsonSerializer.Serialize(M5Tests.Item().Item, VndbJson.Options));
            query.Parameters.AddWithValue("$notes", new string('n', 4096)); query.Parameters.AddWithValue("$shelf", shelfSize); query.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); query.ExecuteNonQuery();
        }
        using var handler = new StubHandler("{\"results\":[],\"more\":false}"); using var http = new HttpClient(handler);
        using var upstream = new VndbClient(http, VndbClientTests.FastOptions); using var importer = new LibraryImporter(upstream, new(http, dir.Path), store);
        using var catalog = new CatalogEnricher(upstream, importer, store, store); using var queries = new CatalogQueries(store, catalog, store, upstream, importer, TimeProvider.System);
        long commands = 0, bytes = 0; var measuring = new AsyncLocal<bool>();
        using var listener = new MeterListener { InstrumentPublished = (i, l) => { if (i.Meter.Name is "Vndb.Library.Reads" or "Vndb.Library.Sql") l.EnableMeasurementEvents(i); } };
        listener.SetMeasurementEventCallback<long>((i, n, _, _) => { if (!measuring.Value) return; if (i.Name == "vndb.library.sql.commands") Interlocked.Add(ref commands, n); if (i.Name == "vndb.library.read.bytes") Interlocked.Add(ref bytes, n); }); listener.Start();
        var measurements = new List<object>();
        T Measure<T>(string operation, Func<T> read, int maxSql = 20)
        {
            commands = bytes = 0; measuring.Value = true; var allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew(); T result;
            try { result = read(); } finally { measuring.Value = false; } watch.Stop();
            measurements.Add(new { operation, sql_commands = commands, materialized_text_bytes = bytes, allocated_bytes = GC.GetAllocatedBytesForCurrentThread() - allocated, milliseconds = watch.Elapsed.TotalMilliseconds });
            Assert.InRange(commands, 1, maxSql); return result;
        }
        Assert.NotNull(Measure("legacy-producer-get", () => store.FindProducers("").FirstOrDefault(p => p.Producer.Id == "p1")));
        var legacyBytes = bytes; Assert.NotNull(Measure("producer-get", () => store.GetProducer("p1"))); Assert.True(bytes * 100 < legacyBytes);
        Assert.Single(Measure("local-id", () => queries.Local("vn_get", new(""), "v17")).Items);
        Assert.Single(Measure("normalized-search", () => queries.Local("vn_search", new("Ｓｙｎｔｈｅｔｉｃ 12345"))).Items);
        Assert.Equal("v17", Assert.Single(Measure("personal-title-association", () => queries.Local("vn_search", new("Ｐｅｒｓｏｎａｌ Ａｌｉａｓ"))).Items).VndbId);
        var filtered = Measure("producer-language", () => store.BrowseCatalog(new(ProducerId: "p1", Language: "ja")));
        Assert.Equal(0, filtered.Total); // an unknown release language is not assumed from the novel's original language
        var summary = Measure("shelf-summary", () => store.ReadShelfSummary(pageSize: 24));
        Assert.Equal(shelfSize, summary.Total); Assert.Equal(24, summary.Items.Count); Assert.True(bytes < 128 * 1024);
        Assert.Equal(shelfSize, Measure("owned-shelf", () => store.ReadShelfSummary(isLocal: true)).Total);
        commands = bytes = 0; measuring.Value = true;
        var refreshWatch = Stopwatch.StartNew(); var refresh = await queries.QueryAsync("vn_get", new("", Mode: "explicit_refresh"), "v17", Ct); measuring.Value = false;
        measurements.Add(new { operation = "explicit-refresh", sql_commands = commands, materialized_text_bytes = bytes, upstream_calls = handler.Requests.Count, milliseconds = refreshWatch.Elapsed.TotalMilliseconds, response_bytes = JsonSerializer.SerializeToUtf8Bytes(refresh, VndbJson.Options).Length });
        Assert.Single(handler.Requests);
        Assert.Equal(0, bytes); Assert.Equal(0, commands); // empty upstream success must not preload every producer
        commands = bytes = 0; measuring.Value = true; var batchWatch = Stopwatch.StartNew();
        await importer.SaveFetchedManyAsync([StorageTests.Entry().Novel], Ct); measuring.Value = false;
        measurements.Add(new { operation = "batch-save-known-producers", sql_commands = commands, materialized_text_bytes = bytes, milliseconds = batchWatch.Elapsed.TotalMilliseconds });
        Assert.True(bytes < 16 * 1024); Assert.Single(handler.Requests);
        var output = Path.Combine(TestDirectory.ArtifactRoot, "query-read-cost"); Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "reads-" + shelfSize + "-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(new { novels = 67459, producers = 12000, shelfSize, measurements }, VndbJson.Options), Ct);
    }
}
