using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vndb.Local;

public sealed record ProcessIdentity(int Pid, long CreatedUtcTicks, string Executable);
public sealed record ProcessObservation(ProcessIdentity Identity, int ParentPid);
public interface IProcessInventory { IReadOnlyList<ProcessObservation> Snapshot(); }

public sealed partial class WindowsProcessInventory : IProcessInventory
{
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessEntry
    {
        public uint Size, Usage, Pid; public nuint Heap; public uint Module, Threads, ParentPid;
        public int Priority; public uint Flags; public fixed char Name[260];
    }
    private sealed class SnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
    { public SnapshotHandle() : base(true) { } protected override bool ReleaseHandle() => CloseHandle(handle) != 0; }
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial SnapshotHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)] private static partial int First(SnapshotHandle snapshot, ref ProcessEntry entry);
    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)] private static partial int Next(SnapshotHandle snapshot, ref ProcessEntry entry);
    [LibraryImport("kernel32.dll")] private static partial int CloseHandle(nint handle);
    public unsafe IReadOnlyList<ProcessObservation> Snapshot()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("本阶段只验证Windows进程身份跟踪。");
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        var entry = new ProcessEntry { Size = (uint)sizeof(ProcessEntry) }; var results = new List<ProcessObservation>();
        if (First(snapshot, ref entry) == 0) return results;
        do
        {
            try { using var process = Process.GetProcessById((int)entry.Pid); results.Add(new(Identify(process), (int)entry.ParentPid)); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        } while (Next(snapshot, ref entry) != 0);
        return results;
    }
    public static ProcessIdentity Identify(Process process) => new(process.Id, process.StartTime.ToUniversalTime().Ticks,
        Path.GetFullPath(process.MainModule?.FileName ?? throw new InvalidOperationException("进程路径不可读。")));
    public static bool Same(ProcessIdentity a, ProcessIdentity b) => a.Pid == b.Pid && a.CreatedUtcTicks == b.CreatedUtcTicks &&
        string.Equals(a.Executable, b.Executable, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Never select by basename. A departed root needs its held handle's verified exit time.</summary>
public sealed class ProcessTracker(ProcessIdentity root, string expectedPath)
{
    private readonly Dictionary<int, (ProcessIdentity Identity, long SeenAt)> _known = new() { [root.Pid] = (root, root.CreatedUtcTicks) };
    public IReadOnlyList<ProcessIdentity> Match(IReadOnlyList<ProcessObservation> observations, DateTimeOffset now, DateTimeOffset? rootExitedAt = null)
    {
        var pending = observations.ToList(); var matches = new List<ProcessIdentity>(); var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var observation in pending.ToArray())
            {
                var identity = observation.Identity;
                var isRoot = WindowsProcessInventory.Same(identity, root);
                var descendant = false;
                if (!isRoot && _known.TryGetValue(observation.ParentPid, out var parent) && identity.CreatedUtcTicks >= parent.Identity.CreatedUtcTicks)
                {
                    var liveParent = observations.SingleOrDefault(p => p.Identity.Pid == observation.ParentPid);
                    descendant = liveParent is not null ? WindowsProcessInventory.Same(liveParent.Identity, parent.Identity) :
                        observation.ParentPid == root.Pid && rootExitedAt is { } exit ? identity.CreatedUtcTicks <= exit.UtcTicks : identity.CreatedUtcTicks <= parent.SeenAt;
                }
                if (!isRoot && !descendant) continue;
                _known[identity.Pid] = (identity, now.UtcTicks); pending.Remove(observation); changed = true;
                if (string.Equals(identity.Executable, expectedPath, StringComparison.OrdinalIgnoreCase)) matches.Add(identity);
            }
        }
        return matches;
    }
}
