using System.Text;
using Vndb.Contracts;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    public static string ActivityKey(string device, string kind, string id) => "act-" + PlayRules.Hash(Encoding.UTF8.GetBytes(device + "/" + kind + "/" + id));
    public string? ActivityMapping(string device, string kind, string clientId, string? hash = null, string? legacyHash = null)
    {
        using var db = Open(); using var query = Command(db, null, "SELECT payload_sha,server_id FROM activity_mappings WHERE device_id=$d AND kind=$k AND client_id=$id", ("$d", device), ("$k", kind), ("$id", clientId));
        using var reader = query.ExecuteReader(); if (!reader.Read()) return null;
        if (hash is not null && reader.GetString(0) != hash && reader.GetString(0) != legacyHash) throw new PlayConflictException("同一客户端创建键对应不同资料。");
        return reader.GetString(1);
    }
    public void SaveActivityMapping(string device, string kind, string clientId, string hash, string serverId, string? legacyHash = null)
    {
        using var db = Open(); Execute(db, null, "INSERT INTO activity_mappings VALUES($d,$k,$id,$sha,$server) ON CONFLICT(device_id,kind,client_id) DO NOTHING",
            ("$d", device), ("$k", kind), ("$id", clientId), ("$sha", hash), ("$server", serverId));
        _ = ActivityMapping(device, kind, clientId, hash, legacyHash);
    }
    public void RegisterActivityBatch(ActivityBatch batch, string hash)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var find = Command(db, tx, "SELECT payload_sha FROM activity_batches WHERE id=$id", ("$id", batch.BatchId));
        if (find.ExecuteScalar() is string previous && !ArchiveFormat.MatchesHash(batch, previous)) throw new PlayConflictException("同一活动批次ID内容不同。");
        Execute(db, tx, "INSERT INTO activity_batches VALUES($id,$sha,$json,NULL,'pending') ON CONFLICT(id) DO NOTHING", ("$id", batch.BatchId), ("$sha", hash), ("$json", Json(batch))); tx.Commit();
    }
    public void SaveActivityReceipt(ActivityReceipt receipt)
    {
        using var db = Open(); Execute(db, null, "UPDATE activity_batches SET receipt_json=$json,state=$state WHERE id=$id", ("$id", receipt.BatchId), ("$json", Json(receipt)),
            ("$state", receipt.Complete ? "complete" : receipt.Results.Any(r => r.Status == "dependency_pending") ? "pending" : "review"));
    }
    public ActivityBatch? PendingActivityBatch()
    {
        using var db = Open(); using var query = Command(db, null, "SELECT payload_json FROM activity_batches WHERE state='pending' ORDER BY COALESCE(json_extract(receipt_json,'$.received_at'),'') LIMIT 1");
        return query.ExecuteScalar() is string json ? Read<ActivityBatch>(json) : null;
    }
    public ActivityReceipt? ActivityReceipt(string id)
    { using var db = Open(); using var query = Command(db, null, "SELECT receipt_json FROM activity_batches WHERE id=$id", ("$id", id)); return query.ExecuteScalar() is string json ? Read<ActivityReceipt>(json) : null; }
    public IReadOnlyList<PlayEditConflict> PlayEditConflicts()
    {
        using var db = Open(); var conflicts = Rows<PlayEditConflict>(db, null, "play_edit_conflicts");
        // A later Web edit must be visible on refresh; the decision also checks this play revision.
        return conflicts.Select(c => GetPlay(c.Current.Id) is { } p ? c with { Current = PlayView(p) } : c).ToArray();
    }
    public ActivityResult ApplyActivityEdit(string deviceId, PlayEditOperation incoming, string playId)
    {
        var conflictId = ActivityKey(deviceId, "edit", incoming.Id);
        var old = PlayEditConflicts().SingleOrDefault(c => c.Id == conflictId);
        if (old is not null && old.Incoming != incoming) throw new PlayConflictException("同一编辑键内容不同。");
        if (old?.State is "server" or "incoming") return new(incoming.Id, "play_edits", "applied", playId, "冲突已人工处理。");
        try
        {
            var current = GetPlay(playId) ?? throw new ArgumentException("游玩不存在。");
            if (incoming.Details.NovelId != current.Details.NovelId || incoming.Details.DurationMinutes != current.Details.DurationMinutes)
                throw new PlayConflictException("活动编辑不能覆盖作品关联或历史手工时间基数。");
            var saved = SavePersonalPlay(new(incoming.Details, ExpectedRevision: incoming.ExpectedRevision), playId, conflictId);
            return new(incoming.Id, "play_edits", "applied", saved.Id);
        }
        catch (PlayConflictException)
        {
            var current = PlayView(GetPlay(playId) ?? throw new ArgumentException("游玩不存在。"));
            if (old is null || old.Current.Revision != current.Revision)
            {
                var conflict = new PlayEditConflict(conflictId, deviceId, incoming, current, (old?.Revision ?? 0) + 1);
                using var db = Open(); Execute(db, null, "INSERT INTO play_edit_conflicts VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET record_json=excluded.record_json", ("$id", conflictId), ("$json", Json(conflict)));
            }
            return new(incoming.Id, "play_edits", "conflict", playId, "个人字段已更新，保留双方版本。", current, incoming.Details);
        }
    }
    public PlayEditConflict ResolvePlayEdit(string id, PlayEditDecision decision, string key) => Mutate("activity/conflict/" + id, key, decision, (db, tx) =>
    {
        var old = Rows<PlayEditConflict>(db, tx, "play_edit_conflicts").Single(c => c.Id == id);
        var play = SessionPlay(db, tx, old.Current.Id);
        if (old.Revision != decision.ExpectedRevision || old.State != "pending" || play.Revision != decision.PlayRevision || play.DeletedAt is not null) throw new PlayConflictException("冲突或Web记录已改变，请重新载入双方版本。");
        if (decision.UseIncoming)
        {
            var d = old.Incoming.Details with { NovelId = play.Details.NovelId, DurationMinutes = play.Details.DurationMinutes };
            var details = new PlayDetails(d.Title, d.NovelId, d.Status, d.StartedOn, d.FinishedOn, d.DurationMinutes, d.Score, d.Review, d.Notes, d.ExtraNotes, d.Language);
            PlayRules.Validate(details, Get);
            play = play with { Details = details, Revision = play.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
            WritePlay(db, tx, play, "conflict-resolution");
        }
        var result = old with { State = decision.UseIncoming ? "incoming" : "server", Revision = old.Revision + 1 };
        Execute(db, tx, "UPDATE play_edit_conflicts SET record_json=$json WHERE id=$id", ("$id", id), ("$json", Json(result)));
        Event(db, tx, play.Id, "edit-conflict-decision", Json(old), Json(result)); return result;
    });
}
