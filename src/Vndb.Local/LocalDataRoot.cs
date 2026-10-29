using Vndb.Contracts;
using System.Security.Cryptography;
using System.Text.Json;
using Vndb.Core;

namespace Vndb.Local;

public static class LocalDataRoot
{
    public static string Default
    {
        get
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents)) documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
            if (documents.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidDataException("桌面资料目录需要本机文档目录，不能使用NAS共享路径。");
            return Path.Combine(documents, "VndbLocal");
        }
    }
    public static string Legacy => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VndbLocal");
    public static async Task MigrateAsync(string source, string target, CancellationToken ct = default)
    {
        source = Path.GetFullPath(source); target = Path.GetFullPath(target);
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(source)) return;
        if (target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("新旧资料目录不能嵌套。");
        using var migrationLease = await LocalFileLease.AcquireAsync(target, "data-migration", ct);
        var marker = Path.Combine(target, "legacy-migration.json"); if (File.Exists(marker)) return;
        var lockFile = Path.Combine(source, "session.lock");
        using var held = File.Exists(lockFile) ? new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        for (var parent = new DirectoryInfo(target); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("目标资料目录含链接，未迁移。");
        var copied = new Dictionary<string, string>();
        async Task Walk(string directory)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("旧数据含链接，保留原件等待人工核对。");
            foreach (var file in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested(); var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("旧数据含链接，未跟随。");
                if ((attributes & FileAttributes.Directory) != 0) { await Walk(file); continue; }
                if (Path.GetExtension(file) != ".json" && Path.GetFileName(file) != "credential.bin") continue;
                var relative = Path.GetRelativePath(source, file); var destination = Path.Combine(target, relative);
                var bytes = await File.ReadAllBytesAsync(file, ct);
                if (Path.GetFileName(file) == "credential.bin")
                {
                    destination = Path.ChangeExtension(destination, ".json");
                    bytes = JsonSerializer.SerializeToUtf8Bytes(new { format = "windows-dpapi", data = Convert.ToBase64String(bytes) }, VndbJson.Options);
                }
                var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (File.Exists(destination))
                { if (Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(destination, ct))) != sha) throw new IOException("目标已有不同JSON，未覆盖；请核对迁移清单。"); }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { await File.WriteAllBytesAsync(temporary, bytes, ct); ct.ThrowIfCancellationRequested(); File.Move(temporary, destination); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                copied[relative] = sha;
            }
        }
        await Walk(source);
        await JsonSnapshotStore.AtomicWriteAsync(marker, new { source, copied, originals_preserved = true }, false, ct);
    }
}
