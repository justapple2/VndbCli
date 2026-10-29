using System.Diagnostics;
using System.Security.Cryptography;
using Vndb.Contracts;

namespace Vndb.Local;

public static class LocalFiles
{
    public static string Full(string path)
    {
        NativeFiles.Supported();
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.Length > 240 || path[2] != '\\' && path[2] != '/' || path[3..].Contains(':') ||
            path.Replace('\\', '/').Split('/').Skip(1).Any(p => p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.'))) throw new ArgumentException("需要本地盘规范绝对路径（至多240字符），不接受UNC、设备、上级路径或歧义名称。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    public static bool Same(string a, string b) => Full(a).Equals(Full(b), StringComparison.OrdinalIgnoreCase);
    public static bool Within(string parent, string path) => Full(path).StartsWith(Full(parent).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    public static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
    }
    public static void CheckAncestors(string path)
    {
        for (var current = Full(path); current is not null; current = Path.GetDirectoryName(current))
            if (Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("路径经过重解析入口，已停止。");
    }
    public static LocalTreeProof Snapshot(string path, CancellationToken ct = default)
    {
        path = Full(path); CheckAncestors(path); var volume = NativeFiles.Volume(path); var root = NativeFiles.Identity(path); var rows = new List<LocalFileProof>();
        void Walk(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested(); if (rows.Count >= 100000) throw new IOException("单容器清单超过100000项，请人工核对边界。");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("目录含重解析入口；未跟随。");
                var relative = Path.GetRelativePath(path, entry).Replace('\\', '/');
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    using var handle = NativeFiles.Open(entry); var identity = NativeFiles.Identity(handle, volume);
                    rows.Add(new(relative, true, identity, 0, 0, (int)attributes, null)); Walk(entry);
                }
                else
                {
                    using var handle = NativeFiles.Open(entry, read: true); var before = NativeFiles.Facts(handle);
                    using var stream = new FileStream(handle, FileAccess.Read); var hash = Convert.ToHexStringLower(SHA256.HashData(stream)); var after = NativeFiles.Facts(handle);
                    if (before.IndexHigh != after.IndexHigh || before.IndexLow != after.IndexLow || before.SizeHigh != after.SizeHigh || before.SizeLow != after.SizeLow || before.WriteHigh != after.WriteHigh || before.WriteLow != after.WriteLow) throw new IOException("读取期间文件已变化。");
                    rows.Add(new(relative, false, NativeFiles.Identity(handle, volume), stream.Length, (long)(((ulong)after.WriteHigh << 32) | after.WriteLow), (int)attributes, hash));
                }
            }
        }
        Walk(path); rows.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        return new(root, rows, LocalArchiveFormat.TreeHash(root, rows), rows.Count(r => !r.Directory), rows.Where(r => !r.Directory).Sum(r => r.Length));
    }
    public static IReadOnlyList<LocalDependencyHint> Inspect(string path)
    {
        path = Full(path); CheckAncestors(path); var hints = new List<LocalDependencyHint>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(path).Take(100))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("不读取链接中的线索。");
            if (Directory.Exists(entry)) continue;
            var name = Path.GetFileName(entry); var ext = Path.GetExtension(entry).ToLowerInvariant();
            if (ext == ".exe")
            {
                var version = FileVersionInfo.GetVersionInfo(entry);
                hints.Add(new(name, "pe", string.Join(" · ", new[] { version.ProductName, version.FileDescription, version.CompanyName, version.FileVersion }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim()));
                if (name.Contains("setup", StringComparison.OrdinalIgnoreCase) || name.Contains("unins", StringComparison.OrdinalIgnoreCase) || name.Contains("install", StringComparison.OrdinalIgnoreCase)) hints.Add(new(name, "installer", "安装／卸载程序线索；注册表与外部依赖需要人工核对。"));
            }
            if (ext is ".reg" or ".lnk") hints.Add(new(name, ext == ".reg" ? "registry" : "shortcut", "保留原文件；未导入注册表或解析执行快捷方式，迁移后需核对原路径。"));
            if (ext is ".ini" or ".cfg" or ".txt")
            {
                using var stream = File.OpenRead(entry); using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                var buffer = new char[2048]; var count = reader.ReadBlock(buffer, 0, buffer.Length); var text = new string(buffer, 0, count);
                if (text.Contains("HKEY_", StringComparison.OrdinalIgnoreCase) || text.Contains("レジストリ", StringComparison.Ordinal) || text.Contains("registry", StringComparison.OrdinalIgnoreCase)) hints.Add(new(name, "registry-reference", "配置／说明中有注册表文字线索，需要人工核对；未自动改写。"));
            }
        }
        hints.Add(new("", "compatibility-unknown", "仅凭目录不能确定硬盘版／安装版可运行性；未读取系统注册表，未运行游戏。PE公司名可能是引擎商，只用于候选查询。"));
        return hints;
    }
}
