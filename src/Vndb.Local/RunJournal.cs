using System.Text;
using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

public sealed record JournalData(long Sequence, string Step, DateTimeOffset At, string Previous, string PayloadSha256);
public sealed record JournalLine(JournalData Data, string Sha256);

/// <summary>Original segments are never rewritten; a torn tail is sealed into a linked recovery segment.</summary>
public sealed class RunJournal : IDisposable
{
    private readonly string _directory;
    private readonly FileStream _lock;
    private int _segment;
    private readonly List<JournalLine> _lines = [];
    public string Head => _lines.LastOrDefault()?.Sha256 ?? new string('0', 64);
    public RunJournal(string directory)
    {
        _directory = Path.GetFullPath(directory); LocalFiles.CheckAncestors(_directory); Directory.CreateDirectory(_directory);
        LocalFiles.CheckAncestors(Path.Combine(_directory, ".run-lock"));
        _lock = new FileStream(Path.Combine(_directory, ".run-lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var files = Directory.GetFiles(_directory, "journal-*.jsonl").Order(StringComparer.Ordinal).ToArray(); string? tail = null;
            for (var index = 0; index < files.Length; index++)
            {
                if (Path.GetFileName(files[index]) != $"journal-{index:D6}.jsonl") throw new InvalidDataException("日志分段缺失。");
                LocalFiles.CheckAncestors(files[index]);
                var bytes = File.ReadAllBytes(files[index]); var start = 0; var first = true;
                for (var end = 0; end < bytes.Length; end++)
                {
                    if (bytes[end] != '\n') continue;
                    var line = JsonSerializer.Deserialize<JournalLine>(bytes.AsSpan(start, end - start), VndbJson.Options) ?? throw new InvalidDataException("日志为空。");
                    if (first && tail is not null && (line.Data.Step != "recovered_tail" || line.Data.PayloadSha256 != tail)) throw new InvalidDataException("日志尾部恢复链不符。");
                    if (line.Data.Sequence != _lines.Count + 1 || line.Data.Previous != Head || !LocalArchiveFormat.MatchesHash(line.Data, line.Sha256)) throw new InvalidDataException("日志哈希链损坏，保留原件停止执行。");
                    _lines.Add(line); first = false; tail = null; start = end + 1;
                }
                if (start < bytes.Length) tail = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes.AsSpan(start)));
                _segment = index;
            }
            if (tail is not null) { _segment++; Append("recovered_tail", tail); }
        }
        catch { _lock.Dispose(); throw; }
    }
    public bool Has(string step) => _lines.Any(l => l.Data.Step == step);
    public void BindValue<T>(string step, T value) => Bind(step, LocalArchiveFormat.Hash(value), LocalArchiveFormat.LegacyWindowsHash(value));
    public void Bind(string step, string hash, string? legacyHash = null)
    {
        var existing = _lines.FirstOrDefault(l => l.Data.Step == step);
        if (existing is not null && existing.Data.PayloadSha256 != hash && existing.Data.PayloadSha256 != legacyHash) throw new InvalidDataException("持久意图与批准日志不符。");
        if (existing is null) Append(step, hash);
    }
    public void Append(string step, string payloadSha256)
    {
        var data = new JournalData(_lines.Count + 1, step, DateTimeOffset.UtcNow, Head, payloadSha256); var line = new JournalLine(data, LocalArchiveFormat.Hash(data));
        var compact = new JsonSerializerOptions(VndbJson.Options) { WriteIndented = false };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, compact) + "\n");
        LocalFiles.CheckAncestors(Path.Combine(_directory, $"journal-{_segment:D6}.jsonl"));
        using var file = new FileStream(Path.Combine(_directory, $"journal-{_segment:D6}.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        file.Write(bytes); file.Flush(true); _lines.Add(line);
    }
    public void Dispose() => _lock.Dispose();
    public static T Read<T>(string file)
    {
        LocalFiles.CheckAncestors(file);
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(file), VndbJson.Options) ?? throw new InvalidDataException("持久记录无效。");
    }
    public static void Write<T>(string file, T value)
    {
        LocalFiles.CheckAncestors(file);
        if (File.Exists(file)) { if (LocalArchiveFormat.Hash(Read<T>(file)) != LocalArchiveFormat.Hash(value)) throw new InvalidDataException("不可覆盖不同的持久记录。"); return; }
        var temporary = file + ".writing-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { stream.Write(LocalArchiveFormat.Serialize(value)); stream.Flush(true); }
        File.Move(temporary, file); // Metadata-only atomic publication; never overwrites existing intent.
    }
}
