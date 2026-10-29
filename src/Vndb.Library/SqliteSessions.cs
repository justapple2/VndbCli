using Microsoft.Data.Sqlite;
using Vndb.Contracts;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    private static void InitializeM6(SqliteConnection db, SqliteTransaction tx) => Execute(db, tx, """
        CREATE TABLE IF NOT EXISTS play_sessions(id TEXT PRIMARY KEY,device_id TEXT NOT NULL REFERENCES devices(id),play_id TEXT NOT NULL REFERENCES play_records(id),record_json TEXT NOT NULL CHECK(json_valid(record_json)));
        CREATE INDEX IF NOT EXISTS ix_session_play ON play_sessions(play_id);
        CREATE TABLE IF NOT EXISTS play_time_heads(play_id TEXT PRIMARY KEY REFERENCES play_records(id),revision INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS play_time_adjustments(id TEXT PRIMARY KEY,play_id TEXT NOT NULL REFERENCES play_records(id),record_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS activity_mappings(device_id TEXT NOT NULL,kind TEXT NOT NULL,client_id TEXT NOT NULL,payload_sha TEXT NOT NULL,server_id TEXT NOT NULL,PRIMARY KEY(device_id,kind,client_id));
        CREATE TABLE IF NOT EXISTS activity_batches(id TEXT PRIMARY KEY,payload_sha TEXT NOT NULL,payload_json TEXT NOT NULL,receipt_json TEXT,state TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS play_edit_conflicts(id TEXT PRIMARY KEY,record_json TEXT NOT NULL);
        PRAGMA user_version=9;
        """);
    private static PlayRecord SessionPlay(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        using var query = Command(db, tx, "SELECT record_json FROM play_records WHERE id=$id", ("$id", id));
        return query.ExecuteScalar() is string json ? ReadPlay(json) : throw new ArgumentException("游玩经历尚不存在。");
    }
    private static long TimeRevision(SqliteConnection db, SqliteTransaction? tx, string id)
    { using var query = Command(db, tx, "SELECT revision FROM play_time_heads WHERE play_id=$id", ("$id", id)); return Convert.ToInt64(query.ExecuteScalar() ?? 0L); }
    private static void BumpTime(SqliteConnection db, SqliteTransaction tx, string id) => Execute(db, tx,
        "INSERT INTO play_time_heads VALUES($id,1) ON CONFLICT(play_id) DO UPDATE SET revision=revision+1", ("$id", id));
    private static void WriteSession(SqliteConnection db, SqliteTransaction tx, PlaySession session)
    {
        Execute(db, tx, "INSERT INTO play_sessions VALUES($id,$device,$play,$json) ON CONFLICT(id) DO UPDATE SET record_json=excluded.record_json",
            ("$id", session.Sample.Id), ("$device", session.Sample.DeviceId), ("$play", session.Sample.PlayId), ("$json", Json(session)));
        BumpTime(db, tx, session.Sample.PlayId);
    }
    public IReadOnlyList<PlaySession> Sessions(string? playId = null)
    { using var db = Open(); return Rows<PlaySession>(db, null, "play_sessions").Where(s => playId is null || s.Sample.PlayId == playId).ToArray(); }
    public PlaySession UpsertSession(SessionSample sample)
    {
        if (!Guid.TryParseExact(sample.Id, "D", out _) || sample.Sequence < 1 || sample.CumulativeSeconds is < 0 or > 315360000 ||
            sample.State is not ("active" or "paused" or "ended" or "interrupted" or "review") || sample.Reason?.Length > 500 ||
            sample.EndedAt is not null && sample.State is "active" or "paused" || sample.EndedAt is null && sample.State is not ("active" or "paused"))
            throw new ArgumentException("无效会话、累计秒数、序号或终态。");
        using var db = Open(); using var tx = db.BeginTransaction();
        var play = SessionPlay(db, tx, sample.PlayId); if (play.DeletedAt is not null) throw new PlayConflictException("游玩经历已移除。");
        var device = Rows<LibraryDevice>(db, tx, "devices").SingleOrDefault(d => d.Id == sample.DeviceId);
        if (device is null || device.Legacy || device.MappedToId is not null) throw new ArgumentException("会话需要已登记的稳定设备ID。");
        if (sample.InstallationId is { } installationId)
        {
            var installation = Rows<Installation>(db, tx, "installations").SingleOrDefault(i => i.Id == installationId);
            if (installation is null || installation.RemovedAt is not null || installation.Details.DeviceId != sample.DeviceId) throw new ArgumentException("会话安装不属于此设备。");
            using var item = Command(db, tx, "SELECT item_id FROM play_item_links WHERE play_id=$id", ("$id", play.Id));
            var itemId = item.ExecuteScalar() as string;
            if (!Rows<InstallationComponent>(db, tx, "installation_components").Any(c => c.InstallationId == installationId && c.Details.ItemId == itemId && (sample.ComponentId is null || c.Id == sample.ComponentId)))
                throw new ArgumentException("安装组件与游玩条目不符。");
        }
        else if (sample.ComponentId is not null) throw new ArgumentException("组件缺少安装。");
        var sessions = Rows<PlaySession>(db, tx, "play_sessions"); var old = sessions.SingleOrDefault(s => s.Sample.Id == sample.Id);
        if (old is not null)
        {
            var prior = old.Sample;
            if (prior.DeviceId != sample.DeviceId || prior.PlayId != sample.PlayId || prior.StartedAt != sample.StartedAt || prior.InstallationId != sample.InstallationId || prior.ComponentId != sample.ComponentId)
                throw new PlayConflictException("会话身份或游玩关联不能改变。");
            if (sample.Sequence == prior.Sequence && sample != prior) throw new PlayConflictException("同一会话序号内容不同。");
            if (sample.Sequence <= prior.Sequence || prior.State is not ("active" or "paused")) return old;
            if (sample.CumulativeSeconds < prior.CumulativeSeconds) throw new PlayConflictException("累计秒数不能回退。");
        }
        if (sample.State is "active" or "paused" && sessions.Any(s => s.Sample.Id != sample.Id && s.Sample.DeviceId == sample.DeviceId && s.Sample.State is "active" or "paused"))
            throw new PlayConflictException("同设备已有自动会话，请先结束或恢复旧会话。");
        var conflicts = sessions.Where(s => s.Sample.Id != sample.Id && s.Sample.DeviceId != sample.DeviceId && s.Sample.PlayId == sample.PlayId &&
            s.ReviewState != "excluded" && s.Sample.StartedAt < (sample.EndedAt ?? sample.CheckpointAt) && sample.StartedAt < (s.Sample.EndedAt ?? s.Sample.CheckpointAt)).ToArray();
        var pending = sample.NeedsReview || sample.CheckpointAt < sample.StartedAt || conflicts.Length > 0;
        var result = new PlaySession(sample, (old?.Revision ?? 0) + 1,
            old?.Reviewed == true ? old.ReviewState : pending || old?.ReviewState == "pending" ? "pending" : "accepted", old?.Reviewed ?? false);
        foreach (var conflict in conflicts.Where(s => !s.Reviewed && s.ReviewState != "pending")) WriteSession(db, tx, conflict with { ReviewState = "pending", Revision = conflict.Revision + 1 });
        WriteSession(db, tx, result); Event(db, tx, sample.Id, "session-checkpoint", old is null ? null : Json(old), Json(result)); tx.Commit(); return result;
    }
    public PlaySession DecideSession(string id, SessionDecision decision, string key) => Mutate("session/decision/" + id, key, decision, (db, tx) =>
    {
        var old = Rows<PlaySession>(db, tx, "play_sessions").Single(s => s.Sample.Id == id);
        if (old.Revision != decision.ExpectedRevision || old.Sample.State is "active" or "paused") throw new PlayConflictException("仅可确认已结束且修订匹配的会话。");
        var result = old with { ReviewState = decision.Include ? "accepted" : "excluded", Reviewed = true, Revision = old.Revision + 1 };
        WriteSession(db, tx, result); Event(db, tx, id, "session-decision", Json(old), Json(result)); return result;
    });
    public PlayTimeSummary PlayTime(string playId)
    {
        using var db = Open(); var play = SessionPlay(db, null, playId);
        return Summarize(play, Rows<PlaySession>(db, null, "play_sessions"), Rows<TimeAdjustment>(db, null, "play_time_adjustments"), TimeRevision(db, null, playId));
    }
    public IReadOnlyDictionary<string, PlayTimeSummary> PlayTimes(IEnumerable<PlayRecord> records)
    {
        using var read = new LibraryReadScope(this, "times"); return ReadTimes(read, records.ToArray());
    }
    private static PlayTimeSummary Summarize(PlayRecord play, IReadOnlyList<PlaySession> allSessions, IReadOnlyList<TimeAdjustment> allAdjustments, long revision)
    {
        var playId = play.Id;
        var sessions = allSessions.Where(s => s.Sample.PlayId == playId).ToArray();
        var seconds = sessions.Where(s => s.ReviewState == "accepted").Sum(s => s.Sample.CumulativeSeconds);
        var adjustments = allAdjustments.Where(a => a.PlayId == playId).Sum(a => a.Seconds);
        return new(playId, play.Details.DurationMinutes, seconds, adjustments, play.Details.DurationMinutes is { } m ? Math.Max(0, m * 60L + seconds + adjustments) : null,
            sessions.Count(s => s.ReviewState == "pending"), revision + play.Revision);
    }
    public TimeAdjustment AdjustTime(TimeAdjustment adjustment, string key)
    {
        if (!Guid.TryParseExact(adjustment.Id, "D", out _) || adjustment.Seconds is < -315360000 or > 315360000 || string.IsNullOrWhiteSpace(adjustment.Reason) || adjustment.Reason.Length > 1000) throw new ArgumentException("人工调整需要唯一ID、秒数及原因。");
        return Mutate("time/adjust", key, adjustment, (db, tx) =>
        {
            var play = SessionPlay(db, tx, adjustment.PlayId);
            if (TimeRevision(db, tx, adjustment.PlayId) + play.Revision != adjustment.ExpectedRevision) throw new PlayConflictException("时间汇总已更新，请重新核对。");
            var total = (play.Details.DurationMinutes ?? 0) * 60L + Rows<PlaySession>(db, tx, "play_sessions").Where(s => s.Sample.PlayId == play.Id && s.ReviewState == "accepted").Sum(s => s.Sample.CumulativeSeconds)
                + Rows<TimeAdjustment>(db, tx, "play_time_adjustments").Where(a => a.PlayId == play.Id).Sum(a => a.Seconds);
            if (total + adjustment.Seconds < 0) throw new ArgumentException("调整后已知时间不能为负；未知历史基数保持未知。");
            Execute(db, tx, "INSERT INTO play_time_adjustments VALUES($id,$play,$json)", ("$id", adjustment.Id), ("$play", adjustment.PlayId), ("$json", Json(adjustment)));
            BumpTime(db, tx, adjustment.PlayId); Event(db, tx, adjustment.PlayId, "time-adjustment", null, Json(adjustment)); return adjustment;
        });
    }
    public PersonalPlayView PlayView(PlayRecord p)
    {
        using var read = new LibraryReadScope(this, "play-view");
        using var select = read.Query("SELECT item_id FROM play_item_links WHERE play_id=$id", ("$id", p.Id));
        var itemId = select.ExecuteScalar() as string;
        return PersonalView(p, ReadTimes(read, [p])[p.Id], itemId);
    }
}
