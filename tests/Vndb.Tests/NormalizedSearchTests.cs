using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vndb.Core;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

public sealed class NormalizedSearchTests
{
    [Fact]
    public void NormalizedMissReadsOnlyTitlesThenSelectedDetailsAndKeepsFiltersAndTitleBoundaries()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        var entry = StorageTests.Entry();
        store.Save(entry with { Novel = entry.Novel with { Title = "Primary★Title", AltTitle = "Alternate★Name",
            Aliases = ["Alias★Needle", "Unsafe\nNeedle", "Left", "Right"],
            Titles = [new() { Lang = "ja", Title = "日本＊作品", Latin = "Sp ace d" }] } });
        var baseGame = entry with { Novel = entry.Novel with { Id = "v18", Title = "Base", AltTitle = null, Aliases = [], Titles = [] } };
        store.Save(baseGame); store.Save(baseGame with { Novel = baseGame.Novel with { Id = "v19", Title = "Base＋" } });
        // More than one old page, with valid JSON but an unrelated detail that cannot be
        // deserialised as a novel. A title miss must never load that full detail.
        using (var db = new SqliteConnection("Pooling=False;Data Source=" + Path.Combine(dir.Path, "library.db")))
        {
            db.Open(); using var tx = db.BeginTransaction(); using var insert = db.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO novels SELECT $id,'Unrelated',NULL,released,original_language,rating,'unrelated',
                  json_set(entry_json,'$.novel.id',$id,'$.novel.title','Unrelated','$.novel.alt_title',NULL,
                    '$.novel.aliases',json('[]'),'$.novel.titles',json('[]'),'$.novel.release_catalog','unread-detail'),
                  created_at,updated_at,cover_hash,cover_status FROM novels WHERE id='v17'
                """;
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            for (var i = 1000; i < 2005; i++) { id.Value = "v" + i; insert.ExecuteNonQuery(); }
            tx.Commit();
        }
        Assert.Throws<JsonException>(() => store.Get("v1000"));
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("Local search must not access upstream"));
        using var http = new HttpClient(handler); using var client = new VndbClient(http, VndbClientTests.FastOptions);
        using var importer = new LibraryImporter(client, new(http, dir.Path), store);
        using var catalog = new CatalogEnricher(client, importer, store, store);
        using var queries = new CatalogQueries(store, catalog, store, client, importer, TimeProvider.System);
        foreach (var query in new[] { "PrimaryTitle", "AlternateName", "AliasNeedle", "日本作品", "Spaced" })
        {
            var found = queries.Local(new(query));
            Assert.Equal("v17", Assert.Single(found.Results).Novel.Id); Assert.False(found.NeedsRemote);
            Assert.Empty(queries.Local(new(query, Producer: "p999")).Results);
        }
        Assert.Equal("v19", Assert.Single(queries.Local(new("Base ＋")).Results).Novel.Id);
        foreach (var query in new[] { "NoSuchSyntheticTitle", "LeftRight", "UnsafeNeedle" })
        {
            var miss = queries.Local(new(query)); Assert.Empty(miss.Results); Assert.True(miss.NeedsRemote);
        }
        var beyond = queries.Local(new("AliasNeedle", Page: 2, Limit: 1));
        Assert.Equal(1, beyond.Total); Assert.Empty(beyond.Results); Assert.False(beyond.NeedsRemote);
        Assert.Empty(handler.Requests);
    }
}
