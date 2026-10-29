using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vndb.Core;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    private static PlayRecord ReadPlay(string json) => JsonSerializer.Deserialize<PlayRecord>(json, VndbJson.Options)
        ?? throw new InvalidDataException("个人游玩记录损坏。");
    public PlayRecord? GetPlay(string id)
    {
        using var connection = Open();
        using var command = Command(connection, null, "SELECT record_json FROM play_records WHERE id=$id", ("$id", id));
        return command.ExecuteScalar() is string json ? ReadPlay(json) : null;
    }
    private IReadOnlyList<PlayRecord> AllPlays()
    {
        using var connection = Open();
        using var command = Command(connection, null, "SELECT record_json FROM play_records");
        using var reader = command.ExecuteReader();
        var result = new List<PlayRecord>();
        while (reader.Read()) result.Add(ReadPlay(reader.GetString(0)));
        return result;
    }
    public PlayRecord SavePlay(PlayDetails details, string? id = null, long? expectedRevision = null)
    {
        PlayRules.Validate(details, Get);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        PlayRecord? existing = null;
        if (id is not null)
        {
            using var command = Command(connection, transaction, "SELECT record_json FROM play_records WHERE id=$id", ("$id", id));
            existing = command.ExecuteScalar() is string json ? ReadPlay(json) : throw new ArgumentException("游玩记录不存在。");
            if (existing.DeletedAt is not null) throw new PlayConflictException("记录已移除，请先明确恢复。");
            if (expectedRevision != existing.Revision) throw new PlayConflictException("记录已更新，请重新载入后再保存，避免覆盖其他编辑。");
            if (existing.Details == details) { transaction.Commit(); return existing; }
        }
        var now = DateTimeOffset.UtcNow;
        var result = new PlayRecord(existing?.Id ?? "play-" + Guid.NewGuid().ToString("N"), details, existing?.Source,
            (existing?.Revision ?? 0) + 1, existing?.CreatedAt ?? now, now);
        WritePlay(connection, transaction, result, existing is null ? "create" : "edit");
        transaction.Commit();
        return result;
    }
    private static void WritePlay(SqliteConnection connection, SqliteTransaction transaction, PlayRecord record, string kind, string? parentItemId = null)
    {
        using var previous = Command(connection, transaction, "SELECT record_json FROM play_records WHERE id=$id", ("$id", record.Id));
        var before = previous.ExecuteScalar() as string;
        var d = record.Details;
        var json = JsonSerializer.Serialize(record, VndbJson.Options);
        Execute(connection, transaction, """
            INSERT INTO play_records(id,novel_id,title,status,started_on,finished_on,duration_minutes,score,
                source_hash,source_sheet,source_row,record_json,revision,created_at,updated_at)
            VALUES($id,$vn,$title,$status,$start,$end,$minutes,$score,$hash,$sheet,$row,$json,$revision,$created,$updated)
            ON CONFLICT(id) DO UPDATE SET novel_id=excluded.novel_id,title=excluded.title,status=excluded.status,
                started_on=excluded.started_on,finished_on=excluded.finished_on,duration_minutes=excluded.duration_minutes,
                score=excluded.score,record_json=excluded.record_json,revision=excluded.revision,updated_at=excluded.updated_at;
            """, ("$id", record.Id), ("$vn", d.NovelId), ("$title", d.Title), ("$status", d.Status),
            ("$start", d.StartedOn?.ToString("yyyy-MM-dd")), ("$end", d.FinishedOn?.ToString("yyyy-MM-dd")),
            ("$minutes", d.DurationMinutes), ("$score", d.Score), ("$hash", record.Source?.Sha256),
            ("$sheet", record.Source?.Sheet), ("$row", record.Source?.Row), ("$json", json), ("$revision", record.Revision),
            ("$created", record.CreatedAt.ToString("O")), ("$updated", record.UpdatedAt.ToString("O")));
        Execute(connection, transaction, "INSERT INTO play_events(play_id,kind,recorded_at,snapshot_json) VALUES($id,$kind,$at,$json)",
            ("$id", record.Id), ("$kind", kind), ("$at", record.UpdatedAt.ToString("O")), ("$json", json));
        Event(connection, transaction, record.Id, "play-" + kind, before, json);
        if (parentItemId is null) EnsurePlayItem(connection, transaction, record);
        else
        {
            var parent = Item(connection, transaction, parentItemId);
            if (parent.DeletedAt is not null || parent.Details.NovelId != record.Details.NovelId) throw new PlayConflictException("书架条目已更新，请重新载入。");
            Execute(connection, transaction, "INSERT INTO play_item_links VALUES($play,$item) ON CONFLICT(play_id) DO UPDATE SET item_id=excluded.item_id", ("$play", record.Id), ("$item", parentItemId));
        }
    }
    public PlayPage Plays(string? query = null, string? status = null, bool? played = null, int? year = null,
        bool unmatched = false, string? novelId = null, int page = 1, int limit = 24) =>
        PlayRules.Page(AllPlays(), query, status, played, year, unmatched, novelId, page, limit);
    public PlayStatistics PlayStatistics() => PlayRules.Statistics(AllPlays());

    public PlayImportResult ImportPlays(PlayImportBatch batch)
    {
        PlayRules.ValidateBatch(batch);
        // Metadata cached by an unrelated search does not establish a globally unique identity.
        var map = CatalogLookups().Where(l => l.Status == "matched" && l.NovelId is not null &&
            l.Key == CatalogRules.Key(l.Title) && Get(l.NovelId) is not null).ToDictionary(l => l.Key, l => l.NovelId!, StringComparer.Ordinal);
        var prepared = batch.Rows.Select(row =>
        {
            var details = row.Details;
            if (details.NovelId is null && map.TryGetValue(PlayRules.NormalizeTitle(details.Title), out var matched))
                details = details with { NovelId = matched };
            PlayRules.Validate(details, Get);
            return (Row: row, Details: details, Id: PlayRules.ImportId(batch, row));
        }).ToArray();
        var fingerprint = PlayRules.BatchHash(batch);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var find = Command(connection, transaction, "SELECT payload_sha256 FROM play_import_batches WHERE source_sha256=$hash", ("$hash", batch.SourceSha256));
        if (find.ExecuteScalar() is string previous && previous != fingerprint)
            throw new PlayConflictException("同一源文件哈希对应的导入数据发生变化，未覆盖已有个人记录。");
        var inserted = 0;
        var linked = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var item in prepared)
        {
            using var exists = Command(connection, transaction, "SELECT novel_id FROM play_records WHERE id=$id", ("$id", item.Id));
            using var reader = exists.ExecuteReader();
            if (reader.Read()) { if (!reader.IsDBNull(0)) linked++; continue; }
            reader.Close();
            var source = new PlaySource(batch.SourceFileName, batch.SourceSha256, item.Row.Sheet, item.Row.Row,
                item.Row.MonthLabel, item.Row.RawJson, item.Row.Warnings);
            WritePlay(connection, transaction, new(item.Id, item.Details, source, 1, now, now), "import");
            inserted++;
            if (item.Details.NovelId is not null) linked++;
        }
        Execute(connection, transaction, "INSERT INTO play_import_batches VALUES($hash,$file,$rows,$payload,$at) ON CONFLICT(source_sha256) DO NOTHING",
            ("$hash", batch.SourceSha256), ("$file", batch.SourceFileName), ("$rows", batch.Rows.Count), ("$payload", fingerprint), ("$at", now.ToString("O")));
        transaction.Commit();
        return new(batch.SourceSha256, batch.Rows.Count, inserted, batch.Rows.Count - inserted, linked, batch.Rows.Count - linked);
    }
}
