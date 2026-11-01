using System.Security.Cryptography;
using System.Text;

namespace Vndb.Local;

/// <summary>Per-resource cross-process lease; persistent empty files carry no private values.</summary>
public sealed class LocalFileLease : IDisposable
{
    private readonly FileStream _stream;
    private LocalFileLease(FileStream stream) => _stream = stream;
    public static LocalFileLease? TryAcquire(string directory, string resource)
    {
        RequireOrdinaryAncestors(directory); var locks = Path.Combine(directory, "locks"); RejectLink(locks); Directory.CreateDirectory(locks);
        var path = Path.Combine(locks, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(resource))) + ".lock"); RejectLink(path);
        try { return new(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33 or 11) { return null; }
    }
    public static async Task<LocalFileLease> AcquireAsync(string directory, string resource, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(directory);
        RequireOrdinaryAncestors(root);
        var locks = Path.Combine(root, "locks");
        Directory.CreateDirectory(root);
        RejectLink(root); RejectLink(locks);
        Directory.CreateDirectory(locks);
        var path = Path.Combine(locks, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(resource))) + ".lock");
        while (true)
        {
            ct.ThrowIfCancellationRequested(); RejectLink(path);
            try { return new(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33 or 11) { await Task.Delay(25, ct); }
        }
    }
    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("本机状态或锁不能是重解析入口。");
    }
    internal static void RequireOrdinaryAncestors(string path)
    {
        path = Path.GetFullPath(path); RejectLink(path);
        for (var parent = Directory.GetParent(path); parent is not null; parent = parent.Parent) RejectLink(parent.FullName);
    }
    public void Dispose() => _stream.Dispose();
}
