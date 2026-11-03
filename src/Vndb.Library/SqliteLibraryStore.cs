using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vndb.Core;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore : ILibraryStore
{
    private readonly string _connectionString;
    public string DataDirectory { get; }
    public SqliteLibraryStore(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DataDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DataDirectory, "library.db"), Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true, DefaultTimeout = 30, Pooling = false
        }.ToString();
        using var connection = Open();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        var oldVersion = Convert.ToInt32(version.ExecuteScalar());
        if (oldVersion > SchemaVersion) throw new InvalidDataException("数据库版本高于本程序支持版本，请勿降级写入。");
        Execute(connection, null, "PRAGMA journal_mode=WAL;");
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS novels (
                id TEXT PRIMARY KEY, title TEXT NOT NULL, alt_title TEXT, released TEXT, original_language TEXT,
                rating REAL, search_text TEXT NOT NULL, entry_json TEXT NOT NULL CHECK(json_valid(entry_json)),
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL, cover_hash TEXT,
                cover_status TEXT NOT NULL CHECK(cover_status IN ('ready','none','failed')));
            CREATE TABLE IF NOT EXISTS producers (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, original TEXT, kind TEXT, lang TEXT,
                metadata_json TEXT NOT NULL CHECK(json_valid(metadata_json)), updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS novel_developers (
                novel_id TEXT NOT NULL REFERENCES novels(id) ON DELETE CASCADE,
                producer_id TEXT NOT NULL REFERENCES producers(id), ordinal INTEGER NOT NULL,
                PRIMARY KEY(novel_id,producer_id));
            CREATE INDEX IF NOT EXISTS ix_developers_producer ON novel_developers(producer_id,novel_id);
            CREATE TABLE IF NOT EXISTS novel_titles (
                novel_id TEXT NOT NULL REFERENCES novels(id) ON DELETE CASCADE,
                lang TEXT NOT NULL, title TEXT NOT NULL, latin TEXT, official INTEGER NOT NULL, main INTEGER NOT NULL,
                PRIMARY KEY(novel_id,lang));
            CREATE TABLE IF NOT EXISTS assets (
                hash TEXT PRIMARY KEY, relative_path TEXT NOT NULL, content_type TEXT NOT NULL,
                size_bytes INTEGER NOT NULL, source_url TEXT NOT NULL, downloaded_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS import_events (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT, novel_id TEXT NOT NULL,
                imported_at TEXT NOT NULL, cover_status TEXT NOT NULL, cover_error TEXT, snapshot_sha256 TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS archive_locations (
                id TEXT PRIMARY KEY, novel_id TEXT NOT NULL REFERENCES novels(id),
                selected_producer_id TEXT NOT NULL REFERENCES producers(id), machine TEXT NOT NULL,
                original_path TEXT NOT NULL, current_path TEXT NOT NULL, run_id TEXT NOT NULL,
                plan_sha256 TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN ('planned','archived','restored')),
                location_json TEXT NOT NULL CHECK(json_valid(location_json)), created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_archive_novel ON archive_locations(novel_id);
            CREATE INDEX IF NOT EXISTS ix_archive_run ON archive_locations(run_id);
            CREATE TABLE IF NOT EXISTS archive_events (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT, location_id TEXT NOT NULL REFERENCES archive_locations(id),
                recorded_at TEXT NOT NULL, state TEXT NOT NULL, snapshot_json TEXT NOT NULL CHECK(json_valid(snapshot_json)));
            CREATE TABLE IF NOT EXISTS play_records (
                id TEXT PRIMARY KEY, novel_id TEXT REFERENCES novels(id), title TEXT NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('not_started','playing','played','completed','on_hold','dropped')),
                started_on TEXT, finished_on TEXT, duration_minutes INTEGER CHECK(duration_minutes >= 0),
                score REAL CHECK(score >= 0 AND score <= 10), source_hash TEXT, source_sheet TEXT, source_row INTEGER,
                record_json TEXT NOT NULL CHECK(json_valid(record_json)), revision INTEGER NOT NULL,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
                UNIQUE(source_hash,source_sheet,source_row));
            CREATE INDEX IF NOT EXISTS ix_play_novel ON play_records(novel_id);
            CREATE INDEX IF NOT EXISTS ix_play_status_dates ON play_records(status,finished_on,started_on);
            CREATE TABLE IF NOT EXISTS play_import_batches (
                source_sha256 TEXT PRIMARY KEY, file_name TEXT NOT NULL, row_count INTEGER NOT NULL,
                payload_sha256 TEXT NOT NULL, imported_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS play_events (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT, play_id TEXT NOT NULL REFERENCES play_records(id),
                kind TEXT NOT NULL, recorded_at TEXT NOT NULL, snapshot_json TEXT NOT NULL CHECK(json_valid(snapshot_json)));
            CREATE TABLE IF NOT EXISTS local_games (
                id TEXT PRIMARY KEY, novel_id TEXT REFERENCES novels(id), machine TEXT NOT NULL, path TEXT NOT NULL,
                title TEXT NOT NULL, record_json TEXT NOT NULL CHECK(json_valid(record_json)), revision INTEGER NOT NULL,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_local_novel ON local_games(novel_id);
            CREATE TABLE IF NOT EXISTS catalog_lookups (
                key TEXT PRIMARY KEY, title TEXT NOT NULL, status TEXT NOT NULL, novel_id TEXT REFERENCES novels(id),
                lookup_json TEXT NOT NULL CHECK(json_valid(lookup_json)), checked_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS catalog_events (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT, entity_id TEXT NOT NULL, kind TEXT NOT NULL,
                recorded_at TEXT NOT NULL, snapshot_json TEXT NOT NULL CHECK(json_valid(snapshot_json)));
            CREATE TABLE IF NOT EXISTS catalog_jobs (
                id TEXT PRIMARY KEY, status TEXT NOT NULL, active INTEGER NOT NULL CHECK(active IN (0,1)),
                record_json TEXT NOT NULL CHECK(json_valid(record_json)), created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_catalog_one_active_job ON catalog_jobs(active) WHERE active=1;
            CREATE TABLE IF NOT EXISTS library_searches (
                key TEXT PRIMARY KEY, record_json TEXT NOT NULL CHECK(json_valid(record_json)), updated_at TEXT NOT NULL);
            """);
        InitializeM2(connection, transaction, oldVersion);
        InitializeM3(connection, transaction, oldVersion);
        InitializeM4(connection, transaction);
        InitializeM6(connection, transaction);
        InitializePresence(connection, transaction);
        InitializeReleases(connection, transaction);
        InitializeDatasets(connection, transaction);
        InitializeCatalogIndex(connection, transaction, oldVersion);
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS personal_organization(id INTEGER PRIMARY KEY CHECK(id=1),record_json TEXT NOT NULL CHECK(json_valid(record_json))); PRAGMA user_version=14;");
        transaction.Commit();
    }
    public LibraryEntry? Get(string id)
    {
        id = VndbId.Normalize(id, 'v');
        using var connection = Open();
        using var command = Command(connection, null, "SELECT entry_json FROM novels WHERE id=$id", ("$id", id));
        return command.ExecuteScalar() is string json ? Deserialize(json) : null;
    }
    public void Save(LibraryEntry entry)
        => SaveMetadataBatch([entry]);
    public void SaveMetadataBatch(IReadOnlyList<LibraryEntry> entries)
    {
        if (entries.Count is < 1 or > 100) throw new ArgumentException("资料写入批次须为1–100项。");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var input in entries)
        {
            var entry = input;
            var before = NovelEntry(connection, transaction, entry.Novel.Id);
            entry = WriteLibraryEntry(connection, transaction, entry);
            WriteProvenance(connection, transaction, entry.Novel.Id, entry.Provenance, entry.UpdatedAt, entry.Completeness, null,
                entry.Provenance == "web_vndb" ? entry.UpdatedAt : null);
            Event(connection, transaction, entry.Novel.Id, "metadata-save", before is null ? null : Json(before), Json(entry));
        }
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS personal_organization(id INTEGER PRIMARY KEY CHECK(id=1),record_json TEXT NOT NULL CHECK(json_valid(record_json))); PRAGMA user_version=14;");
        transaction.Commit();
    }
    public void SaveEnrichment(LibraryEntry entry, long expectedRevision, IReadOnlyList<Producer> originalProducers)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var get = Command(db, tx, "SELECT revision,batch_id FROM metadata_provenance WHERE vndb_id=$id", ("$id", entry.Novel.Id));
        string? batch;
        using (var reader = get.ExecuteReader())
        {
            if (!reader.Read() || reader.GetInt64(0) != expectedRevision) throw new PlayConflictException("补全期间资料已变更，保留当前版本并重试。");
            batch = reader.IsDBNull(1) ? null : reader.GetString(1);
        }
        var producers = entry.Producers.Select(p =>
        {
            using var current = Command(db, tx, "SELECT metadata_json FROM producers WHERE id=$id", ("$id", p.Id));
            var json = current.ExecuteScalar() as string;
            var original = originalProducers.FirstOrDefault(o => o.Id == p.Id);
            return json is not null && json != Json(original) ? Read<Producer>(json) : p;
        }).ToArray();
        entry = entry with { Producers = producers };
        var before = NovelEntry(db, tx, entry.Novel.Id);
        entry = WriteLibraryEntry(db, tx, entry);
        WriteProvenance(db, tx, entry.Novel.Id, entry.Provenance, entry.UpdatedAt, entry.Completeness, batch, null);
        Event(db, tx, entry.Novel.Id, "metadata-enrichment", before is null ? null : Json(before), Json(entry)); tx.Commit();
    }
    private static LibraryEntry WriteLibraryEntry(SqliteConnection connection, SqliteTransaction transaction, LibraryEntry entry)
    {
        entry = PreserveReleases(connection, transaction, entry);
        var id = VndbId.Normalize(entry.Novel.Id, 'v');
        var json = JsonSerializer.Serialize(entry, VndbJson.Options);
        Execute(connection, transaction, """
            INSERT INTO novels(id,title,alt_title,released,original_language,rating,search_text,entry_json,created_at,updated_at,cover_hash,cover_status)
            VALUES($id,$title,$alt,$released,$olang,$rating,$search,$json,$created,$updated,$cover,$status)
            ON CONFLICT(id) DO UPDATE SET title=excluded.title,alt_title=excluded.alt_title,released=excluded.released,
                original_language=excluded.original_language,rating=excluded.rating,search_text=excluded.search_text,
                entry_json=excluded.entry_json,updated_at=excluded.updated_at,cover_hash=excluded.cover_hash,cover_status=excluded.cover_status;
            """, ("$id", id), ("$title", entry.Novel.Title), ("$alt", entry.Novel.AltTitle),
            ("$released", entry.Novel.Released), ("$olang", entry.Novel.OriginalLanguage), ("$rating", entry.Novel.Rating),
            ("$search", LibraryRules.SearchText(entry.Novel)), ("$json", json), ("$created", entry.CreatedAt.ToString("O")),
            ("$updated", entry.UpdatedAt.ToString("O")), ("$cover", entry.Cover?.Hash), ("$status", entry.CoverStatus));
        Execute(connection, transaction, "DELETE FROM novel_developers WHERE novel_id=$id; DELETE FROM novel_titles WHERE novel_id=$id", ("$id", id));
        for (var i = 0; i < entry.Producers.Count; i++)
        {
            var p = entry.Producers[i];
            using var oldProducer = Command(connection, transaction, "SELECT metadata_json FROM producers WHERE id=$id", ("$id", p.Id));
            var changed = oldProducer.ExecuteScalar() as string != Json(p);
            Execute(connection, transaction, """
                INSERT INTO producers(id,name,original,kind,lang,metadata_json,updated_at) VALUES($id,$name,$original,$kind,$lang,$json,$updated)
                ON CONFLICT(id) DO UPDATE SET name=excluded.name,original=excluded.original,kind=excluded.kind,
                    lang=excluded.lang,metadata_json=excluded.metadata_json,
                    updated_at=CASE WHEN producers.metadata_json=excluded.metadata_json THEN producers.updated_at ELSE excluded.updated_at END;
                """, ("$id", p.Id), ("$name", p.Name), ("$original", p.Original), ("$kind", p.Type),
                ("$lang", p.Lang), ("$json", JsonSerializer.Serialize(p, VndbJson.Options)), ("$updated", entry.UpdatedAt.ToString("O")));
            if (changed) WriteProvenance(connection, transaction, p.Id, entry.Provenance, entry.UpdatedAt, entry.Completeness, null, entry.Provenance == "web_vndb" ? entry.UpdatedAt : null);
            Execute(connection, transaction, "INSERT INTO novel_developers VALUES($vn,$producer,$ordinal)", ("$vn", id), ("$producer", p.Id), ("$ordinal", i));
        }
        foreach (var t in entry.Novel.Titles)
            Execute(connection, transaction, "INSERT INTO novel_titles VALUES($vn,$lang,$title,$latin,$official,$main)",
                ("$vn", id), ("$lang", t.Lang), ("$title", t.Title), ("$latin", t.Latin), ("$official", t.Official), ("$main", t.Main));
        if (entry.Cover is { } a)
            Execute(connection, transaction, "INSERT INTO assets VALUES($hash,$path,$type,$size,$url,$at) ON CONFLICT(hash) DO NOTHING",
                ("$hash", a.Hash), ("$path", a.RelativePath), ("$type", a.ContentType), ("$size", a.SizeBytes), ("$url", a.SourceUrl), ("$at", a.DownloadedAt.ToString("O")));
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
        Execute(connection, transaction, "INSERT INTO import_events(novel_id,imported_at,cover_status,cover_error,snapshot_sha256) VALUES($id,$at,$status,$error,$hash)",
            ("$id", id), ("$at", entry.UpdatedAt.ToString("O")), ("$status", entry.CoverStatus), ("$error", entry.CoverError), ("$hash", hash));
        WriteReleases(connection, transaction, entry);
        return entry;
    }
    public LibraryPage List(string? query = null, string? producer = null, int page = 1, int limit = 24)
    {
        LibraryRules.ValidatePage(page, limit);
        producer = string.IsNullOrWhiteSpace(producer) ? null : VndbId.Normalize(producer, 'p');
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        const string where = "WHERE ($q='' OR instr(n.search_text,$q)>0) AND ($producer='' OR EXISTS(SELECT 1 FROM novel_developers d WHERE d.novel_id=n.id AND d.producer_id=$producer))";
        (string, object?)[] filter = [("$q", query?.Trim().ToLowerInvariant() ?? ""), ("$producer", producer ?? "")];
        using var count = Command(connection, transaction, "SELECT count(*) FROM novels n " + where, filter);
        var total = Convert.ToInt32(count.ExecuteScalar());
        var personal = !string.IsNullOrWhiteSpace(query);
        using var select = Command(connection, transaction, (personal ? PersonalOrderCte : "") + "SELECT n.entry_json FROM novels n " + (personal ? PersonalOrderJoin : "") + where + " ORDER BY " + (personal ? PersonalOrder : "n.updated_at DESC,n.id") + " LIMIT $limit OFFSET $offset",
            [.. filter, ("$limit", limit), ("$offset", (page - 1) * limit)]);
        using var reader = select.ExecuteReader();
        var entries = new List<LibraryEntry>();
        while (reader.Read()) entries.Add(Deserialize(reader.GetString(0)));
        reader.Close();
        transaction.Commit();
        return new(entries, page, limit, total, (long)page * limit < total);
    }
    public IReadOnlyList<ProducerCount> Producers()
    {
        using var connection = Open();
        using var command = Command(connection, null, "SELECT p.id,p.name,p.original,count(*) FROM producers p JOIN novel_developers d ON d.producer_id=p.id GROUP BY p.id ORDER BY p.name");
        using var reader = command.ExecuteReader();
        var result = new List<ProducerCount>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3)));
        return result;
    }
    public LibraryStatistics Statistics()
    {
        using var connection = Open();
        using var command = Command(connection, null, "SELECT count(*), (SELECT count(DISTINCT producer_id) FROM novel_developers), count(cover_hash), sum(cover_status='failed') FROM novels");
        using var reader = command.ExecuteReader();
        reader.Read();
        return new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.IsDBNull(3) ? 0 : reader.GetInt32(3));
    }
    public void Backup(string destination)
    {
        if (File.Exists(destination)) throw new IOException("备份文件已经存在。");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        using var source = Open();
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        target.Open();
        source.BackupDatabase(target);
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
    private static LibraryEntry Deserialize(string json) => JsonSerializer.Deserialize<LibraryEntry>(json, VndbJson.Options)
        ?? throw new InvalidDataException("资料库记录损坏。");
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        command.ExecuteNonQuery();
    }
}
