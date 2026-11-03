using Microsoft.Data.Sqlite;
using Vndb.Core;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    public const int SchemaVersion = 14;
    private static void InitializeReleases(SqliteConnection db, SqliteTransaction tx) => Execute(db, tx, """
        CREATE TABLE IF NOT EXISTS releases (
            id TEXT PRIMARY KEY, metadata_json TEXT NOT NULL CHECK(json_valid(metadata_json)), fetched_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS novel_releases (
            novel_id TEXT NOT NULL REFERENCES novels(id), release_id TEXT NOT NULL REFERENCES releases(id),
            PRIMARY KEY(novel_id,release_id));
        CREATE INDEX IF NOT EXISTS ix_novel_releases_release ON novel_releases(release_id,novel_id);
        PRAGMA user_version=11;
        """);

    private static LibraryEntry PreserveReleases(SqliteConnection db, SqliteTransaction tx, LibraryEntry entry)
    {
        if (entry.Novel.ReleaseCatalog is { } incoming) ReleaseRules.Validate(incoming, entry.Novel.Id);
        var previous = NovelEntry(db, tx, entry.Novel.Id)?.Novel;
        return entry with { Novel = entry.Novel with { ReleaseCatalog = ReleaseRules.Prefer(entry.Novel.ReleaseCatalog, previous?.ReleaseCatalog), Relations = entry.Novel.Relations ?? previous?.Relations } };
    }
    private static void WriteReleases(SqliteConnection db, SqliteTransaction tx, LibraryEntry entry)
    {
        if (entry.Novel.ReleaseCatalog is not { } catalog) return;
        Execute(db, tx, "DELETE FROM novel_releases WHERE novel_id=$id", ("$id", entry.Novel.Id));
        foreach (var release in catalog.Items)
        {
            Execute(db, tx, """
                INSERT INTO releases(id,metadata_json,fetched_at) VALUES($id,$json,$at)
                ON CONFLICT(id) DO UPDATE SET metadata_json=excluded.metadata_json,fetched_at=excluded.fetched_at
                WHERE excluded.fetched_at > releases.fetched_at;
                """, ("$id", release.Id), ("$json", Json(release)), ("$at", catalog.FetchedAt.ToUniversalTime().ToString("O")));
            using (var changed = Command(db, tx, "SELECT changes()"))
                if (Convert.ToInt32(changed.ExecuteScalar()) > 0) WriteReleaseIndex(db, tx, release.Id, Json(release));
            Execute(db, tx, "INSERT INTO novel_releases VALUES($vn,$release)", ("$vn", entry.Novel.Id), ("$release", release.Id));
        }
    }
}
