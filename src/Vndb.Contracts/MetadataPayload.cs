using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Vndb.Core;

namespace Vndb.Contracts;

public static class MetadataPayload
{
    public static (IReadOnlyList<NormalizedMetadata> Items, string? ClientId, string RawSha) Parse(JsonElement root, ImportLimits limits)
    {
        ImportValidation.Metadata(root, limits);
        UniqueProperties(root);
        string? clientId = null;
        JsonElement[] rows;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("仅接受明确版本的 metadata 批次或已知旧导出结构。");
        if (root.TryGetProperty("format", out _))
        {
            if (Text(root, "format") != "vn-library-batch" || !root.TryGetProperty("format_version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != 1 || Text(root, "kind") != "metadata")
                throw new ArgumentException("不支持此 JSON 版本或种类。");
            clientId = Text(root, "batch_id");
            if (root.TryGetProperty("batch_id", out var batchValue) && (batchValue.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(clientId)) ||
                clientId is { Length: > 160 } || clientId?.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')) == true) throw new ArgumentException("batch_id 无效。");
            rows = Array(root, "items");
        }
        else if (root.TryGetProperty("items", out _) && root.TryGetProperty("source", out _)) rows = Array(root, "items"); // M1 CLI response.
        else if (root.TryGetProperty("results", out _) && root.TryGetProperty("more", out _)) rows = Array(root, "results"); // old CLI query page.
        else if (root.TryGetProperty("novel", out _) || root.TryGetProperty("id", out _)) rows = [root];
        else throw new ArgumentException("无法识别导出结构；不会猜测任意 JSON 中的字段。");
        if (rows.Length < 1) throw new ArgumentException("每批至少需要1项。");
        limits.RequireItems(rows.Length);
        var items = rows.Select((row, index) =>
        {
            try { return Normalize(row); }
            catch (Exception e) when (e is ArgumentException or JsonException or InvalidDataException or InvalidOperationException or FormatException)
            { return new NormalizedMetadata("invalid-" + index, null, "无效条目 " + (index + 1), null, "invalid", "条目字段、身份或图片地址无效。"); }
        }).ToArray();
        return (items, clientId, Hash(Encoding.UTF8.GetBytes(root.GetRawText())));
    }
    private static NormalizedMetadata Normalize(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new ArgumentException("条目需要对象。");
        MetadataSnapshot? snapshot = null;
        var id = Text(row, "vndb_id") ?? Text(row, "id");
        var title = Text(row, "title") ?? Text(row, "name");
        if (row.TryGetProperty("novel", out var n) && n.ValueKind == JsonValueKind.Object)
        {
            id ??= Text(n, "id"); title ??= Text(n, "title");
            if (FullNovel(n))
            {
                var novel = n.Deserialize<VisualNovel>(VndbJson.Options) ?? throw new ArgumentException();
                var producers = row.TryGetProperty("developers", out var dev) && dev.ValueKind == JsonValueKind.Array ? dev :
                    row.TryGetProperty("producers", out var prod) && prod.ValueKind == JsonValueKind.Array ? prod : default;
                snapshot = Novel(novel, producers.ValueKind == JsonValueKind.Array ? producers.EnumerateArray().Where(FullProducer).Select(p => p.Deserialize<Producer>(VndbJson.Options)!).ToArray() : [],
                    Date(row, "fetched_at") ?? Date(row, "updated_at") ?? DateTimeOffset.UnixEpoch);
            }
        }
        else if (row.TryGetProperty("producer", out var p) && FullProducer(p))
        {
            var producer = p.Deserialize<Producer>(VndbJson.Options) ?? throw new ArgumentException();
            id ??= producer.Id; title ??= producer.Name;
            snapshot = new(producer.Id, producer.Name, "producer", null, [], producer, new("not_applicable"), Date(row, "fetched_at") ?? DateTimeOffset.UnixEpoch, "client_vndb_import");
        }
        else if (FullNovel(row)) snapshot = Novel(row.Deserialize<VisualNovel>(VndbJson.Options)!, [], DateTimeOffset.UnixEpoch);
        else if (id?.StartsWith('p') == true && row.TryGetProperty("description", out _) && row.TryGetProperty("aliases", out _))
        {
            var producer = row.Deserialize<Producer>(VndbJson.Options)!;
            snapshot = new(producer.Id, producer.Name, "producer", null, [], producer, new("not_applicable"), DateTimeOffset.UnixEpoch, "client_vndb_import");
        }
        if (id is not null) id = VndbId.Normalize(id, id.StartsWith('p') ? 'p' : 'v');
        if (snapshot is not null && snapshot.VndbId != id) throw new ArgumentException("外层身份与快照不一致。");
        if (snapshot is not null && Text(row, "kind") is { } declared && declared != snapshot.Kind) throw new ArgumentException("条目种类与快照不一致。");
        title ??= id;
        if (string.IsNullOrWhiteSpace(title) || title.Length > 500 || title.Any(char.IsControl)) throw new ArgumentException("缺少有效标题。");
        if (snapshot?.Producer is { } producerValue && (VndbId.Normalize(producerValue.Id, 'p') != id || string.IsNullOrWhiteSpace(producerValue.Name))) throw new ArgumentException();
        if (snapshot?.Producer is { } value) ValidateProducer(value);
        var normalized = new NormalizedMetadata("", id, title, snapshot, snapshot is null ? "listing" : "snapshot");
        return normalized with { OperationId = Hash(JsonSerializer.SerializeToUtf8Bytes(normalized, VndbJson.Options)) };
    }
    private static MetadataSnapshot Novel(VisualNovel novel, IReadOnlyList<Producer> producers, DateTimeOffset fetched)
    {
        if (VndbId.Normalize(novel.Id, 'v') != novel.Id || string.IsNullOrWhiteSpace(novel.Title) || novel.Title.Length > 500 || novel.Title.Any(char.IsControl) ||
            novel.Rating is < 0 or > 100 || novel.VoteCount is < 0 || novel.LengthMinutes is < 0) throw new ArgumentException();
        if (novel.Developers is null || novel.Titles is null || novel.Aliases is null || novel.Languages is null || novel.Platforms is null ||
            novel.Titles.Any(t => t is null || string.IsNullOrWhiteSpace(t.Lang) || string.IsNullOrWhiteSpace(t.Title)) ||
            novel.Titles.Select(t => t.Lang).Distinct().Count() != novel.Titles.Count || novel.Aliases.Any(t => t is null) ||
            novel.Languages.Any(t => t is null) || novel.Platforms.Any(t => t is null) || novel.Description?.Length > 500000)
            throw new ArgumentException();
        if (novel.ReleaseCatalog is { } releases) ReleaseRules.Validate(releases, novel.Id);
        if (novel.Relations is { } relations && (relations.Count > 1000 || relations.Any(r => r is null ||
            VndbId.Normalize(r.Id, 'v') != r.Id || string.IsNullOrWhiteSpace(r.Relation) || r.Relation.Length > 40 || r.Relation.Any(char.IsControl))))
            throw new ArgumentException("作品关联无效。");
        if (novel.Image is not null) _ = CoverPolicy.ValidateSource(novel.Image.Url);
        if (novel.Developers.Count > 1000 || novel.Developers.DistinctBy(p => p?.Id).Count() != novel.Developers.Count ||
            novel.Developers.Any(p => p is null || VndbId.Normalize(p.Id, 'p') != p.Id || string.IsNullOrWhiteSpace(p.Name)) ||
            producers.Any(p => p is null || !novel.Developers.Any(d => d.Id == p.Id) || string.IsNullOrWhiteSpace(p.Name)) || producers.DistinctBy(p => p.Id).Count() != producers.Count)
            throw new ArgumentException("会社关系无效。");
        foreach (var producer in producers) ValidateProducer(producer);
        return new(novel.Id, novel.Title, "vn", novel, producers, null, new(novel.Image is null ? "none" : "not_downloaded"),
            fetched, "client_vndb_import", novel.Developers.All(p => producers.Any(d => d.Id == p.Id)) ? "full_text" : "partial_text");
    }
    private static void ValidateProducer(Producer producer)
    {
        if (VndbId.Normalize(producer.Id, 'p') != producer.Id || string.IsNullOrWhiteSpace(producer.Name) || producer.Name.Length > 500 || producer.Name.Any(char.IsControl) ||
            producer.Aliases is null || producer.Aliases.Count > 1000 || producer.Aliases.Any(a => a is null || a.Length > 10000) || producer.Description?.Length > 500000)
            throw new ArgumentException("会社资料无效。");
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool FullNovel(JsonElement row) => row.ValueKind == JsonValueKind.Object && new[] { "id", "title", "developers", "titles", "aliases", "languages", "platforms", "description", "image", "released", "alttitle", "olang", "rating", "votecount", "length_minutes" }.All(name => row.TryGetProperty(name, out _));
    private static bool FullProducer(JsonElement row) => row.ValueKind == JsonValueKind.Object && new[] { "id", "name", "aliases", "lang", "type", "description" }.All(name => row.TryGetProperty(name, out _));
    private static string? Text(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static DateTimeOffset? Date(JsonElement row, string name) => Text(row, name) is { } value ? DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : null;
    private static JsonElement[] Array(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : throw new ArgumentException("缺少条目数组。");
    private static void UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("JSON 包含重复字段。");
                UniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) UniqueProperties(item);
    }
}
