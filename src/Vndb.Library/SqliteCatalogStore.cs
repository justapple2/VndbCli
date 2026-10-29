using System.Text.Json;
using Vndb.Core;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore : ICatalogStore
{
    public IReadOnlyList<LocalGame> LocalGames()
    {
        using var db = Open(); using var command = Command(db, null, "SELECT record_json FROM local_games ORDER BY id");
        using var reader = command.ExecuteReader(); var rows = new List<LocalGame>();
        while (reader.Read()) rows.Add(JsonSerializer.Deserialize<LocalGame>(reader.GetString(0), VndbJson.Options)!);
        return rows.Select(ProjectLocal).ToArray();
    }
    public LocalGameImportResult ImportLocalGames(IReadOnlyList<LocalGameInput> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count is < 1 or > 5000) throw new ArgumentException("本地清单需要 1–5000 项。");
        var prepared = rows.Select(r => (Id: CatalogRules.LocalId(r), Details: r)).ToArray();
        if (prepared.DistinctBy(p => p.Id).Count() != rows.Count) throw new ArgumentException("本地目录清单有重复路径。");
        using var db = Open(); using var transaction = db.BeginTransaction(); var inserted = 0;
        foreach (var item in prepared)
        {
            using var find = Command(db, transaction, "SELECT 1 FROM local_games WHERE id=$id", ("$id", item.Id));
            if (find.ExecuteScalar() is not null) continue;
            var now = DateTimeOffset.UtcNow;
            var record = new LocalGame(item.Id, item.Details, null, 1, now, now);
            var json = JsonSerializer.Serialize(record, VndbJson.Options);
            Execute(db, transaction, "INSERT INTO local_games VALUES($id,NULL,$machine,$path,$title,$json,1,$created,$updated)",
                ("$id", item.Id), ("$machine", item.Details.Machine), ("$path", item.Details.Path), ("$title", item.Details.Title),
                ("$json", json), ("$created", now.ToString("O")), ("$updated", now.ToString("O")));
            Execute(db, transaction, "INSERT INTO catalog_events(entity_id,kind,recorded_at,snapshot_json) VALUES($id,'local-import',$at,$json)",
                ("$id", item.Id), ("$at", now.ToString("O")), ("$json", json));
            SeedLocal(db, transaction, record);
            inserted++;
        }
        transaction.Commit(); return new(inserted, rows.Count - inserted, LocalGames().Count);
    }
    public LocalGame LinkLocalGame(string id, string novelId, long revision)
    {
        novelId = VndbId.Normalize(novelId, 'v');
        if (Get(novelId) is null) throw new ArgumentException("请先收录作品资料。");
        using var db = Open(); using var transaction = db.BeginTransaction();
        using var find = Command(db, transaction, "SELECT record_json FROM local_games WHERE id=$id", ("$id", id));
        var previous = find.ExecuteScalar() is string text ? JsonSerializer.Deserialize<LocalGame>(text, VndbJson.Options)! :
            throw new ArgumentException("本地目录记录不存在。");
        var result = LinkLegacyLocal(db, transaction, previous, novelId, revision);
        transaction.Commit(); return result;
    }
    public IReadOnlyList<CatalogLookup> CatalogLookups()
    {
        using var db = Open(); using var command = Command(db, null, "SELECT lookup_json FROM catalog_lookups ORDER BY key");
        using var reader = command.ExecuteReader(); var rows = new List<CatalogLookup>();
        while (reader.Read()) rows.Add(JsonSerializer.Deserialize<CatalogLookup>(reader.GetString(0), VndbJson.Options)!);
        return rows;
    }
    public void SaveCatalogLookup(CatalogLookup lookup)
    {
        if (lookup.Key != CatalogRules.Key(lookup.Title) || lookup.Status is not ("matched" or "needs_review" or "not_found" or "error") ||
            string.IsNullOrWhiteSpace(lookup.Query) || lookup.Query.Length > 200 || lookup.Candidates.Count > 20 ||
            (lookup.Status == "matched" && lookup.NovelId is null) ||
            (lookup.NovelId is not null && Get(lookup.NovelId) is null)) throw new ArgumentException("资料查询记录无效。");
        var json = JsonSerializer.Serialize(lookup, VndbJson.Options);
        using var db = Open(); using var transaction = db.BeginTransaction();
        Execute(db, transaction, """
            INSERT INTO catalog_lookups VALUES($key,$title,$status,$vn,$json,$at)
            ON CONFLICT(key) DO UPDATE SET title=excluded.title,status=excluded.status,novel_id=excluded.novel_id,
                lookup_json=excluded.lookup_json,checked_at=excluded.checked_at
            """, ("$key", lookup.Key), ("$title", lookup.Title), ("$status", lookup.Status), ("$vn", lookup.NovelId),
            ("$json", json), ("$at", lookup.CheckedAt.ToString("O")));
        Execute(db, transaction, "INSERT INTO catalog_events(entity_id,kind,recorded_at,snapshot_json) VALUES($id,'lookup',$at,$json)",
            ("$id", lookup.Key), ("$at", lookup.CheckedAt.ToString("O")), ("$json", json));
        transaction.Commit();
    }
}
