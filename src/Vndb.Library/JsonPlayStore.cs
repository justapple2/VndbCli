using System.Text.Json;
using Vndb.Core;

namespace Vndb.Library;

public sealed partial class JsonLibraryStore
{
    private readonly object _playGate = new();
    private string PlayFolder => Path.Combine(DataDirectory, "plays");
    private string PlayPath(string id)
    {
        if (!id.StartsWith("play-", StringComparison.Ordinal) || id.Length > 80 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            throw new ArgumentException("无效的游玩记录 ID。");
        return Path.Combine(PlayFolder, id + ".json");
    }
    public PlayRecord? GetPlay(string id) => File.Exists(PlayPath(id))
        ? JsonSerializer.Deserialize<PlayRecord>(File.ReadAllText(PlayPath(id)), VndbJson.Options) : null;
    private IEnumerable<PlayRecord> AllPlays() => Directory.Exists(PlayFolder)
        ? Directory.EnumerateFiles(PlayFolder, "play-*.json").Select(path => JsonSerializer.Deserialize<PlayRecord>(File.ReadAllText(path), VndbJson.Options)
            ?? throw new InvalidDataException("个人游玩记录损坏。")) : [];
    private void WritePlay(PlayRecord record, string kind)
    {
        Directory.CreateDirectory(PlayFolder);
        var temporary = Path.Combine(PlayFolder, "." + Guid.NewGuid().ToString("N") + ".tmp");
        var json = JsonSerializer.Serialize(record, VndbJson.Options);
        try { File.WriteAllText(temporary, json); File.Move(temporary, PlayPath(record.Id), overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        File.WriteAllText(Path.Combine(DataDirectory, "audit", "play-" + Guid.NewGuid().ToString("N") + ".json"),
            JsonSerializer.Serialize(new { kind, record }, VndbJson.Options));
    }
    public PlayRecord SavePlay(PlayDetails details, string? id = null, long? expectedRevision = null)
    {
        PlayRules.Validate(details, Get);
        lock (_playGate)
        {
            var old = id is null ? null : GetPlay(id) ?? throw new ArgumentException("游玩记录不存在。");
            if (old is not null && old.Revision != expectedRevision) throw new PlayConflictException("记录已更新，请重新载入后再保存。");
            if (old?.Details == details) return old;
            var now = DateTimeOffset.UtcNow;
            var record = new PlayRecord(old?.Id ?? "play-" + Guid.NewGuid().ToString("N"), details, old?.Source,
                (old?.Revision ?? 0) + 1, old?.CreatedAt ?? now, now);
            WritePlay(record, old is null ? "create" : "edit");
            return record;
        }
    }
    public PlayPage Plays(string? query = null, string? status = null, bool? played = null, int? year = null,
        bool unmatched = false, string? novelId = null, int page = 1, int limit = 24) =>
        PlayRules.Page(AllPlays(), query, status, played, year, unmatched, novelId, page, limit);
    public PlayStatistics PlayStatistics() => PlayRules.Statistics(AllPlays());
    public PlayImportResult ImportPlays(PlayImportBatch batch)
    {
        PlayRules.ValidateBatch(batch);
        var map = PlayRules.TitleMap(this);
        var prepared = batch.Rows.Select(row =>
        {
            var details = row.Details;
            if (details.NovelId is null && map.TryGetValue(PlayRules.NormalizeTitle(details.Title), out var id)) details = details with { NovelId = id };
            PlayRules.Validate(details, Get);
            return (Row: row, Details: details, Id: PlayRules.ImportId(batch, row));
        }).ToArray();
        lock (_playGate)
        {
            Directory.CreateDirectory(PlayFolder);
            var batchPath = Path.Combine(PlayFolder, "batch-" + batch.SourceSha256 + ".json");
            var hash = PlayRules.BatchHash(batch);
            if (File.Exists(batchPath))
            {
                using var previous = JsonDocument.Parse(File.ReadAllText(batchPath));
                if (previous.RootElement.GetProperty("payload_sha256").GetString() != hash)
                    throw new PlayConflictException("同一源文件哈希对应的导入数据发生变化，未覆盖已有个人记录。");
            }
            var inserted = 0;
            var linked = 0;
            var now = DateTimeOffset.UtcNow;
            foreach (var item in prepared)
            {
                var existing = GetPlay(item.Id);
                if (existing is not null) { if (existing.Details.NovelId is not null) linked++; continue; }
                var source = new PlaySource(batch.SourceFileName, batch.SourceSha256, item.Row.Sheet, item.Row.Row,
                    item.Row.MonthLabel, item.Row.RawJson, item.Row.Warnings);
                WritePlay(new(item.Id, item.Details, source, 1, now, now), "import");
                inserted++;
                if (item.Details.NovelId is not null) linked++;
            }
            if (!File.Exists(batchPath)) File.WriteAllText(batchPath, JsonSerializer.Serialize(new { batch.SourceFileName, batch.SourceSha256, payload_sha256 = hash, row_count = batch.Rows.Count, imported_at = now }, VndbJson.Options));
            return new(batch.SourceSha256, batch.Rows.Count, inserted, batch.Rows.Count - inserted, linked, batch.Rows.Count - linked);
        }
    }
}
