using Microsoft.Data.Sqlite;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Library;

internal sealed record DatasetWork(DatasetJob Job, string? SourceFile = null, string? Stage = null, string? ManifestSha = null, string Cursor = "");

public sealed partial class SqliteLibraryStore
{
    private static void InitializeDatasets(SqliteConnection db, SqliteTransaction tx) => Execute(db, tx, """
        CREATE TABLE IF NOT EXISTS dataset_jobs(id TEXT PRIMARY KEY,state TEXT NOT NULL,record_json TEXT NOT NULL CHECK(json_valid(record_json)));
        CREATE UNIQUE INDEX IF NOT EXISTS ux_active_dataset ON dataset_jobs((1)) WHERE state NOT IN ('completed','cancelled');
        CREATE TABLE IF NOT EXISTS dataset_sync(id INTEGER PRIMARY KEY CHECK(id=1),record_json TEXT NOT NULL CHECK(json_valid(record_json)));
        PRAGMA user_version=12;
        """);
    private static DatasetWork Dataset(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        using var get = Command(db, tx, "SELECT record_json FROM dataset_jobs WHERE id=$id", ("$id", id));
        return get.ExecuteScalar() is string json ? Read<DatasetWork>(json) : throw new ArgumentException("导出任务不存在。");
    }
    private static void WriteDataset(SqliteConnection db, SqliteTransaction tx, DatasetWork work) => Execute(db, tx,
        "INSERT INTO dataset_jobs VALUES($id,$state,$json) ON CONFLICT(id) DO UPDATE SET state=excluded.state,record_json=excluded.record_json",
        ("$id", work.Job.Id), ("$state", work.Job.State), ("$json", Json(work)));
    public IReadOnlyList<DatasetJob> DatasetJobs()
    {
        using var db = Open(); using var command = Command(db, null, "SELECT record_json FROM dataset_jobs ORDER BY rowid DESC LIMIT 20");
        using var reader = command.ExecuteReader(); var jobs = new List<DatasetJob>(); while (reader.Read()) jobs.Add(Read<DatasetWork>(reader.GetString(0)).Job); return jobs;
    }
    internal DatasetWork Dataset(string id) { using var db = Open(); return Dataset(db, null, id); }
    internal IReadOnlyList<DatasetWork> DatasetWorks()
    {
        using var db = Open(); using var command = Command(db, null, "SELECT record_json FROM dataset_jobs ORDER BY rowid");
        using var reader = command.ExecuteReader(); var works = new List<DatasetWork>();
        while (reader.Read()) works.Add(Read<DatasetWork>(reader.GetString(0)));
        return works;
    }
    internal DatasetCleanupResult CommitDatasetCleanup(DatasetCleanupRequest request, string key, Func<IReadOnlyList<DatasetWork>, DatasetCleanupResult> clean) =>
        Mutate("dataset/cleanup", key, request, (db, tx) =>
        {
            using var query = Command(db, tx, "SELECT record_json FROM dataset_jobs");
            var works = new List<DatasetWork>();
            using (var reader = query.ExecuteReader()) while (reader.Read()) works.Add(Read<DatasetWork>(reader.GetString(0)));
            return clean(works);
        });
    internal DatasetCleanupResult? DatasetCleanupReceipt(DatasetCleanupRequest request, string key)
    {
        using var db = Open();
        var hash = PlayRules.Hash(System.Text.Encoding.UTF8.GetBytes(Json(request)));
        return TryOperationReceipt<DatasetCleanupResult>(db, null, "dataset/cleanup", key, hash, out var result) ? result : null;
    }
    internal bool HasDatasetReceipt(string key)
    {
        using var db = Open(); using var query = Command(db, null, "SELECT 1 FROM operation_receipts WHERE scope='dataset/create' AND key=$key", ("$key", key));
        return query.ExecuteScalar() is not null;
    }
    internal DatasetJob ReplayDatasetUpload(string key, string sha)
    {
        using var db = Open(); var hash = PlayRules.Hash(System.Text.Encoding.UTF8.GetBytes(Json(new { source = "upload", sha })));
        return TryOperationReceipt<DatasetJob>(db, null, "dataset/create", key, hash, out var result) ? result :
            throw new PlayConflictException("原上传回执已变化，请核对后重新提交。");
    }
    internal DatasetWork? ActiveDataset()
    {
        using var db = Open(); using var command = Command(db, null, "SELECT record_json FROM dataset_jobs WHERE state NOT IN ('completed','cancelled') LIMIT 1");
        return command.ExecuteScalar() is string json ? Read<DatasetWork>(json) : null;
    }
    internal DatasetJob EnqueueDataset(string source, string key, string? file = null, string? sha = null, long bytes = 0) =>
        Mutate("dataset/create", key, new { source, sha }, (db, tx) => EnqueueDataset(db, tx, source, file, sha, bytes));
    private static DatasetJob EnqueueDataset(SqliteConnection db, SqliteTransaction tx, string source, string? file = null, string? sha = null, long bytes = 0)
    {
        using var prior = Command(db, tx, "SELECT record_json FROM dataset_jobs ORDER BY rowid DESC");
        using (var reader = prior.ExecuteReader())
            while (reader.Read())
            {
                var old = Read<DatasetWork>(reader.GetString(0));
                if (old.Job.Source == source && (sha is null || old.Job.Sha256 == sha) && old.Job.State != "cancelled") return old.Job;
                if (old.Job.State is not ("completed" or "cancelled")) throw new PlayConflictException("已有导出任务，请先完成或取消。");
            }
        var job = new DatasetJob(Guid.NewGuid().ToString("D"), source, "queued", sha is null ? "download" : "prepare", 1, DateTimeOffset.UtcNow, sha, DownloadBytes: bytes);
        WriteDataset(db, tx, new(job, file)); return job;
    }
    public DatasetJob ControlDataset(string id, string action, DatasetRevision revision, string key) =>
        Mutate("dataset/control/" + id, key, new { action, revision }, (db, tx) =>
        {
            var work = Dataset(db, tx, id); var job = work.Job;
            if (job.Revision != revision.ExpectedRevision) throw new PlayConflictException("任务进度已变，请刷新后重试。");
            if (action == "resume" && job.EarliestResumeAt > DateTimeOffset.UtcNow) throw new PlayConflictException("上游要求等待，请在可恢复时间之后重试。");
            var state = action switch
            {
                "pause" when job.State is "queued" or "downloading" or "preparing" or "importing" => "paused",
                "resume" when job.State is "paused" or "failed" => "queued",
                "cancel" when job.State is not ("completed" or "cancelled") => "cancelled",
                _ => throw new ArgumentException("此状态不支持该操作。")
            };
            job = job with { State = state, Revision = job.Revision + 1, Error = null };
            WriteDataset(db, tx, work with { Job = job }); return job;
        });
    internal DatasetWork UpdateDataset(string id, Func<DatasetWork, DatasetWork> change)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); var work = Dataset(db, tx, id);
        if (work.Job.State is "paused" or "cancelled" or "failed" or "completed") return work;
        var next = change(work); next = next with { Job = next.Job with { Revision = work.Job.Revision + 1 } };
        WriteDataset(db, tx, next); tx.Commit(); return next;
    }
    internal void CommitDatasetBatch(string id, string expectedCursor, string phase, IReadOnlyList<(string Id, string Json)> rows, DumpManifest manifest)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); var work = Dataset(db, tx, id);
        if (work.Job.State is not ("queued" or "importing") || work.Cursor != expectedCursor || work.Job.Phase != phase) return;
        long written = 0, kept = 0;
        foreach (var row in rows)
        {
            if (phase == "producer")
            {
                var producer = Read<Producer>(row.Json);
                using var prior = Command(db, tx, "SELECT updated_at FROM producers WHERE id=$id", ("$id", producer.Id));
                if (prior.ExecuteScalar() is string date && DateTimeOffset.Parse(date) >= manifest.SnapshotAt) { kept++; continue; }
                Execute(db, tx, """
                    INSERT INTO producers VALUES($id,$name,$original,$type,$lang,$json,$at)
                    ON CONFLICT(id) DO UPDATE SET name=excluded.name,original=excluded.original,kind=excluded.kind,lang=excluded.lang,metadata_json=excluded.metadata_json,updated_at=excluded.updated_at
                    """, ("$id", producer.Id), ("$name", producer.Name), ("$original", producer.Original), ("$type", producer.Type), ("$lang", producer.Lang), ("$json", row.Json), ("$at", manifest.SnapshotAt.ToString("O")));
                WriteProvenance(db, tx, producer.Id, "vndb_dump", manifest.SnapshotAt, "partial_text", work.Job.Id, null); written++;
            }
            else
            {
                var incoming = Read<LibraryEntry>(row.Json); var previous = NovelEntry(db, tx, row.Id);
                if (previous is not null && (previous.Provenance == "client_vndb_import" || previous.UpdatedAt >= manifest.SnapshotAt))
                {
                    if (previous.Novel.ReleaseCatalog is not null) { kept++; continue; }
                    // A missing field can be filled without replacing newer API text or its observation time.
                    incoming = previous with { Novel = previous.Novel with { ReleaseCatalog = incoming.Novel.ReleaseCatalog, Relations = previous.Novel.Relations ?? incoming.Novel.Relations } };
                }
                else if (previous is not null) incoming = incoming with { CreatedAt = previous.CreatedAt, Cover = previous.Cover,
                    CoverStatus = previous.Cover is null ? incoming.CoverStatus : previous.CoverStatus,
                    Novel = incoming.Novel with { Image = previous.Novel.Image, Rating = incoming.Novel.Rating ?? previous.Novel.Rating,
                        VoteCount = incoming.Novel.VoteCount ?? previous.Novel.VoteCount,
                        ReleaseCatalog = ReleaseRules.Prefer(incoming.Novel.ReleaseCatalog, previous.Novel.ReleaseCatalog) } };
                var producers = incoming.Producers.Select(p =>
                {
                    using var current = Command(db, tx, "SELECT metadata_json FROM producers WHERE id=$id", ("$id", p.Id));
                    return current.ExecuteScalar() is string json ? Read<Producer>(json) : p;
                }).ToArray();
                incoming = incoming with { Producers = producers, Novel = incoming.Novel with { Developers = producers } };
                var saved = WriteLibraryEntry(db, tx, incoming);
                WriteProvenance(db, tx, row.Id, saved.Provenance, saved.UpdatedAt, saved.Completeness, work.Job.Id, null); written++;
            }
        }
        var job = work.Job with { State = "importing", Revision = work.Job.Revision + 1,
            Processed = work.Job.Processed + rows.Count, Written = work.Job.Written + written, Kept = work.Job.Kept + kept };
        if (rows.Count == 0)
        {
            if (phase == "producer") { job = job with { Phase = "vn" }; work = work with { Cursor = "" }; }
            else
            {
                if (job.Processed != job.Total || job.Written + job.Kept != job.Total) throw new InvalidDataException("导入覆盖计数不一致。");
                job = job with { State = "completed", Phase = "completed", CompletedAt = DateTimeOffset.UtcNow };
            }
        }
        WriteDataset(db, tx, work with { Job = job, Cursor = rows.Count > 0 ? rows[^1].Id : work.Cursor }); tx.Commit();
    }
}
