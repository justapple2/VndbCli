using Microsoft.Data.Sqlite;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    private static void InitializeCatalogIndex(SqliteConnection db, SqliteTransaction tx, int oldVersion)
    {
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS release_languages(release_id TEXT NOT NULL REFERENCES releases(id),language TEXT NOT NULL,mtl INTEGER NOT NULL,PRIMARY KEY(release_id,language));
            CREATE INDEX IF NOT EXISTS ix_release_language ON release_languages(language,mtl,release_id);
            CREATE TABLE IF NOT EXISTS release_platforms(release_id TEXT NOT NULL REFERENCES releases(id),platform TEXT NOT NULL,PRIMARY KEY(release_id,platform));
            CREATE INDEX IF NOT EXISTS ix_release_platform ON release_platforms(platform,release_id);
            CREATE TABLE IF NOT EXISTS release_roles(release_id TEXT NOT NULL REFERENCES releases(id),novel_id TEXT NOT NULL,rtype TEXT NOT NULL,PRIMARY KEY(release_id,novel_id));
            CREATE INDEX IF NOT EXISTS ix_release_role ON release_roles(rtype,novel_id,release_id);
            CREATE INDEX IF NOT EXISTS ix_release_patch ON releases(json_extract(metadata_json,'$.patch'),id);
            CREATE INDEX IF NOT EXISTS ix_catalog_updated ON novels(updated_at DESC,id);
            CREATE INDEX IF NOT EXISTS ix_catalog_title ON novels(title COLLATE NOCASE,id);
            CREATE INDEX IF NOT EXISTS ix_catalog_released ON novels(released,id);
            """);
        if (oldVersion < 13)
            Execute(db, tx, """
                DELETE FROM release_languages; DELETE FROM release_platforms; DELETE FROM release_roles;
                INSERT INTO release_languages SELECT r.id,json_extract(l.value,'$.lang'),json_extract(l.value,'$.mtl') FROM releases r,json_each(r.metadata_json,'$.languages') l;
                INSERT INTO release_platforms SELECT DISTINCT r.id,p.value FROM releases r,json_each(r.metadata_json,'$.platforms') p;
                INSERT INTO release_roles SELECT r.id,json_extract(v.value,'$.id'),json_extract(v.value,'$.rtype') FROM releases r,json_each(r.metadata_json,'$.vns') v;
                """);
        Execute(db, tx, "PRAGMA user_version=13");
    }
    private static void WriteReleaseIndex(SqliteConnection db, SqliteTransaction tx, string id, string json) => Execute(db, tx, """
        DELETE FROM release_languages WHERE release_id=$id; DELETE FROM release_platforms WHERE release_id=$id; DELETE FROM release_roles WHERE release_id=$id;
        INSERT INTO release_languages SELECT $id,json_extract(l.value,'$.lang'),json_extract(l.value,'$.mtl') FROM json_each($json,'$.languages') l;
        INSERT INTO release_platforms SELECT DISTINCT $id,p.value FROM json_each($json,'$.platforms') p;
        INSERT INTO release_roles SELECT $id,json_extract(v.value,'$.id'),json_extract(v.value,'$.rtype') FROM json_each($json,'$.vns') v;
        """, ("$id", id), ("$json", json));
}
