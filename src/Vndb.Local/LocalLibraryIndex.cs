using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

public sealed record LocalIndexPreview(string Directory, string LegacySha256, long LegacyBytes, long IndexBytes,
    int Items, int Installations, int MetadataFiles, string Format = "vn-local-index-preview", int FormatVersion = 1);

public sealed partial class LocalLibraryStore
{
    // Explicit format-specific budgets; the legacy/config/outbox 8MiB bound stays unchanged.
    public const long IndexMaxBytes = 32 * 1024 * 1024;
    private const long MigrationMaxBytes = 256 * 1024 * 1024;
    public string IndexPath => Path.Combine(DirectoryPath, "local-library.v2.json");
    private JsonStateFile<JsonObject> IndexState() => new(DirectoryPath, "local-library.v2.json", IndexMaxBytes);
    private string? _indexObserved;
    private static string Sha(byte[]? bytes) => bytes is null ? "missing" : Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static T ReadNode<T>(JsonNode node) => node.Deserialize<T>(VndbJson.Options) ?? throw new InvalidDataException("本机索引无效，原件保留。");
    private static JsonObject Object<T>(T value) => JsonSerializer.SerializeToNode(value, VndbJson.Options)!.AsObject();
    private async Task<byte[]?> LegacyBytesAsync(CancellationToken ct) => await new JsonStateFile<LocalLibrary>(DirectoryPath, "local-library.json", MigrationMaxBytes).BytesAsync(ct);
    private async Task<string> LegacyShaAsync(CancellationToken ct)
    {
        LocalFileLease.RequireOrdinaryAncestors(FilePath);
        if (!File.Exists(FilePath)) return "missing";
        if ((File.GetAttributes(FilePath) & FileAttributes.ReparsePoint) != 0 || new FileInfo(FilePath).Length > MigrationMaxBytes) throw new InvalidDataException("旧登记不是普通文件或超过迁移上限。");
        await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
    private async Task<JsonObject> ReadIndexNodeAsync(CancellationToken ct, bool observe = true)
    {
        var bytes = await IndexState().BytesAsync(ct) ?? throw new InvalidDataException("本机索引缺失。");
        var node = JsonNode.Parse(bytes)?.AsObject() ?? throw new InvalidDataException("本机索引无效。");
        if ((string?)node["format"] != "vn-local-library-index" || (int?)node["format_version"] != 2 || node["library"] is not JsonObject || node["metadata_refs"] is not JsonObject ||
            (string?)node["legacy_sha256"] != await LegacyShaAsync(ct))
            throw new InvalidDataException("本机索引版本不兼容或旧登记已变化，请人工核对；未覆盖任一文件。");
        if (observe) _indexObserved = Sha(bytes); return node;
    }
    private async Task<LocalLibrary> ReadIndexAsync(bool details, CancellationToken ct)
    {
        var node = await ReadIndexNodeAsync(ct); var libraryNode = details ? node["library"]!.DeepClone().AsObject() : node["library"]!.AsObject();
        var library = ReadNode<LocalLibrary>(libraryNode); Validate(library);
        var refs = node["metadata_refs"]!.AsObject(); var ids = library.Items.Select(i => i.Item.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != library.Items.Count || refs.Any(r => !ids.Contains(r.Key) || r.Value is null || r.Value.GetValue<string>() is not { Length: 64 } hash || hash.Any(c => !char.IsAsciiHexDigitLower(c)))) throw new InvalidDataException("本机索引条目或详情引用无效。");
        if (!details) return library;
        if (details)
            foreach (var row in libraryNode["items"]!.AsArray())
                if (refs[(string)row!["item"]!["id"]!] is { } reference)
                {
                    var raw = await MetadataBytesAsync(reference.GetValue<string>(), ct);
                    var metadata = ReadNode<MetadataSnapshot>(raw);
                    if (metadata.VndbId != (string?)row["item"]!["details"]!["novel_id"]) throw new InvalidDataException("详情快照与本机条目关联不符。");
                    row["metadata"] = raw;
                }
        library = ReadNode<LocalLibrary>(libraryNode); Validate(library); return library;
    }
    private async Task<JsonObject> MetadataBytesAsync(string hash, CancellationToken ct)
    {
        if (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigitLower(c))) throw new InvalidDataException("详情引用无效。");
        var bytes = await new JsonStateFile<JsonObject>(DirectoryPath, "local-metadata/" + hash + ".json").BytesAsync(ct);
        if (bytes is null || Sha(bytes) != hash) throw new InvalidDataException("详情快照缺失或摘要不符；登记与原件保留。");
        return JsonNode.Parse(bytes)?.AsObject() ?? throw new InvalidDataException("详情快照无效。");
    }
    private async Task<(JsonObject Library, JsonObject Refs)> SplitAsync(JsonObject full, bool write, CancellationToken ct)
    {
        var library = full.DeepClone().AsObject(); var refs = new JsonObject(); var seen = new HashSet<string>(StringComparer.Ordinal); long total = 0;
        if (library["items"]!.AsArray().Count > 10000) throw new InvalidDataException("本机索引最多10000个作品条目，请缩小登记范围。");
        foreach (var row in library["items"]!.AsArray())
        {
            ct.ThrowIfCancellationRequested();
            if (row!["metadata"] is not JsonObject metadata) continue;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata, VndbJson.Options);
            if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("单条详情超过8MiB，未切换索引。");
            var hash = Sha(bytes); refs[(string)row["item"]!["id"]!] = hash;
            if (seen.Add(hash)) total += bytes.LongLength;
            if (total > MigrationMaxBytes) throw new InvalidDataException("本机索引引用的详情超过256MiB，请缩小登记范围；原件保留。");
            row["metadata"] = JsonSerializer.SerializeToNode(ShelfSummaries.Metadata(ReadNode<MetadataSnapshot>(metadata)), VndbJson.Options);
            if (write)
            {
                var file = Path.Combine(DirectoryPath, "local-metadata", hash + ".json");
                if (!File.Exists(file)) await JsonSnapshotStore.AtomicBytesAsync(file, bytes, ct);
                await MetadataBytesAsync(hash, ct);
            }
        }
        return (library, refs);
    }
    private static JsonObject Envelope(JsonObject library, JsonObject refs, string legacy) => new()
    { ["format"] = "vn-local-library-index", ["format_version"] = 2, ["legacy_sha256"] = legacy, ["library"] = library, ["metadata_refs"] = refs };
    public async Task<LocalIndexPreview> PreviewIndexAsync(CancellationToken ct = default)
    {
        if (File.Exists(IndexPath)) throw new InvalidOperationException("本机索引已启用，不重复迁移。");
        var bytes = await LegacyBytesAsync(ct); var raw = bytes is null ? Object(new LocalLibrary([], [])) : JsonNode.Parse(bytes)!.AsObject();
        var library = ReadNode<LocalLibrary>(raw); Validate(library);
        var split = await SplitAsync(raw, false, ct); var envelope = Envelope(split.Library, split.Refs, Sha(bytes));
        var size = JsonSerializer.SerializeToUtf8Bytes(envelope, VndbJson.Options).LongLength;
        if (size > IndexMaxBytes) throw new InvalidDataException("拆分后权威索引仍超过32MiB，请缩小登记范围；原件保留。");
        return new(DirectoryPath, Sha(bytes), bytes?.LongLength ?? 0, size, library.Items.Count, library.Installations.Count, split.Refs.Select(r => r.Value!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count());
    }
    public async Task ApplyIndexAsync(LocalIndexPreview approved, string approvedSha256, CancellationToken ct = default)
    {
        using var format = await LocalFileLease.AcquireAsync(DirectoryPath, "local-library-format", ct);
        using var legacyLease = await LocalFileLease.AcquireAsync(DirectoryPath, "json-state/local-library.json", ct);
        if (LocalArchiveFormat.Hash(approved) != approvedSha256 || approved != await PreviewIndexAsync(ct)) throw new InvalidDataException("迁移预览或原件已变化，请重新确认。");
        var bytes = await LegacyBytesAsync(ct); var raw = bytes is null ? Object(new LocalLibrary([], [])) : JsonNode.Parse(bytes)!.AsObject();
        var split = await SplitAsync(raw, true, ct);
        if (Sha(await LegacyBytesAsync(ct)) != approved.LegacySha256) throw new InvalidDataException("原件变化，未切换索引。");
        await JsonSnapshotStore.AtomicWriteAsync(IndexPath, Envelope(split.Library, split.Refs, approved.LegacySha256), false, ct, IndexMaxBytes);
    }
    private async Task SaveIndexAsync(LocalLibrary value, CancellationToken ct)
    {
        var expected = _indexObserved;
        using var lease = await LocalFileLease.AcquireAsync(DirectoryPath, "json-state/local-library.v2.json", ct);
        var envelope = await ReadIndexNodeAsync(ct, false);
        if (expected is null || expected != Sha(await IndexState().BytesAsync(ct))) throw new InvalidDataException("本机索引已被其他客户端更新，请重新读取。");
        var full = envelope["library"]!.DeepClone().AsObject();
        foreach (var row in full["items"]!.AsArray())
            if (envelope["metadata_refs"]![(string)row!["item"]!["id"]!] is { } reference) row["metadata"] = await MetadataBytesAsync(reference.GetValue<string>(), ct);
        var prior = ReadNode<LocalLibrary>(full);
        var incoming = Object(value);
        var summaries = envelope["library"]!["items"]!.AsArray().ToDictionary(n => (string)n!["item"]!["id"]!, StringComparer.Ordinal);
        var details = full["items"]!.AsArray().ToDictionary(n => (string)n!["item"]!["id"]!, StringComparer.Ordinal);
        foreach (var row in incoming["items"]!.AsArray())
        {
            var id = (string)row!["item"]!["id"]!;
            var summary = summaries.GetValueOrDefault(id);
            var complete = details.GetValueOrDefault(id);
            if (summary is not null && complete is not null && JsonNode.DeepEquals(row["metadata"], summary["metadata"])) row["metadata"] = complete["metadata"]?.DeepClone();
        }
        var merged = MergeUnknown(full, Object(prior), incoming)!.AsObject();
        if (JsonNode.DeepEquals(full, merged)) return;
        var split = await SplitAsync(merged, true, ct); envelope["library"] = split.Library; envelope["metadata_refs"] = split.Refs;
        await JsonSnapshotStore.AtomicWriteAsync(IndexPath, envelope, true, ct, IndexMaxBytes);
        _indexObserved = Sha(await IndexState().BytesAsync(ct));
    }
    // Preserve unknown fields at every level, including stable-ID array members, when a known field changes.
    private static JsonNode? MergeUnknown(JsonNode? raw, JsonNode? oldValue, JsonNode? newValue)
    {
        if (JsonNode.DeepEquals(oldValue, newValue)) return raw?.DeepClone();
        if (raw is JsonObject original && oldValue is JsonObject oldObject && newValue is JsonObject changed)
        {
            var result = original.DeepClone().AsObject();
            foreach (var key in oldObject.Select(p => p.Key).Concat(changed.Select(p => p.Key)).Distinct())
                result[key] = MergeUnknown(original[key], oldObject[key], changed[key]);
            return result;
        }
        if (raw is JsonArray originalArray && oldValue is JsonArray oldArray && newValue is JsonArray newArray)
        {
            static string? Id(JsonNode? n) => (string?)(n?["id"] ?? n?["item"]?["id"] ?? n?["installation"]?["id"]);
            if (newArray.Concat(oldArray).All(n => n is JsonObject && Id(n) is not null))
            {
                var result = new JsonArray();
                var positions = oldArray.Select((n, i) => (Id: Id(n)!, Index: i)).ToDictionary(p => p.Id, p => p.Index, StringComparer.Ordinal);
                foreach (var member in newArray)
                {
                    result.Add(positions.TryGetValue(Id(member)!, out var i) ? MergeUnknown(originalArray[i], oldArray[i], member) : member?.DeepClone());
                }
                return result;
            }
        }
        return newValue?.DeepClone();
    }
}
