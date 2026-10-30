using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Vndb.Contracts;

namespace Vndb.Local;

/// <summary>Windows x64/ARM64 handles own identity and rename; no copy/delete fallback.</summary>
public static partial class NativeFiles
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileInfo
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetInfo(SafeFileHandle file, out FileInfo info);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, byte* info, uint size);
    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool VolumePath(string path, char* buffer, uint length);
    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool VolumeName(string path, char* buffer, uint length);
    public static void Supported()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8) throw new PlatformNotSupportedException("本轮文件扫描／执行仅验证Windows x64/ARM64本地卷；其他平台仍可查询Web。");
        if (Marshal.SizeOf<FileInfo>() != 52) throw new InvalidOperationException("Win32结构不匹配。");
    }
    public static SafeFileHandle Open(string path, bool rename = false, bool gate = false, bool read = false)
    {
        Supported();
        var handle = CreateFile(Path.GetFullPath(path), 0x80u | (rename ? 0x10000u : 0) | (read ? 0x80000000u : 0), read ? 1u : gate || rename ? 3u : 7u, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError(); handle.Dispose();
            var cause = new Win32Exception(error);
            throw new IOException($"无法取得文件身份／独占改名权限（Win32 {error}：{cause.Message}）。", cause);
        }
        try { if ((Facts(handle).Attributes & 1024) != 0) throw new IOException("拒绝重解析入口。"); return handle; }
        catch { handle.Dispose(); throw; }
    }
    internal static FileInfo Facts(SafeFileHandle handle) => GetInfo(handle, out var info) ? info : throw new IOException("无法读取文件身份。", new Win32Exception(Marshal.GetLastPInvokeError()));
    public static unsafe string Volume(string path)
    {
        Supported();
        if (!new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) throw new PlatformNotSupportedException("本轮仅验证Windows本地NTFS的稳定文件身份；其他文件系统不执行整理。");
        var mount = new char[32768]; var name = new char[128];
        fixed (char* m = mount) fixed (char* n = name)
        {
            if (!VolumePath(Path.GetFullPath(path), m, (uint)mount.Length) || !VolumeName(new string(m), n, (uint)name.Length)) throw new IOException("无法确定本地卷身份；不执行跨卷或网络卷移动。", new Win32Exception(Marshal.GetLastPInvokeError()));
            return new string(n);
        }
    }
    public static LocalPhysicalIdentity Identity(string path)
    {
        using var handle = Open(path); return Identity(handle, Volume(path));
    }
    public static LocalPhysicalIdentity Identity(SafeFileHandle handle, string volume)
    {
        var f = Facts(handle); return new(volume, $"{f.VolumeSerial:x8}:{f.IndexHigh:x8}{f.IndexLow:x8}", (long)(((ulong)f.CreatedHigh << 32) | f.CreatedLow));
    }
    public static unsafe void Rename(SafeFileHandle source, string destination)
    {
        Supported(); var name = System.Text.Encoding.Unicode.GetBytes(Path.GetFullPath(destination));
        var buffer = new byte[20 + name.Length + 2]; // DWORD flags, padding, NULL HANDLE, DWORD byte length, WCHAR[]; x64/ARM64.
        BitConverter.TryWriteBytes(buffer.AsSpan(16), name.Length); name.CopyTo(buffer, 20);
        fixed (byte* p = buffer)
            if (!SetFileInformationByHandle(source, 3, p, (uint)buffer.Length)) throw new IOException("同卷无覆盖改名失败；已停止，未复制或删除。", new Win32Exception(Marshal.GetLastPInvokeError()));
    }
}
