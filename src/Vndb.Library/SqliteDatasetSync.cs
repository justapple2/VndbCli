using Microsoft.Data.Sqlite;
using Vndb.Contracts;

namespace Vndb.Library;

// One operational document, included in database snapshots. No second queue or public metadata store.
internal sealed record DatasetSyncState(DatasetSyncSettings Settings, string? LatestSnapshotDate = null,
    DateTimeOffset? LastCheckedAt = null, DateTimeOffset? LastAttemptAt = null, DateTimeOffset? CheckNotBefore = null,
    DateTimeOffset? NextCheckAt = null, string? Error = null, string? CheckToken = null, DateTimeOffset? CheckLeaseUntil = null);
internal sealed record DatasetCheckClaim(string? Token);

public sealed partial class SqliteLibraryStore
{
    private static DatasetSyncState ReadDatasetSync(SqliteConnection db, SqliteTransaction? tx)
    {
        using var command = Command(db, tx, "SELECT record_json FROM dataset_sync WHERE id=1");
        var state = command.ExecuteScalar() is string json ? Read<DatasetSyncState>(json) : new(new());
        if (state.Settings is null || state.Settings.Revision < 0 || state.Settings.IntervalDays is < 1 or > 30)
            throw new InvalidDataException("官方同步配置无效，请保留数据库并检查备份。");
        return state;
    }
    private static void WriteDatasetSync(SqliteConnection db, SqliteTransaction tx, DatasetSyncState state) =>
        Execute(db, tx, "INSERT INTO dataset_sync VALUES(1,$json) ON CONFLICT(id) DO UPDATE SET record_json=excluded.record_json", ("$json", Json(state)));
    private static DatasetJob? DatasetJobByState(SqliteConnection db, SqliteTransaction? tx, bool completed)
    {
        using var command = Command(db, tx, completed ?
            "SELECT record_json FROM dataset_jobs WHERE state='completed' ORDER BY julianday(json_extract(record_json,'$.job.snapshot_at')) DESC LIMIT 1" :
            "SELECT record_json FROM dataset_jobs WHERE state NOT IN ('completed','cancelled') LIMIT 1");
        return command.ExecuteScalar() is string json ? Read<DatasetWork>(json).Job : null;
    }
    public DatasetSyncStatus DatasetSyncStatus(DateTimeOffset now, bool workerEnabled = true)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true); var state = ReadDatasetSync(db, tx);
        var completed = DatasetJobByState(db, tx, true); var active = DatasetJobByState(db, tx, false);
        var newer = state.LatestSnapshotDate is null ? (bool?)null : completed?.SnapshotAt is null ||
            DateOnly.FromDateTime(completed.SnapshotAt.Value.UtcDateTime) < DateOnly.ParseExact(state.LatestSnapshotDate, "yyyy-MM-dd");
        var error = state.Error ?? (state.CheckToken is not null && state.CheckLeaseUntil <= now ? "上次检查中断，请在允许时间后重新检查。" : null);
        return new(state.Settings, state.LatestSnapshotDate, state.LastCheckedAt, state.LastAttemptAt, state.CheckNotBefore,
            state.NextCheckAt, error, state.CheckToken is not null && state.CheckLeaseUntil > now,
            completed?.SnapshotAt, completed?.CompletedAt, active, newer, workerEnabled);
    }
    public DatasetSyncSettings SaveDatasetSync(DatasetSyncWrite request, string key, DateTimeOffset now) =>
        Mutate("dataset/sync/settings", key, request, (db, tx) =>
        {
            if (request.IntervalDays is < 1 or > 30) throw new ArgumentException("自动同步间隔须为1–30天。");
            var state = ReadDatasetSync(db, tx);
            if (state.Settings.Revision != request.ExpectedRevision) throw new PlayConflictException("同步设置已改变，请保留草稿并重新读取。");
            var settings = new DatasetSyncSettings(checked(state.Settings.Revision + 1), request.Enabled, request.IntervalDays);
            WriteDatasetSync(db, tx, state with { Settings = settings, NextCheckAt = request.Enabled ? now : null });
            return settings;
        });
    internal DatasetCheckClaim BeginDatasetCheck(string key, DateTimeOffset now) => Mutate("dataset/sync/check", key, new { }, (db, tx) =>
    {
        var state = ReadDatasetSync(db, tx);
        if (state.CheckNotBefore > now || state.CheckLeaseUntil > now) return new DatasetCheckClaim(null);
        var token = Guid.NewGuid().ToString("N");
        WriteDatasetSync(db, tx, state with { LastAttemptAt = now, CheckNotBefore = now.AddMinutes(15), CheckToken = token, CheckLeaseUntil = now.AddMinutes(2) });
        return new DatasetCheckClaim(token);
    });
    internal bool OwnsDatasetCheck(string token)
    { using var db = Open(); return ReadDatasetSync(db, null).CheckToken == token; }
    internal void FinishDatasetCheck(string token, string? date, string? error, DateTimeOffset? retryAt, DateTimeOffset now)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); var state = ReadDatasetSync(db, tx);
        if (state.CheckToken != token) return;
        if (date is not null && string.CompareOrdinal(date, state.LatestSnapshotDate) < 0)
        { date = null; error = "官方最新日期早于上次发现日期，保留原信息，请稍后核对。"; }
        var next = retryAt ?? (error is null ? now.AddMinutes(15) : now.AddHours(1));
        if (state.CheckNotBefore > next) next = state.CheckNotBefore.Value;
        WriteDatasetSync(db, tx, state with { LatestSnapshotDate = date ?? state.LatestSnapshotDate,
            LastCheckedAt = date is null ? state.LastCheckedAt : now, Error = error, CheckToken = null, CheckLeaseUntil = null, CheckNotBefore = next });
        tx.Commit();
    }
    internal long? BeginScheduledDatasetSync(DateTimeOffset now)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); var state = ReadDatasetSync(db, tx);
        if (!state.Settings.Enabled || state.NextCheckAt > now || state.NextCheckAt is null || state.CheckNotBefore > now ||
            state.CheckLeaseUntil > now || DatasetJobByState(db, tx, false) is not null) return null;
        // Persist bounded retry before the HEAD request; crashes cannot cause one request per worker tick.
        WriteDatasetSync(db, tx, state with { NextCheckAt = now.AddHours(1) }); tx.Commit(); return state.Settings.Revision;
    }
    internal DatasetSyncResult UpdateLatestDataset(string key, DateTimeOffset now, long? scheduleRevision = null) => Mutate("dataset/sync/update", key, new { scheduleRevision }, (db, tx) =>
    {
        var state = ReadDatasetSync(db, tx); var date = state.LatestSnapshotDate;
        if (scheduleRevision is not null && (!state.Settings.Enabled || state.Settings.Revision != scheduleRevision)) return new DatasetSyncResult("schedule_changed", date);
        if (state.CheckToken is not null) return new(state.CheckLeaseUntil > now ? "checking" : "check_failed", date);
        if (state.Error is not null || state.LastCheckedAt is null) return new("check_failed", date);
        if (date is null) return new("not_checked", null);
        var completed = DatasetJobByState(db, tx, true); var active = DatasetJobByState(db, tx, false);
        DatasetSyncResult result;
        if (completed?.SnapshotAt is { } snapshot && DateOnly.FromDateTime(snapshot.UtcDateTime) >= DateOnly.ParseExact(date, "yyyy-MM-dd")) result = new("current", date);
        else if (active is not null) result = new(active.Source == "vndb-db-" + date + ".tar.zst" && active.State is not ("paused" or "failed") ? "existing" : "blocked", date, active);
        else result = new("queued", date, EnqueueDataset(db, tx, "vndb-db-" + date + ".tar.zst"));
        if (state.Settings.Enabled && result.State is "current" or "queued" or "existing")
            WriteDatasetSync(db, tx, state with { NextCheckAt = now.AddDays(state.Settings.IntervalDays) });
        return result;
    });
}
