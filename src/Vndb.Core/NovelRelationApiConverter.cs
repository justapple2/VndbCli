using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vndb.Core;

// Kana uses relation_official. Internal snapshots intentionally keep their existing official field/hash.
internal sealed class NovelRelationApiConverter : JsonConverter<NovelRelation>
{
    public override NovelRelation Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader); var root = document.RootElement;
        if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("relation", out var code) || code.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("relation_official", out var official) || official.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new JsonException("VNDB 作品关联字段无效。");
        try
        {
            var normalized = VndbId.Normalize(id.GetString()!, 'v'); var value = code.GetString()!;
            if (value.Length is 0 or > 40 || value.Any(char.IsControl)) throw new JsonException("VNDB 关系类型无效。");
            return new(normalized, value, official.GetBoolean());
        }
        catch (ArgumentException e) { throw new JsonException("VNDB 关联ID无效。", e); }
    }
    public override void Write(Utf8JsonWriter writer, NovelRelation value, JsonSerializerOptions options) => throw new NotSupportedException("API reader only.");
}
