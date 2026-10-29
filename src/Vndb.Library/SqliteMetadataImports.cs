using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    public ImportBatchReceipt PreviewMetadata(IReadOnlyList<NormalizedMetadata> items, string? clientId, string rawSha, bool addToLibrary)
    {
        var hash = PlayRules.Hash(JsonSerializer.SerializeToUtf8Bytes(items, VndbJson.Options));
        var id = "import-" + hash;
        using var db = Open(); using var tx = db.BeginTransaction();
        if (clientId is not null)
        {
            using var alias = Command(db, tx, "SELECT payload_sha FROM metadata_batch_aliases WHERE client_id=$id", ("$id", clientId));
            if (alias.ExecuteScalar() is string prior && prior != hash) throw new PlayConflictException("batch_id 已用于不同内容，未覆盖原回执。");
        }
        Execute(db, tx, "INSERT INTO metadata_batches VALUES($id,$hash,$raw,$client,$add,'preview',$at) ON CONFLICT(id) DO NOTHING",
            ("$id", id), ("$hash", hash), ("$raw", rawSha), ("$client", clientId), ("$add", addToLibrary), ("$at", DateTimeOffset.UtcNow.ToString("O")));
        if (clientId is not null) Execute(db, tx, "INSERT INTO metadata_batch_aliases VALUES($client,$batch,$hash) ON CONFLICT(client_id) DO NOTHING", ("$client", clientId), ("$batch", id), ("$hash", hash));
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            Execute(db, tx, "INSERT INTO metadata_import_items VALUES($batch,$ordinal,$input,$result,'preview') ON CONFLICT(batch_id,ordinal) DO NOTHING",
                ("$batch", id), ("$ordinal", i), ("$input", Json(item)), ("$result", Json(PreviewResult(db, tx, item, i))));
        }
        // A preview option can change before commit; committed semantics and effects never change on retries.
        Execute(db, tx, "UPDATE metadata_batches SET add_to_library=$add WHERE id=$id AND state='preview'", ("$add", addToLibrary), ("$id", id));
        var receipt = ReadBatch(db, tx, id); tx.Commit(); return receipt;
    }
    private static ImportItemResult PreviewResult(SqliteConnection db, SqliteTransaction tx, NormalizedMetadata item, int index)
    {
        if (item.Error is not null) return new(index, item.OperationId, item.VndbId, item.Title, "invalid", item.Error);
        if (item.Snapshot?.Novel is { } novel && NovelEntry(db, tx, novel.Id) is { } old)
        {
            var same = Json(old.Novel) == Json(novel) && Json(old.Producers) == Json(item.Snapshot.Developers);
            return new(index, item.OperationId, item.VndbId, item.Title, same ? "existing" : "needs_review", same ? "已有相同资料，提交后复用。" : "与现有资料不同，提交后保留候选，当前资料不变。");
        }
        return new(index, item.OperationId, item.VndbId, item.Title, "preview", item.Snapshot is null ? "清单将排队补全；标题候选需要人工核对。" : "完整快照可离线收录，缺失资源另行补全。");
    }
    private static ImportBatchReceipt ReadBatch(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        using var get = Command(db, tx, "SELECT payload_sha,client_id,state,created_at,raw_sha FROM metadata_batches WHERE id=$id", ("$id", id));
        string hash, state, raw; string? client; DateTimeOffset created;
        using (var reader = get.ExecuteReader())
        {
            if (!reader.Read()) throw new ArgumentException("导入批次不存在。");
            hash = reader.GetString(0); client = reader.IsDBNull(1) ? null : reader.GetString(1); state = reader.GetString(2);
            created = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture); raw = reader.GetString(4);
        }
        using var rows = Command(db, tx, "SELECT result_json,status FROM metadata_import_items WHERE batch_id=$id ORDER BY ordinal", ("$id", id));
        using var all = rows.ExecuteReader(); var items = new List<ImportItemResult>(); var complete = 0;
        while (all.Read()) { items.Add(Read<ImportItemResult>(all.GetString(0))); if (all.GetString(1) != "preview") complete++; }
        return new(id, hash, client, state, state != "preview", items.Count, complete, items, created, RawSha256: raw);
    }
    public ImportBatchReceipt GetMetadataBatch(string id) { using var db = Open(); return ReadBatch(db, null, id); }
    public IReadOnlyList<ImportBatchReceipt> MetadataBatches()
    {
        using var db = Open(); using var select = Command(db, null, "SELECT id FROM metadata_batches ORDER BY created_at DESC LIMIT 100");
        var ids = new List<string>(); using (var reader = select.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetString(0));
        return ids.Select(id => ReadBatch(db, null, id)).ToArray();
    }
    public ImportBatchReceipt CommitMetadata(string id, string key) => Mutate("metadata/commit", key, new { id }, (db, tx) =>
    {
        _ = ReadBatch(db, tx, id);
        Execute(db, tx, "UPDATE metadata_batches SET state='queued' WHERE id=$id AND state='preview'", ("$id", id));
        return ReadBatch(db, tx, id);
    });
    public ImportBatchReceipt PauseMetadata(string id, bool paused, string key) => Mutate("metadata/pause/" + id, key, new { paused }, (db, tx) =>
    {
        var current = ReadBatch(db, tx, id);
        if (current.State is not ("complete" or "preview")) Execute(db, tx, "UPDATE metadata_batches SET state=$state WHERE id=$id", ("$id", id), ("$state", paused ? "paused" : "queued"));
        return ReadBatch(db, tx, id);
    });
    public bool ProcessMetadataItem()
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var next = Command(db, tx, """
            SELECT b.id,b.add_to_library,i.ordinal,i.input_json FROM metadata_batches b JOIN metadata_import_items i ON i.batch_id=b.id
            WHERE b.state IN ('queued','processing') AND i.status='preview' ORDER BY b.created_at,i.ordinal LIMIT 1
            """);
        string batch; bool add; int index; NormalizedMetadata item;
        using (var reader = next.ExecuteReader())
        {
            if (!reader.Read()) return false;
            batch = reader.GetString(0); add = reader.GetBoolean(1); index = reader.GetInt32(2); item = Read<NormalizedMetadata>(reader.GetString(3));
        }
        // Effects, per-item operation receipt and cursor update share one transaction.
        var operationKey = item.OperationId + (add ? ":shelf" : ":catalog");
        using var prior = Command(db, tx, "SELECT result_json FROM metadata_operations WHERE operation_id=$id", ("$id", operationKey));
        ImportItemResult result;
        if (prior.ExecuteScalar() is string previous) result = Read<ImportItemResult>(previous) with { Index = index };
        else
        {
            result = ApplyMetadata(db, tx, item, index, batch, add);
            Execute(db, tx, "INSERT INTO metadata_operations VALUES($id,$json)", ("$id", operationKey), ("$json", Json(result)));
        }
        Execute(db, tx, "UPDATE metadata_import_items SET result_json=$result,status=$state WHERE batch_id=$batch AND ordinal=$index",
            ("$result", Json(result)), ("$state", result.Status), ("$batch", batch), ("$index", index));
        Execute(db, tx, "UPDATE metadata_batches SET state=CASE WHEN EXISTS(SELECT 1 FROM metadata_import_items WHERE batch_id=$id AND status='preview') THEN 'processing' ELSE 'complete' END WHERE id=$id", ("$id", batch));
        tx.Commit(); return true;
    }
    private static LibraryEntry? NovelEntry(SqliteConnection db, SqliteTransaction tx, string id)
    {
        using var get = Command(db, tx, "SELECT entry_json FROM novels WHERE id=$id", ("$id", id));
        return get.ExecuteScalar() is string json ? Deserialize(json) : null;
    }
    private static ImportItemResult ApplyMetadata(SqliteConnection db, SqliteTransaction tx, NormalizedMetadata item, int index, string batch, bool add)
    {
        if (item.Error is not null) return new(index, item.OperationId, item.VndbId, item.Title, "error", item.Error);
        string? shelf = null;
        var snapshot = item.Snapshot;
        if (snapshot?.Novel is { } novel)
        {
            var old = NovelEntry(db, tx, novel.Id);
            if (old is not null)
            {
                if (add) shelf = EnsureNovelItem(db, tx, novel.Id, item.Title);
                if (Json(old.Novel) == Json(novel) && Json(old.Producers) == Json(snapshot.Developers))
                    return new(index, item.OperationId, item.VndbId, item.Title, "existing", ItemId: shelf);
                var candidate = Candidate(db, tx, "metadata_conflict", item.VndbId, shelf, snapshot, [], "现有资料保留，导入快照作为候选；客户端时钟不决定覆盖顺序。");
                return new(index, item.OperationId, item.VndbId, item.Title, "needs_review", ItemId: shelf, CandidateId: candidate.Id);
            }
            SaveImportedSnapshot(db, tx, snapshot, batch, null);
            if (add) shelf = EnsureNovelItem(db, tx, novel.Id, item.Title);
            var job = snapshot.Cover.Status != "none" || snapshot.Completeness != "full_text" ? QueueMetadataJob(db, tx, novel.Id, item.Title, shelf) : null;
            return new(index, item.OperationId, item.VndbId, item.Title, "imported", ItemId: shelf, EnrichmentId: job?.Id);
        }
        if (snapshot?.Producer is { } producer)
        {
            using var get = Command(db, tx, "SELECT metadata_json FROM producers WHERE id=$id", ("$id", producer.Id));
            var current = get.ExecuteScalar() as string;
            if (current is not null)
            {
                if (current == Json(producer)) return new(index, item.OperationId, producer.Id, item.Title, "existing");
                var candidate = Candidate(db, tx, "metadata_conflict", producer.Id, null, snapshot, [], "已保留现有会社资料，请确认导入候选。");
                return new(index, item.OperationId, producer.Id, item.Title, "needs_review", CandidateId: candidate.Id);
            }
            SaveImportedSnapshot(db, tx, snapshot, batch, null);
            return new(index, item.OperationId, producer.Id, item.Title, "imported");
        }
        if (item.VndbId is { } id && id.StartsWith('v') && NovelEntry(db, tx, id) is not null)
        {
            if (add) shelf = EnsureNovelItem(db, tx, id, item.Title);
            return new(index, item.OperationId, id, item.Title, "existing", ItemId: shelf);
        }
        if (item.VndbId?.StartsWith('p') == true)
        {
            using var found = Command(db, tx, "SELECT 1 FROM producers WHERE id=$id", ("$id", item.VndbId));
            if (found.ExecuteScalar() is not null) return new(index, item.OperationId, item.VndbId, item.Title, "existing");
        }
        if (add && item.VndbId?.StartsWith('p') != true)
        {
            var now = DateTimeOffset.UtcNow; shelf = "item-import-" + item.OperationId;
            using var exists = Command(db, tx, "SELECT 1 FROM library_items WHERE id=$id", ("$id", shelf));
            if (exists.ExecuteScalar() is null) WriteItem(db, tx, new(shelf, new(item.Title), 1, now, now), "item-import");
        }
        var queued = QueueMetadataJob(db, tx, item.VndbId, item.Title, shelf);
        return new(index, item.OperationId, item.VndbId, item.Title, "queued", "清单已持久化，等待联网补全，不代表完整资料已入库。", shelf, EnrichmentId: queued.Id);
    }
    private static void SaveImportedSnapshot(SqliteConnection db, SqliteTransaction tx, MetadataSnapshot snapshot, string? batch, LibraryEntry? previous)
    {
        if (snapshot.Novel is { } novel)
        {
            var developers = new List<Producer>();
            foreach (var developer in novel.Developers)
            {
                var incoming = snapshot.Developers.FirstOrDefault(p => p.Id == developer.Id);
                using var get = Command(db, tx, "SELECT metadata_json FROM producers WHERE id=$id", ("$id", developer.Id));
                var prior = get.ExecuteScalar() as string;
                if (prior is not null)
                {
                    var kept = Read<Producer>(prior); developers.Add(kept);
                    if (incoming is not null && Json(incoming) != Json(kept))
                        Candidate(db, tx, "metadata_conflict", kept.Id, null, new(kept.Id, incoming.Name, "producer", null, [], incoming,
                            new("not_applicable"), snapshot.FetchedAt, "client_vndb_import"), [], "快照所附会社资料与现有资料不同，已保留候选。");
                }
                else developers.Add(incoming ?? new Producer { Id = developer.Id, Name = developer.Name, Original = developer.Original });
            }
            var retainedCover = novel.Image is not null && previous?.Novel.Image?.Url == novel.Image.Url ? previous.Cover : null;
            var entry = new LibraryEntry(novel, developers, retainedCover, novel.Image is null ? "none" : retainedCover is not null ? "ready" : "failed",
                novel.Image is null ? null : "离线快照已收录，封面等待补全。", previous?.CreatedAt ?? DateTimeOffset.UtcNow, snapshot.FetchedAt, "client_vndb_import", snapshot.Completeness);
            entry = WriteLibraryEntry(db, tx, entry);
        }
        else if (snapshot.Producer is { } producer)
            Execute(db, tx, """
                INSERT INTO producers VALUES($id,$name,$original,$kind,$lang,$json,$at)
                ON CONFLICT(id) DO UPDATE SET name=excluded.name,original=excluded.original,kind=excluded.kind,lang=excluded.lang,metadata_json=excluded.metadata_json,updated_at=excluded.updated_at
                """, ("$id", producer.Id), ("$name", producer.Name), ("$original", producer.Original), ("$kind", producer.Type), ("$lang", producer.Lang), ("$json", Json(producer)), ("$at", snapshot.FetchedAt.ToString("O")));
        WriteProvenance(db, tx, snapshot.VndbId, "client_vndb_import", snapshot.FetchedAt, snapshot.Completeness, batch, null);
        Event(db, tx, snapshot.VndbId, "metadata-import", previous is null ? null : Json(previous), Json(snapshot));
    }
    private static ImportCandidate Candidate(SqliteConnection db, SqliteTransaction tx, string kind, string? novelId, string? itemId,
        MetadataSnapshot? snapshot, IReadOnlyList<MetadataSnapshot> matches, string message)
    {
        long revision = 0;
        if (kind == "metadata_conflict")
        {
            using var get = Command(db, tx, "SELECT revision FROM metadata_provenance WHERE vndb_id=$id", ("$id", novelId));
            revision = Convert.ToInt64(get.ExecuteScalar() ?? 0);
        }
        else if (itemId is not null) revision = Item(db, tx, itemId).Revision;
        var candidate = new ImportCandidate("candidate-" + Guid.NewGuid().ToString("N"), kind, novelId, itemId, revision, snapshot, matches, "pending", message);
        Execute(db, tx, "INSERT INTO metadata_candidates VALUES($id,$json)", ("$id", candidate.Id), ("$json", Json(candidate)));
        Event(db, tx, candidate.Id, "candidate-create", null, Json(candidate)); return candidate;
    }
    private static MetadataJob QueueMetadataJob(SqliteConnection db, SqliteTransaction tx, string? id, string title, string? itemId)
    {
        var key = PlayRules.Hash(System.Text.Encoding.UTF8.GetBytes((id ?? title) + "\0" + itemId));
        using var get = Command(db, tx, "SELECT record_json FROM metadata_jobs WHERE id=$id", ("$id", key));
        if (get.ExecuteScalar() is string current) return Read<MetadataJob>(current);
        var job = new MetadataJob(key, id, title, itemId, "queued", 0, DateTimeOffset.UtcNow, null,
            ItemRevision: itemId is null ? null : Item(db, tx, itemId).Revision);
        Execute(db, tx, "INSERT INTO metadata_jobs VALUES($id,$json)", ("$id", key), ("$json", Json(job))); return job;
    }
    public IReadOnlyList<ImportCandidate> MetadataCandidates()
    {
        using var db = Open(); using var select = Command(db, null, "SELECT record_json FROM metadata_candidates ORDER BY id");
        using var reader = select.ExecuteReader(); var result = new List<ImportCandidate>();
        while (reader.Read()) result.Add(Read<ImportCandidate>(reader.GetString(0))); return result;
    }
    public ImportCandidate DecideCandidate(string id, CandidateDecision decision, bool accept, string key) =>
        Mutate("candidate/" + id, key, new { decision, accept }, (db, tx) =>
        {
            using var select = Command(db, tx, "SELECT record_json FROM metadata_candidates WHERE id=$id", ("$id", id));
            var candidate = select.ExecuteScalar() is string json ? Read<ImportCandidate>(json) : throw new ArgumentException("候选不存在。");
            if (candidate.State != "pending" || candidate.ExpectedRevision != decision.ExpectedRevision) throw new PlayConflictException("候选状态已改变。");
            if (accept && candidate.Kind == "metadata_conflict")
            {
                using var revision = Command(db, tx, "SELECT revision FROM metadata_provenance WHERE vndb_id=$id", ("$id", candidate.VndbId));
                if (Convert.ToInt64(revision.ExecuteScalar() ?? 0) != decision.ExpectedRevision) throw new PlayConflictException("公开资料已更新，请重新比较后确认。");
                var snapshot = candidate.Incoming!;
                SaveImportedSnapshot(db, tx, snapshot, null, snapshot.Kind == "vn" ? NovelEntry(db, tx, snapshot.VndbId) : null);
                if (snapshot.Novel?.Image is not null || snapshot.Completeness != "full_text") QueueMetadataJob(db, tx, snapshot.VndbId, snapshot.Title, candidate.ItemId);
            }
            else if (accept && candidate.Kind == "title_matches")
            {
                if (decision.NovelId is null || !candidate.Matches.Any(n => n.VndbId == decision.NovelId)) throw new ArgumentException("请选择本次候选中的作品。");
                if (candidate.ItemId is { } itemId) LinkItemCore(db, tx, itemId, new(decision.NovelId, decision.ExpectedRevision));
            }
            var result = candidate with { State = accept ? "accepted" : "rejected" };
            Execute(db, tx, "UPDATE metadata_candidates SET record_json=$json WHERE id=$id", ("$json", Json(result)), ("$id", id));
            Event(db, tx, id, "candidate-decision", Json(candidate), Json(result)); return result;
        });
}
