using System.Security.Cryptography;
using System.Text;
using Vndb.Core;

namespace Vndb.Library;

public sealed record ArchiveRegistration(string NovelId, string SelectedProducerId, string Machine,
    string OriginalPath, string CurrentPath, string RunId, string PlanSha256, string State,
    string? JournalSha256, long? FileCount, long? SizeBytes, DateTimeOffset? VerifiedAt);
public sealed record ArchiveLocation(string Id, ArchiveRegistration Registration,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public static class ArchiveRules
{
    public static string Validate(ArchiveRegistration value, LibraryEntry? entry)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (VndbId.Normalize(value.NovelId, 'v') != value.NovelId || VndbId.Normalize(value.SelectedProducerId, 'p') != value.SelectedProducerId)
            throw new ArgumentException("归档关系必须使用规范 VNDB ID。");
        if (entry is null) throw new ArgumentException("作品必须先收录到资料库，才能登记归档目录。");
        if (!entry.Producers.Any(p => p.Id == value.SelectedProducerId))
            throw new ArgumentException("选定会社不在该作品的开发者关系中。");
        Text(value.Machine, 128); Text(value.RunId, 128);
        PathText(value.OriginalPath); PathText(value.CurrentPath);
        Hash(value.PlanSha256);
        if (value.State is not ("planned" or "archived" or "restored")) throw new ArgumentException("无效的归档状态。");
        if (value.FileCount is < 0 || value.SizeBytes is < 0) throw new ArgumentException("文件数量与大小不能为负数。");
        if (value.State != "planned")
        {
            Hash(value.JournalSha256);
            if (value.VerifiedAt is null || value.FileCount is null || value.SizeBytes is null)
                throw new ArgumentException("已归档/复原的记录必须包含客户端核对时间、文件数、大小和日志哈希。");
        }
        // Paths are opaque client metadata; the NAS never opens them.
        var original = value.OriginalPath.Replace('/', '\\');
        if (original.Length > 2 && original[1] == ':') original = original.ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.Machine.ToUpperInvariant() + "\0" + original)));
    }
    private static void Text(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl))
            throw new ArgumentException("归档记录字段为空、过长或包含控制字符。");
    }
    private static void PathText(string value)
    {
        Text(value, 4096);
        if (!(value.StartsWith('/') || value.StartsWith("\\\\", StringComparison.Ordinal) ||
              (value.Length > 2 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/')))
            throw new ArgumentException("归档路径必须是客户端绝对路径。");
        if (value.Replace('\\', '/').Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException("归档路径不能包含 . 或 .. 段。");
    }
    private static void Hash(string? value)
    {
        if (value is null || value.Length != 64 || value.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("归档证据必须包含 SHA256。");
    }
}
