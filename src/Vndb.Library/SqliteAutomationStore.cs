using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vndb.Core;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore : IAutomationStore
{
    private static CatalogJob? ReadJob(SqliteConnection db, SqliteTransaction? tx, string? id = null)
    {
        using var command = Command(db, tx, id is null ? "SELECT record_json FROM catalog_jobs ORDER BY active DESC,created_at DESC LIMIT 1" :
            "SELECT record_json FROM catalog_jobs WHERE id=$id", ("$id", id));
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<CatalogJob>(json, VndbJson.Options) : null;
    }
    public CatalogJob? GetCatalogJob(string? id = null) { using var db = Open(); return ReadJob(db, null, id); }
    private static void WriteJob(SqliteConnection db, SqliteTransaction tx, CatalogJob job, string kind, string? key = null)
    {
        Execute(db, tx, """
            INSERT INTO catalog_jobs(id,status,active,record_json,created_at,updated_at) VALUES($id,$status,$active,$json,$created,$updated)
            ON CONFLICT(id) DO UPDATE SET status=excluded.status,active=excluded.active,record_json=excluded.record_json,updated_at=excluded.updated_at;
            """, ("$id", job.Id), ("$status", job.Status), ("$active", AutomationRules.Active(job) ? 1 : 0),
            ("$json", JsonSerializer.Serialize(job, VndbJson.Options)), ("$created", job.CreatedAt.ToString("O")), ("$updated", job.UpdatedAt.ToString("O")));
        var audit = JsonSerializer.Serialize(new { job = AutomationRules.Summary(job), key }, VndbJson.Options);
        Execute(db, tx, "INSERT INTO catalog_events(entity_id,kind,recorded_at,snapshot_json) VALUES($id,$kind,$at,$json)",
            ("$id", job.Id), ("$kind", kind), ("$at", job.UpdatedAt.ToString("O")), ("$json", audit));
    }
    public CatalogJob QueueCatalogJob(IReadOnlyList<CatalogTask> tasks, int? intervalSeconds, DateTimeOffset now)
    {
        AutomationRules.Interval(intervalSeconds);
        if (tasks.Count is < 1 or > 5000 || tasks.Any(t => t.Key != CatalogRules.Key(t.Title)))
            throw new ArgumentException("后台任务需要 1–5000 个有效标题。");
        using var db = Open(); using var tx = db.BeginTransaction();
        var previous = ReadJob(db, tx); CatalogJob job;
        if (previous is not null && AutomationRules.Active(previous))
        {
            var pendingKeys = previous.Tasks.Skip(previous.Cursor).Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
            var additions = tasks.DistinctBy(t => t.Key).Where(t => !pendingKeys.Contains(t.Key)).ToArray();
            if (previous.Tasks.Count + additions.Length > 5000) throw new ArgumentException("当前任务标题数量超过 5000。");
            job = previous with { Tasks = previous.Tasks.Concat(additions).ToArray(), IntervalSeconds = intervalSeconds ?? previous.IntervalSeconds, UpdatedAt = now };
        }
        else job = new("job-" + Guid.NewGuid().ToString("N"), tasks.DistinctBy(t => t.Key).ToArray(), intervalSeconds ?? 5,
            "queued", 0, 0, 0, 0, 0, 0, 0, now, now, now);
        WriteJob(db, tx, job, "job-queue"); tx.Commit(); return job;
    }
    public CatalogJob? BeginCatalogJob(DateTimeOffset now)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); var job = ReadJob(db, tx);
        if (job is null || job.Status is not ("queued" or "running") || job.NextRunAt > now || job.Cursor >= job.Total)
        { tx.Commit(); return null; }
        job = job with { Status = "running", Attempts = job.Attempts + 1, UpdatedAt = now };
        WriteJob(db, tx, job, "job-item-start", job.Tasks[job.Cursor].Key); tx.Commit(); return job;
    }
    public CatalogJob FinishCatalogItem(string id, string key, string outcome, DateTimeOffset now, string? error = null)
    {
        if (outcome is not ("matched" or "needs_review" or "not_found" or "error" or "skipped")) throw new ArgumentException("无效的任务结果。");
        using var db = Open(); using var tx = db.BeginTransaction(); var job = ReadJob(db, tx, id) ?? throw new ArgumentException("后台任务不存在。");
        if (job.Cursor >= job.Total || job.Tasks[job.Cursor].Key != key) throw new PlayConflictException("后台任务进度已改变。");
        if (outcome == "error" && job.Attempts < 3)
            job = job with { Status = job.Status == "paused" ? "paused" : "queued", NextRunAt = now.AddSeconds(60 * job.Attempts), UpdatedAt = now, LastError = error };
        else
        {
            var cursor = job.Cursor + 1;
            job = job with { Cursor = cursor, Attempts = 0, Status = cursor == job.Total ? "completed" : job.Status == "paused" ? "paused" : "queued",
                Matched = job.Matched + (outcome == "matched" ? 1 : 0), NeedsReview = job.NeedsReview + (outcome == "needs_review" ? 1 : 0),
                NotFound = job.NotFound + (outcome == "not_found" ? 1 : 0), Errors = job.Errors + (outcome == "error" ? 1 : 0),
                Skipped = job.Skipped + (outcome == "skipped" ? 1 : 0), NextRunAt = now.AddSeconds(job.IntervalSeconds), UpdatedAt = now,
                LastError = error ?? job.LastError };
        }
        WriteJob(db, tx, job, outcome == "error" ? "job-item-error" : "job-item-finish", key); tx.Commit(); return job;
    }
    public CatalogJob PauseCatalogJob(string id, bool paused, DateTimeOffset now)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); var job = ReadJob(db, tx, id) ?? throw new ArgumentException("后台任务不存在。");
        if (AutomationRules.Active(job))
        {
            job = job with { Status = paused ? "paused" : "queued", NextRunAt = paused ? job.NextRunAt : now, UpdatedAt = now };
            WriteJob(db, tx, job, paused ? "job-pause" : "job-resume");
        }
        tx.Commit(); return job;
    }
    public LibrarySearchCache? GetLibrarySearchCache(string key)
    {
        using var db = Open(); using var command = Command(db, null, "SELECT record_json FROM library_searches WHERE key=$key", ("$key", key));
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<LibrarySearchCache>(json, VndbJson.Options) : null;
    }
    public void SaveLibrarySearchCache(LibrarySearchCache cache)
    {
        if (cache.Key != AutomationRules.SearchKey(AutomationRules.Search(cache.Request)) || cache.NovelIds.Any(id => Get(id) is null))
            throw new ArgumentException("查询缓存需要已收录的完整作品。");
        using var db = Open(); using var tx = db.BeginTransaction(); var json = JsonSerializer.Serialize(cache, VndbJson.Options);
        Execute(db, tx, "INSERT INTO library_searches VALUES($key,$json,$at) ON CONFLICT(key) DO UPDATE SET record_json=excluded.record_json,updated_at=excluded.updated_at",
            ("$key", cache.Key), ("$json", json), ("$at", cache.SavedAt.ToString("O")));
        Execute(db, tx, "INSERT INTO catalog_events(entity_id,kind,recorded_at,snapshot_json) VALUES($key,'search-save',$at,$json)",
            ("$key", cache.Key), ("$at", cache.SavedAt.ToString("O")), ("$json", json)); tx.Commit();
    }
}
