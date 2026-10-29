using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Vndb.Core;

namespace Vndb.Local;

public sealed record LaunchProfile(string Id, string Title, string Executable, IReadOnlyList<string> Arguments,
    string WorkingDirectory, string Sha256, string? InstallationId = null, string? ComponentId = null,
    string? TrackedExecutable = null, string? TrackedSha256 = null, int MatchTimeoutSeconds = 30, LocaleLaunch? Locale = null);
public sealed record TrackedLaunch(int Pid, ProcessIdentity? Identity, LaunchProfile Profile, Process Process);
public enum LaunchMethod { Profile, Direct, GlobalLocale }

/// <summary>Local opt-in profiles only. Metadata imports never create launch authority.</summary>
public sealed class LocalLauncher(string directory)
{
    private readonly string _file = Path.Combine(Path.GetFullPath(directory), "launch-profiles.json");
    public async Task<IReadOnlyList<LaunchProfile>> ReadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_file)) return [];
        if ((File.GetAttributes(_file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("启动配置不能是链接。");
        if (new FileInfo(_file).Length > 8 * 1024 * 1024) throw new InvalidDataException("启动配置超过8MiB。");
        var profiles = JsonSerializer.Deserialize<LaunchProfile[]>(await File.ReadAllTextAsync(_file, ct), VndbJson.Options)
            ?? throw new InvalidDataException("启动配置损坏，原文件保留。");
        if (profiles.Length > 5000 || profiles.Any(p => p is null || string.IsNullOrEmpty(p.Id) || string.IsNullOrEmpty(p.Title) ||
            string.IsNullOrEmpty(p.Executable) || string.IsNullOrEmpty(p.WorkingDirectory) || p.Arguments is null ||
            p.Arguments.Any(a => a is null) || !ArchiveHash(p.Sha256) || p.MatchTimeoutSeconds is < 5 or > 300 ||
            p.Locale is { } locale && (string.IsNullOrEmpty(locale.ToolPath) || !Path.IsPathFullyQualified(locale.ToolPath) || !Guid.TryParseExact(locale.ProfileId, "D", out _) || !ArchiveHash(locale.ToolSha256) || !ArchiveHash(locale.ProfileSha256)) ||
            p.TrackedExecutable is not null && (!Path.IsPathFullyQualified(p.TrackedExecutable) || !ArchiveHash(p.TrackedSha256))) || profiles.Select(p => p.Id).Distinct().Count() != profiles.Length)
            throw new InvalidDataException("启动配置损坏，原文件保留。");
        return profiles;
    }
    public async Task RebaseAsync(string installationId, string source, string target, CancellationToken ct = default)
    {
        string Map(string path) => LocalFiles.Same(path, source) ? target : LocalFiles.Within(source, path) ? Path.Combine(target, Path.GetRelativePath(source, path)) : path;
        using var lease = await LocalFileLease.AcquireAsync(Path.GetDirectoryName(_file)!, "launch-profiles", ct);

        {
            var profiles = await ReadAsync(ct);
            var changed = profiles.Select(p => p.InstallationId != installationId ? p : p with { Executable = Map(p.Executable), WorkingDirectory = Map(p.WorkingDirectory), TrackedExecutable = p.TrackedExecutable is { } tracked ? Map(tracked) : null }).ToArray();
            if (!profiles.SequenceEqual(changed)) await JsonSnapshotStore.AtomicWriteAsync(_file, changed, true, ct);
        }
    }
    private static bool ArchiveHash(string? value) => value?.Length == 64 && value.All(char.IsAsciiHexDigit);

    public async Task<LaunchProfile> SaveAsync(string title, string executable, IReadOnlyList<string> arguments,
        string workingDirectory, string? installationId = null, string? componentId = null, string? id = null, CancellationToken ct = default, LocaleLaunchRequest? locale = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (title.Length > 300 || arguments.Count > 100 || arguments.Any(a => a is null || a.Length > 8192 || a.Contains('\0')))
            throw new ArgumentException("启动名称或参数过长／无效。");
        executable = Path.GetFullPath(executable); workingDirectory = Path.GetFullPath(workingDirectory);
        Check(executable, workingDirectory);
        var localeSettings = locale is null ? null : await LocaleEmulator.CaptureAsync(locale, ct);
        if (localeSettings is not null && !LocalFiles.Same(workingDirectory, Path.GetDirectoryName(executable)!))
            throw new ArgumentException("Locale Emulator固定使用游戏EXE所在目录作为工作目录。");
        var profile = new LaunchProfile(id ?? Guid.NewGuid().ToString("D"), title, executable, arguments.ToArray(),
            workingDirectory, await HashAsync(executable, ct), installationId, componentId, Locale: localeSettings);
        using var lease = await LocalFileLease.AcquireAsync(Path.GetDirectoryName(_file)!, "launch-profiles", ct);

        {
            var profiles = (await ReadAsync(ct)).ToList();
            var old = profiles.SingleOrDefault(p => p.Id == profile.Id);
            if (old is not null && old.Executable == profile.Executable && old.Sha256 == profile.Sha256)
                profile = profile with { TrackedExecutable = old.TrackedExecutable, TrackedSha256 = old.TrackedSha256, MatchTimeoutSeconds = old.MatchTimeoutSeconds };
            profiles.RemoveAll(p => p.Id == profile.Id); profiles.Add(profile);
            await JsonSnapshotStore.AtomicWriteAsync(_file, profiles, true, ct);
        }
        return profile;
    }

    // Takes an ID, never an imported profile. There is no Web dependency or shell fallback.
    public async Task<int> LaunchAsync(string id, CancellationToken ct = default, LaunchMethod method = LaunchMethod.Profile, LocaleLaunch? globalLocale = null)
    {
        var launched = await LaunchTrackedAsync(id, ct, method, globalLocale); using var process = launched.Process; return launched.Pid;
    }
    public async Task<TrackedLaunch> LaunchTrackedAsync(string id, CancellationToken ct = default, LaunchMethod method = LaunchMethod.Profile, LocaleLaunch? globalLocale = null)
    {
        var profile = WithMethod((await ReadAsync(ct)).Single(p => p.Id == id), method, globalLocale);
        var start = await StartInfoAsync(profile, ct);
        ct.ThrowIfCancellationRequested();
        var process = Process.Start(start) ?? throw new IOException("程序未启动。");
        ProcessIdentity? identity = null;
        try { _ = process.Handle; identity = WindowsProcessInventory.Identify(process); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        { try { if (process.HasExited) identity = new(process.Id, process.StartTime.ToUniversalTime().Ticks, start.FileName); } catch (InvalidOperationException) { } }
        return new(process.Id, identity, profile, process);
    }

    public async Task<ProcessStartInfo> PreviewStartAsync(string id, CancellationToken ct = default, LaunchMethod method = LaunchMethod.Profile, LocaleLaunch? globalLocale = null) =>
        await StartInfoAsync(WithMethod((await ReadAsync(ct)).Single(p => p.Id == id), method, globalLocale), ct);

    private static LaunchProfile WithMethod(LaunchProfile profile, LaunchMethod method, LocaleLaunch? globalLocale) => method switch
    {
        LaunchMethod.Profile => profile,
        LaunchMethod.Direct => profile with { Locale = null },
        LaunchMethod.GlobalLocale => profile with { Locale = globalLocale ?? throw new ArgumentException("请先在全局设置保存默认LE工具和语言配置。") },
        _ => throw new ArgumentException("未知启动方式。")
    };

    private static async Task<ProcessStartInfo> StartInfoAsync(LaunchProfile profile, CancellationToken ct)
    {
        Check(profile.Executable, profile.WorkingDirectory);
        if (await HashAsync(profile.Executable, ct) != profile.Sha256)
            throw new IOException("启动程序已改变，请重新选择并保存本机配置。");
        if (profile.TrackedExecutable is { } tracked && (!File.Exists(tracked) || await HashAsync(tracked, ct) != profile.TrackedSha256)) throw new IOException("实际游戏程序已改变，请重新保存跟踪配置。");
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(profile.Locale?.ToolPath ?? profile.Executable) { UseShellExecute = false, WorkingDirectory = profile.WorkingDirectory };
        start.Environment.Remove("VN_LIBRARY_KEY");
        start.Environment.Remove("VNDB_API_TOKEN");
        if (profile.Locale is { } locale)
        {
            if (!LocalFiles.Same(profile.WorkingDirectory, Path.GetDirectoryName(profile.Executable)!))
                throw new ArgumentException("LE启动要求工作目录为启动EXE所在目录，请核对本机配置。");
            if (await LocaleEmulator.CaptureAsync(new(locale.ToolPath, locale.ProfileId), ct) != locale)
                throw new IOException("转区工具或所选语言配置已变化，请重新核对并保存。");
            start.ArgumentList.Add("-runas"); start.ArgumentList.Add(locale.ProfileId); start.ArgumentList.Add(profile.Executable);
        }
        foreach (var argument in profile.Arguments) start.ArgumentList.Add(profile.Locale is null ? argument : LocaleEmulator.QuoteArgument(argument));
        return start;
    }

    public async Task<LaunchProfile> ConfigureTrackingAsync(string id, string? actualExecutable, int timeoutSeconds = 30, CancellationToken ct = default)
    {
        if (timeoutSeconds is < 5 or > 300) throw new ArgumentException("进程匹配超时为5–300秒。");
        string? hash = null;
        if (!string.IsNullOrWhiteSpace(actualExecutable)) { actualExecutable = Path.GetFullPath(actualExecutable); Check(actualExecutable, Path.GetDirectoryName(actualExecutable)!); hash = await HashAsync(actualExecutable, ct); }
        else actualExecutable = null;
        using var lease = await LocalFileLease.AcquireAsync(Path.GetDirectoryName(_file)!, "launch-profiles", ct);

        {
            var profiles = (await ReadAsync(ct)).ToList(); var old = profiles.Single(p => p.Id == id);
            var changed = old with { TrackedExecutable = actualExecutable, TrackedSha256 = hash, MatchTimeoutSeconds = timeoutSeconds };
            profiles[profiles.IndexOf(old)] = changed; await JsonSnapshotStore.AtomicWriteAsync(_file, profiles, true, ct); return changed;
        }
    }

    private static void Check(string executable, string workingDirectory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("本阶段仅验证Windows直接EXE启动。");
        if (!Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable) || !Directory.Exists(workingDirectory))
            throw new ArgumentException("请选择存在的EXE及工作目录。");
        LocalFiles.CheckAncestors(executable); LocalFiles.CheckAncestors(workingDirectory);
    }
    private static async Task<string> HashAsync(string file, CancellationToken ct)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
}
