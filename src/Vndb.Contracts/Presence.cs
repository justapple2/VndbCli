using System.Text.Json.Serialization;

namespace Vndb.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PresenceFlag(string ItemId, bool IsLocal);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PresenceBatch(string Id, string ServiceInstanceId, string DeviceId, long ExpectedRevision,
    IReadOnlyList<PresenceFlag> Items, string Format = "vn-library-presence", int FormatVersion = 1);
public sealed record PresenceReceipt(string Id, string ServiceInstanceId, string PayloadSha256, long Revision);
public sealed record PresenceHead(string DeviceId, long Revision, IReadOnlyList<PresenceFlag> Items);
public sealed record PublicPresence(string NovelId, bool IsLocal);

/// <summary>Location DTOs are never accepted by the Web or sent by the client SDK.</summary>
public static class WirePrivacy
{
    private static readonly HashSet<string> PrivateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "path", "paths", "machine", "root_path", "original_path", "current_path", "source_path", "destination_path", "relative_path", "path_key",
        "source_scope", "target_root", "executable", "executables", "working_directory", "tracked_executable",
        "locale_emulator_path", "tool_path", "registry", "dependencies", "file_proofs", "source_scope_identity", "target_root_identity"
    };
    public static bool ContainsPrivateData(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.String) return ContainsLocalPath(element.GetString() ?? "");
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
            return element.EnumerateObject().Any(p => PrivateNames.Contains(p.Name) ||
                p.Name is "query" or "title" or "title_override" or "company_hint" or "company_override" or "review" or "notes" or "extra_notes" or "reason" &&
                p.Value.ValueKind == System.Text.Json.JsonValueKind.String && ContainsLocalPath(p.Value.GetString() ?? "") || ContainsPrivateData(p.Value));
        return element.ValueKind == System.Text.Json.JsonValueKind.Array && element.EnumerateArray().Any(ContainsPrivateData);
    }
    public static bool ContainsLocalPath(string text)
    {
        if (text.Contains("\\\\", StringComparison.Ordinal) || text.StartsWith("/home/", StringComparison.Ordinal) || text.StartsWith("/Users/", StringComparison.Ordinal)) return true;
        for (var i = 0; i + 2 < text.Length; i++)
            if (char.IsAsciiLetter(text[i]) && text[i + 1] == ':' && text[i + 2] is '\\' or '/' &&
                (i == 0 || !char.IsAsciiLetterOrDigit(text[i - 1]))) return true;
        return false;
    }
}
