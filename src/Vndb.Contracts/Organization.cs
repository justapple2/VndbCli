using System.Security.Cryptography;
using System.Text.Json;
using Vndb.Core;

namespace Vndb.Contracts;

public sealed record PhysicalIdentity(string Volume, string FileId, long CreatedFileTime);
public sealed record FileProof(string Path, bool Directory, PhysicalIdentity Identity, long Length, long LastWriteFileTime, int Attributes, string? Sha256);
public sealed record TreeProof(PhysicalIdentity Root, IReadOnlyList<FileProof> Entries, string Sha256, long Files, long Bytes);
public sealed record ArchivePolicy(string SourceScope, string TargetRoot, string Naming = "company-japanese-v1", string Conflict = "stop");
public sealed record ArchiveComponent(string Id, string RelativePath);
public sealed record DependencyHint(string File, string Kind, string Detail);
public sealed record ArchivePlan(string Id, string DeviceId, string InstallationId, long InstallationRevision,
    string SourcePath, string DestinationPath, ArchivePolicy Policy, PhysicalIdentity SourceScopeIdentity,
    PhysicalIdentity TargetRootIdentity, TreeProof Before, IReadOnlyList<ArchiveComponent> Components,
    IReadOnlyList<DependencyHint> Dependencies, string Company, string Title, DateTimeOffset CreatedAt,
    string Format = "vn-archive-plan", int FormatVersion = 1);
public sealed record RestorePlan(string RunId, string ArchiveSha256, string SourcePath, string DestinationPath,
    TreeProof Before, long InstallationRevision, long RunRevision, DateTimeOffset CreatedAt);
public sealed record ArchiveReceipt(string Id, string PlanSha256, string DeviceId, string InstallationId, string State,
    long Revision, long InstallationRevision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? EvidenceSha256 = null, string? RestoreSha256 = null, string? RestoreEvidenceSha256 = null);
public sealed record ArchiveRunView(ArchivePlan Plan, ArchiveReceipt Receipt, RestorePlan? RestorePlan = null);
public sealed record MoveEvidence(string RunId, string PlanSha256, string Phase, TreeProof Before, TreeProof After,
    string JournalHead, DateTimeOffset CompletedAt);
public sealed record EvidenceReceipt(string RunId, string Phase, string Sha256, long Bytes, DateTimeOffset CreatedAt);
public sealed record ArchiveCompletion(string EvidenceSha256, long ExpectedRevision);

public static class ArchiveFormat
{
    private static readonly JsonSerializerOptions CanonicalOptions = new(VndbJson.Options) { NewLine = "\n" };
    private static readonly JsonSerializerOptions LegacyWindowsOptions = new(VndbJson.Options) { NewLine = "\r\n" };
    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, CanonicalOptions);
    public static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(Serialize(value)));
    /// <summary>Only for verifying existing Windows records; new hashes always use LF.</summary>
    public static string LegacyWindowsHash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, LegacyWindowsOptions)));
    public static bool MatchesHash<T>(T value, string expected) => Hash(value) == expected || LegacyWindowsHash(value) == expected;
    public static bool IsHash(string? value) => value?.Length == 64 && value.All(c => char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c));
    public static string TreeHash(PhysicalIdentity root, IReadOnlyList<FileProof> entries) => Hash(new { root, entries });
    public static bool MatchesTree(TreeProof actual, TreeProof expected) => actual.Root == expected.Root &&
        MatchesHash(new { root = actual.Root, entries = actual.Entries }, expected.Sha256);
    public static void Validate(TreeProof tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (tree.Root is null || tree.Entries is null || tree.Entries.Count > 100000 || !IsHash(tree.Sha256) || !MatchesHash(new { root = tree.Root, entries = tree.Entries }, tree.Sha256)) throw new ArgumentException("无效目录指纹。");
        if (tree.Entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != tree.Entries.Count) throw new ArgumentException("指纹路径重复。");
        foreach (var e in tree.Entries)
            if (string.IsNullOrEmpty(e.Path) || e.Path.StartsWith('/') || e.Path.Contains('\\') || e.Path.Contains(':') || e.Path.Split('/').Any(p => p is "" or "." or "..") || e.Identity is null ||
                e.Identity.Volume != tree.Root.Volume || e.Length < 0 || (e.Attributes & 1024) != 0 || !e.Directory && !IsHash(e.Sha256)) throw new ArgumentException("无效文件证据或重解析入口。");
        if (tree.Files != tree.Entries.Count(e => !e.Directory) || tree.Bytes != tree.Entries.Where(e => !e.Directory).Sum(e => e.Length)) throw new ArgumentException("指纹计数不一致。");
    }
}
