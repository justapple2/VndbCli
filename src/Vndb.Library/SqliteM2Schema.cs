using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, VndbJson.Options);
    private static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, VndbJson.Options) ?? throw new InvalidDataException("记录损坏。");
    private static void InitializeM2(SqliteConnection db, SqliteTransaction tx, int oldVersion)
    {
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS library_items(id TEXT PRIMARY KEY,novel_id TEXT REFERENCES novels(id),record_json TEXT NOT NULL CHECK(json_valid(record_json)));
            CREATE INDEX IF NOT EXISTS ix_item_novel ON library_items(novel_id);
            CREATE INDEX IF NOT EXISTS ix_item_shelf_summary ON library_items((json_extract(record_json,'$.deleted_at') IS NOT NULL),json_extract(record_json,'$.updated_at') DESC,id);
            CREATE TABLE IF NOT EXISTS play_item_links(play_id TEXT PRIMARY KEY REFERENCES play_records(id),item_id TEXT NOT NULL REFERENCES library_items(id));
            CREATE INDEX IF NOT EXISTS ix_play_item ON play_item_links(item_id);
            CREATE TABLE IF NOT EXISTS operation_receipts(scope TEXT NOT NULL,key TEXT NOT NULL,payload_sha TEXT NOT NULL,result_json TEXT NOT NULL,PRIMARY KEY(scope,key));
            CREATE TABLE IF NOT EXISTS business_events(sequence INTEGER PRIMARY KEY AUTOINCREMENT,entity_id TEXT NOT NULL,kind TEXT NOT NULL,before_json TEXT,after_json TEXT,recorded_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS metadata_provenance(vndb_id TEXT PRIMARY KEY,source TEXT NOT NULL,fetched_at TEXT NOT NULL,completeness TEXT NOT NULL,revision INTEGER NOT NULL,batch_id TEXT,verified_at TEXT);
            CREATE TABLE IF NOT EXISTS metadata_candidates(id TEXT PRIMARY KEY,record_json TEXT NOT NULL CHECK(json_valid(record_json)));
            CREATE TABLE IF NOT EXISTS metadata_batches(id TEXT PRIMARY KEY,payload_sha TEXT NOT NULL,raw_sha TEXT NOT NULL,client_id TEXT,add_to_library INTEGER NOT NULL,state TEXT NOT NULL,created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS metadata_batch_aliases(client_id TEXT PRIMARY KEY,batch_id TEXT NOT NULL REFERENCES metadata_batches(id),payload_sha TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS metadata_import_items(batch_id TEXT NOT NULL REFERENCES metadata_batches(id),ordinal INTEGER NOT NULL,input_json TEXT NOT NULL,result_json TEXT NOT NULL,status TEXT NOT NULL,PRIMARY KEY(batch_id,ordinal));
            CREATE TABLE IF NOT EXISTS metadata_operations(operation_id TEXT PRIMARY KEY,result_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS metadata_jobs(id TEXT PRIMARY KEY,record_json TEXT NOT NULL CHECK(json_valid(record_json)));
            PRAGMA user_version=6;
            """);
        if (oldVersion >= 6) return;
        // Only additive projections are created: original personal/source/archive rows stay byte-for-byte intact.
        var novels = new Dictionary<string, LibraryEntry>(StringComparer.Ordinal);
        using (var select = Command(db, tx, "SELECT entry_json FROM novels"))
        using (var reader = select.ExecuteReader())
            while (reader.Read()) { var n = Read<LibraryEntry>(reader.GetString(0)); novels[n.Novel.Id] = n; }
        foreach (var n in novels.Values) WriteProvenance(db, tx, n.Novel.Id, "web_vndb", n.UpdatedAt, "full_text", null, n.UpdatedAt);
        var producers = new List<(string Id, DateTimeOffset At)>();
        using (var select = Command(db, tx, "SELECT id,updated_at FROM producers"))
        using (var reader = select.ExecuteReader()) while (reader.Read()) producers.Add((reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture)));
        foreach (var p in producers) WriteProvenance(db, tx, p.Id, "web_vndb", p.At, "full_text", null, p.At);
        var plays = new List<PlayRecord>();
        using (var select = Command(db, tx, "SELECT record_json FROM play_records"))
        using (var reader = select.ExecuteReader()) while (reader.Read()) plays.Add(ReadPlay(reader.GetString(0)));
        foreach (var play in plays) EnsurePlayItem(db, tx, play);
        var known = new List<string>();
        using (var select = Command(db, tx, "SELECT novel_id FROM archive_locations UNION SELECT novel_id FROM local_games WHERE novel_id IS NOT NULL"))
        using (var reader = select.ExecuteReader()) while (reader.Read()) known.Add(reader.GetString(0));
        foreach (var id in known) EnsureNovelItem(db, tx, id, novels[id].Novel.Title);
    }
    private static string EnsureNovelItem(SqliteConnection db, SqliteTransaction tx, string novelId, string title)
    {
        using var find = Command(db, tx, "SELECT id FROM library_items WHERE novel_id=$vn ORDER BY id LIMIT 1", ("$vn", novelId));
        if (find.ExecuteScalar() is string id) return id; // Includes removed items: reimport must not resurrect them.
        var now = DateTimeOffset.UtcNow;
        var item = new LibraryItem("item-" + Guid.NewGuid().ToString("N"), new(title, novelId), 1, now, now);
        WriteItem(db, tx, item, "item-seed");
        return item.Id;
    }
    private static void EnsurePlayItem(SqliteConnection db, SqliteTransaction tx, PlayRecord play)
    {
        using var find = Command(db, tx, "SELECT i.record_json FROM play_item_links l JOIN library_items i ON i.id=l.item_id WHERE l.play_id=$id", ("$id", play.Id));
        if (find.ExecuteScalar() is string json && Read<LibraryItem>(json).Details.NovelId == play.Details.NovelId) return;
        string itemId;
        if (play.Details.NovelId is { } novelId) itemId = EnsureNovelItem(db, tx, novelId, play.Details.Title);
        else
        {
            itemId = "item-" + play.Id;
            using var exists = Command(db, tx, "SELECT 1 FROM library_items WHERE id=$id", ("$id", itemId));
            if (exists.ExecuteScalar() is null)
                WriteItem(db, tx, new(itemId, new(play.Details.Title), 1, play.CreatedAt, play.UpdatedAt), "item-seed");
        }
        Execute(db, tx, "INSERT INTO play_item_links VALUES($play,$item) ON CONFLICT(play_id) DO UPDATE SET item_id=excluded.item_id", ("$play", play.Id), ("$item", itemId));
    }
    private static void Event(SqliteConnection db, SqliteTransaction tx, string id, string kind, string? before, string? after) =>
        Execute(db, tx, "INSERT INTO business_events(entity_id,kind,before_json,after_json,recorded_at) VALUES($id,$kind,$before,$after,$at)",
            ("$id", id), ("$kind", kind), ("$before", before), ("$after", after), ("$at", DateTimeOffset.UtcNow.ToString("O")));
    private static void WriteItem(SqliteConnection db, SqliteTransaction tx, LibraryItem item, string kind)
    {
        using var get = Command(db, tx, "SELECT record_json FROM library_items WHERE id=$id", ("$id", item.Id));
        var before = get.ExecuteScalar() as string;
        Execute(db, tx, "INSERT INTO library_items VALUES($id,$vn,$json) ON CONFLICT(id) DO UPDATE SET novel_id=excluded.novel_id,record_json=excluded.record_json",
            ("$id", item.Id), ("$vn", item.Details.NovelId), ("$json", Json(item)));
        Event(db, tx, item.Id, kind, before, Json(item));
    }
    private static void WriteProvenance(SqliteConnection db, SqliteTransaction tx, string id, string source, DateTimeOffset at,
        string completeness, string? batchId, DateTimeOffset? verified) => Execute(db, tx, """
            INSERT INTO metadata_provenance VALUES($id,$source,$at,$complete,1,$batch,$verified)
            ON CONFLICT(vndb_id) DO UPDATE SET source=excluded.source,fetched_at=excluded.fetched_at,completeness=excluded.completeness,
            revision=metadata_provenance.revision+1,batch_id=excluded.batch_id,verified_at=excluded.verified_at
            """, ("$id", id), ("$source", source), ("$at", at.ToString("O")), ("$complete", completeness), ("$batch", batchId), ("$verified", verified?.ToString("O")));
}
