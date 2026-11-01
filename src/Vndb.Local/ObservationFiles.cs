namespace Vndb.Local;

public sealed record ObservationFacts(string State, bool Directory = false, LocalPhysicalIdentity? Identity = null, long Length = 0, DateTime LastWriteUtc = default);
public interface IObservationFiles
{
    void BeginPass() { }
    ObservationFacts Inspect(string path);
    IReadOnlyList<string> Children(string path, int limit, CancellationToken ct);
}
/// <summary>Metadata/handle identity only; never opens game content, PE resources or archives.</summary>
public sealed class ObservationFiles : IObservationFiles
{
    private readonly Dictionary<string, string> _volumes = new(ObservationSettingsStore.Paths);
    public void BeginPass() => _volumes.Clear();
    public ObservationFacts Inspect(string path)
    {
        try
        {
            if (path.StartsWith("\\\\", StringComparison.Ordinal)) return new("unknown");
            var drive = Path.GetPathRoot(path)!;
            if (!_volumes.ContainsKey(drive) && OperatingSystem.IsWindows() && new DriveInfo(drive).DriveType == DriveType.Network) return new("unknown");
            LocalFileLease.RequireOrdinaryAncestors(path);
            var attributes = File.GetAttributes(path);
            var directory = (attributes & FileAttributes.Directory) != 0;
            if (!_volumes.TryGetValue(drive, out var volume)) _volumes[drive] = volume = NativeFiles.Volume(path);
            using var handle = NativeFiles.Open(path); var identity = NativeFiles.Identity(handle, volume);
            if (directory) { _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToArray(); return new("present", true, identity); }
            var info = new FileInfo(path); return new("present", false, identity, info.Length, info.LastWriteTimeUtc);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return new("missing"); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or PlatformNotSupportedException) { return new("unknown"); }
    }
    public IReadOnlyList<string> Children(string path, int limit, CancellationToken ct)
    {
        LocalFileLease.RequireOrdinaryAncestors(path);
        var result = new List<string>();
        foreach (var file in Directory.EnumerateFileSystemEntries(path))
        { ct.ThrowIfCancellationRequested(); if (result.Count == limit) throw new IOException("浅层枚举达到预算，请缩小管理范围。"); result.Add(file); }
        return result;
    }
}

public sealed class ObservationSettingsStore(string directory, IObservationFiles? files = null)
{
    private readonly JsonStateFile<ObservationSettings> _state = new(directory, "observation-settings.json");
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    internal static StringComparer Paths => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static bool Within(string root, string path) => Paths.Equals(root, path) || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    public async Task<ObservationSettings> ReadAsync(CancellationToken ct = default)
    { var value = await _state.ReadAsync(ct) ?? new(); Validate(value); return value; }
    public Task SaveAsync(ObservationSettings value, CancellationToken ct = default) { Validate(value); return _state.SaveAsync(value, ct); }
    public ObservationRoot Capture(string path, string kind, int depth)
    {
        path = Normalize(path);
        if (kind is not ("games" or "packages") || depth is < 1 or > 3) throw new ArgumentException("管理根类型或浅层深度无效（1–3）。");
        var facts = (files ?? new ObservationFiles()).Inspect(path);
        if (facts is not { State: "present", Directory: true, Identity: not null }) throw new ArgumentException("管理根不可读或不支持稳定身份；首版自动观测支持本机NTFS，网络及其他卷请手动核对。");
        return new(Guid.NewGuid().ToString("D"), path, kind, depth, facts.Identity);
    }
    private string Normalize(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl) || path.StartsWith("\\\\", StringComparison.Ordinal)) throw new ArgumentException("请选择本机管理目录，不能使用网络共享或相对路径。");
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Paths.Equals(path, Path.GetPathRoot(path)) || Within(path, DirectoryPath) || Within(DirectoryPath, path)) throw new ArgumentException("不能监控盘根或客户端数据目录及其上级／内部。");
        LocalFileLease.RequireOrdinaryAncestors(path); return path;
    }
    internal void Validate(ObservationSettings value)
    {
        if (value.Format != "vn-local-observation-settings" || value.FormatVersion != 1 || value.WatcherLimit is < 1 or > 64 || value.PeriodMinutes is < 10 or > 60 || (value.Roots?.Count ?? 0) > 32)
            throw new InvalidDataException("本机观测设置版本或预算无效，原件保留。");
        var ids = new HashSet<string>(StringComparer.Ordinal); var paths = new List<string>();
        foreach (var root in value.Roots ?? [])
        {
            if (root is null || !Guid.TryParseExact(root.Id, "D", out _) || !ids.Add(root.Id) || root.Identity is null || root.Kind is not ("games" or "packages") || root.Depth is < 1 or > 3) throw new InvalidDataException("管理根格式无效。");
            var path = Normalize(root.Path);
            if (!Paths.Equals(path, root.Path) || paths.Any(p => Within(p, path) || Within(path, p))) throw new InvalidDataException("管理根重复或嵌套，请合并范围。");
            paths.Add(path);
        }
    }
}
