using Vndb.Contracts;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    public IReadOnlyList<CoverRepairResult> RepairPersonalCovers(CoverRepairRequest request, string key)
    {
        if (request.ItemIds is null || request.ItemIds.Count is < 1 or > 100 || request.ItemIds.Any(string.IsNullOrWhiteSpace) || request.ItemIds.Distinct().Count() != request.ItemIds.Count)
            throw new ArgumentException("请选择1–100个唯一的个人书架条目。");
        return Mutate("metadata/covers/repair", key, request, (db, tx) => request.ItemIds.Select(id =>
        {
            var item = Item(db, tx, id);
            if (item.DeletedAt is not null) throw new PlayConflictException("条目已移除，请重新读取书架。");
            if (item.Details.NovelId is not { } vn) return new CoverRepairResult(id, null, "unlinked");
            var entry = NovelEntry(db, tx, vn);
            if (entry is not null && !LibraryRules.NeedsCover(entry, DataDirectory)) return new(id, vn, entry.Cover is null ? "no_cover" : "ready");
            // Reuse the existing VN-scoped queue. Repair cannot rebind personal entries or change their revisions.
            var job = QueueMetadataJob(db, tx, vn, item.Details.Title, null);
            if (job.State is "complete" or "error" or "not_found")
            {
                var next = job with { State = "queued", Attempts = 0, NextAt = DateTimeOffset.UtcNow, Error = null };
                Execute(db, tx, "UPDATE metadata_jobs SET record_json=$json WHERE id=$id", ("$json", Json(next)), ("$id", job.Id));
                Event(db, tx, job.Id, "cover-repair", Json(job), Json(next)); job = next;
            }
            return new CoverRepairResult(id, vn, job.State, job.Id);
        }).ToArray());
    }
    public IReadOnlyList<MetadataJob> MetadataJobs()
    {
        using var db = Open(); using var command = Command(db, null, "SELECT record_json FROM metadata_jobs ORDER BY id");
        using var reader = command.ExecuteReader(); var rows = new List<MetadataJob>();
        while (reader.Read()) rows.Add(Read<MetadataJob>(reader.GetString(0))); return rows;
    }
    public MetadataJob? BeginMetadataJob(DateTimeOffset now)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var command = Command(db, tx, "SELECT record_json FROM metadata_jobs");
        var rows = new List<MetadataJob>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) rows.Add(Read<MetadataJob>(reader.GetString(0)));
        var job = rows.Where(j => j.State is "queued" or "working" && j.NextAt <= now).OrderBy(j => j.NextAt).FirstOrDefault();
        if (job is null) return null;
        if (job.Attempts >= 3)
        {
            var exhausted = job with { State = "error", Error = "重试次数已用尽（包括中断的请求），请人工重试。" };
            Execute(db, tx, "UPDATE metadata_jobs SET record_json=$json WHERE id=$id", ("$json", Json(exhausted)), ("$id", job.Id));
            Event(db, tx, job.Id, "metadata-job-exhausted", Json(job), Json(exhausted)); tx.Commit(); return null;
        }
        var claimed = job with { State = "working", Attempts = job.Attempts + 1, NextAt = now.AddSeconds(5) };
        Execute(db, tx, "UPDATE metadata_jobs SET record_json=$json WHERE id=$id", ("$json", Json(claimed)), ("$id", job.Id)); tx.Commit(); return claimed;
    }
    public void FinishMetadataJob(MetadataJob job, IReadOnlyList<MetadataSnapshot>? matches, string? error, DateTimeOffset now)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var select = Command(db, tx, "SELECT record_json FROM metadata_jobs WHERE id=$id", ("$id", job.Id));
        var current = Read<MetadataJob>((string)select.ExecuteScalar()!);
        var result = job;
        if (error is not null) result = result with { State = job.Attempts >= 3 ? "error" : "queued", Error = error, NextAt = now.AddSeconds(30 * (1 << Math.Min(job.Attempts, 5))) };
        else if (matches?.Count == 0) result = result with { State = "not_found", Error = "VNDB 暂无结果，可修改标题后重新查询。" };
        else
        {
            if (job.VndbId is null && matches is { Count: > 0 })
            {
                var candidate = Candidate(db, tx, "title_matches", null, job.ItemId, null, matches, "请核对タイトル／会社后选择；模糊首项不会自动绑定。");
                result = result with { State = "needs_review", CandidateId = candidate.Id, Error = null };
            }
            else
            {
                result = result with { State = "complete", Error = null };
                if (job.ItemId is { } itemId && job.VndbId?.StartsWith('v') == true)
                {
                    var item = Item(db, tx, itemId);
                    if (item.Details.NovelId is null && item.DeletedAt is null && item.Revision == job.ItemRevision)
                        LinkItemCore(db, tx, itemId, new(job.VndbId, item.Revision));
                }
            }
        }
        if (current.State == "paused") result = result with { State = "paused" };
        Execute(db, tx, "UPDATE metadata_jobs SET record_json=$json WHERE id=$id", ("$json", Json(result)), ("$id", job.Id));
        Event(db, tx, job.Id, "metadata-job", Json(current), Json(result)); tx.Commit();
    }
    public MetadataJob ControlMetadataJob(string id, string action, string key) => Mutate("metadata/job/" + id, key, new { action }, (db, tx) =>
    {
        if (action is not ("pause" or "resume" or "retry")) throw new ArgumentException("无效任务操作。");
        using var get = Command(db, tx, "SELECT record_json FROM metadata_jobs WHERE id=$id", ("$id", id));
        var previous = get.ExecuteScalar() is string json ? Read<MetadataJob>(json) : throw new ArgumentException("资料任务不存在。");
        var result = previous with { State = action == "pause" ? "paused" : "queued", Attempts = action == "retry" ? 0 : previous.Attempts, NextAt = DateTimeOffset.UtcNow, Error = null };
        Execute(db, tx, "UPDATE metadata_jobs SET record_json=$json WHERE id=$id", ("$json", Json(result)), ("$id", id));
        Event(db, tx, id, "metadata-job-" + action, Json(previous), Json(result)); return result;
    });
}
