using System.Text.Json;
using Vndb.Contracts;

namespace Vndb.Library;

public static class MetadataInput
{
    public static (IReadOnlyList<NormalizedMetadata> Items, string? ClientId, string RawSha) Parse(JsonElement root, ImportLimits limits) =>
        MetadataPayload.Parse(root, limits);
}