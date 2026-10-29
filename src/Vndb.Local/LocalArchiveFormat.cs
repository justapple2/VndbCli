using Vndb.Contracts;

namespace Vndb.Local;

/// <summary>Legacy byte compatibility is explicit; local models never become Web DTOs.</summary>
public static class LocalArchiveFormat
{
    public static byte[] Serialize<T>(T value) => ArchiveFormat.Serialize(value);
    public static string Hash<T>(T value) => ArchiveFormat.Hash(value);
    public static string LegacyWindowsHash<T>(T value) => ArchiveFormat.LegacyWindowsHash(value);
    public static bool MatchesHash<T>(T value, string expected) => ArchiveFormat.MatchesHash(value, expected);
    public static bool IsHash(string? value) => ArchiveFormat.IsHash(value);
    public static string TreeHash(LocalPhysicalIdentity root, IReadOnlyList<LocalFileProof> entries) => Hash(new { root, entries });
    public static bool MatchesTree(LocalTreeProof actual, LocalTreeProof expected) => actual.Root == expected.Root && MatchesHash(new { root = actual.Root, entries = actual.Entries }, expected.Sha256);
    public static void Validate(LocalTreeProof tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (tree.Root is null || tree.Entries is null || tree.Entries.Count > 100000 || tree.Entries.Any(e => e is null || e.Identity is null)) throw new ArgumentException("无效目录指纹。");
        // Reuse the existing pure evidence validator without restoring any network location capability.
        ArchiveFormat.Validate(new TreeProof(new(tree.Root.Volume, tree.Root.FileId, tree.Root.CreatedFileTime),
            tree.Entries.Select(e => new FileProof(e.Path, e.Directory, new(e.Identity.Volume, e.Identity.FileId, e.Identity.CreatedFileTime), e.Length, e.LastWriteFileTime, e.Attributes, e.Sha256)).ToArray(), tree.Sha256, tree.Files, tree.Bytes));
    }
}
