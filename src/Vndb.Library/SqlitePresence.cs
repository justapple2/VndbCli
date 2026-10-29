using Vndb.Contracts;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    private static void InitializePresence(Microsoft.Data.Sqlite.SqliteConnection db, Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS local_presence(device_id TEXT NOT NULL,item_id TEXT NOT NULL REFERENCES library_items(id),is_local INTEGER NOT NULL CHECK(is_local IN(0,1)),PRIMARY KEY(device_id,item_id));
            CREATE TABLE IF NOT EXISTS presence_heads(device_id TEXT PRIMARY KEY,revision INTEGER NOT NULL);
            PRAGMA user_version=10;
            """);
    }
    public IReadOnlyList<string> PresentItemIds(string? deviceId = null)
    {
        using var db = Open(); using var query = Command(db, null, "SELECT DISTINCT item_id FROM local_presence WHERE is_local=1 AND ($d IS NULL OR device_id=$d)", ("$d", deviceId));
        using var reader = query.ExecuteReader(); var rows = new List<string>(); while(reader.Read()) rows.Add(reader.GetString(0)); return rows;
    }
    public PresenceHead PresenceHead(string deviceId)
    {
        using var db = Open();
        using var head = Command(db, null, "SELECT revision FROM presence_heads WHERE device_id=$d", ("$d", deviceId));
        var revision = Convert.ToInt64(head.ExecuteScalar() ?? 0L);
        using var query = Command(db, null, "SELECT item_id,is_local FROM local_presence WHERE device_id=$d ORDER BY item_id", ("$d", deviceId));
        using var reader = query.ExecuteReader(); var rows = new List<PresenceFlag>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetBoolean(1)));
        return new(deviceId, revision, rows);
    }
    public IReadOnlyList<PublicPresence> PublicPresence()
    {
        using var db = Open();
        using var query = Command(db, null, "SELECT i.novel_id,max(p.is_local) FROM local_presence p JOIN library_items i ON i.id=p.item_id WHERE i.novel_id IS NOT NULL AND json_extract(i.record_json,'$.deleted_at') IS NULL GROUP BY i.novel_id ORDER BY i.novel_id");
        using var reader = query.ExecuteReader(); var rows = new List<PublicPresence>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetBoolean(1))); return rows;
    }
    public PresenceReceipt SavePresence(PresenceBatch batch, string instanceId, string key)
    {
        if (batch.Format != "vn-library-presence" || batch.FormatVersion != 1 || !Guid.TryParseExact(batch.Id, "D", out _) ||
            !Guid.TryParseExact(batch.DeviceId, "D", out _) || batch.Items is null || batch.Items.Count is < 1 or > 2000 ||
            batch.Items.Any(i => i is null || string.IsNullOrEmpty(i.ItemId)) || batch.Items.Select(i => i.ItemId).Distinct().Count() != batch.Items.Count)
            throw new ArgumentException("无效的本地存在标记批次。");
        if (batch.ServiceInstanceId != instanceId) throw new PlayConflictException("服务实例不符，原JSON保留。");
        return Mutate("presence/" + batch.DeviceId, key, batch, (db, tx) =>
        {
            using var head = Command(db, tx, "SELECT revision FROM presence_heads WHERE device_id=$d", ("$d", batch.DeviceId));
            var revision = Convert.ToInt64(head.ExecuteScalar() ?? 0L);
            if (revision != batch.ExpectedRevision) throw new PlayConflictException("本地标记修订冲突，请重新核对。");
            foreach (var row in batch.Items)
            {
                if (Item(db, tx, row.ItemId).DeletedAt is not null) throw new PlayConflictException("书架条目已移除。");
                Execute(db, tx, "INSERT INTO local_presence VALUES($d,$i,$v) ON CONFLICT(device_id,item_id) DO UPDATE SET is_local=excluded.is_local", ("$d", batch.DeviceId), ("$i", row.ItemId), ("$v", row.IsLocal));
            }
            Execute(db, tx, "INSERT INTO presence_heads VALUES($d,$r) ON CONFLICT(device_id) DO UPDATE SET revision=excluded.revision", ("$d", batch.DeviceId), ("$r", revision + 1));
            return new PresenceReceipt(batch.Id, instanceId, ArchiveFormat.Hash(batch), revision + 1);
        });
    }
    public void EnsureWebPrivacy()
    {
        using var db = Open();
        foreach (var table in new[] { "installations", "installation_components", "installation_legacy_links", "archive_locations", "archive_events", "archive_runs", "archive_evidence", "local_games", "device_legacy_names" })
        {
            using var query = Command(db, null, "SELECT count(*) FROM " + table);
            if (Convert.ToInt64(query.ExecuteScalar()) > 0) throw new InvalidDataException("旧位置资料尚未本机封存／迁移，拒绝启动Web；请执行本地边界迁移流程。");
        }
    }
}
