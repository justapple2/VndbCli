using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Vndb.Local;

public enum GameEngine { Unknown, AdvGame, AdvHd }
public sealed record LaunchDiagnosis(GameEngine Engine, string Root, IReadOnlyList<string> Findings);
public sealed record InstallPathRepair(string Id, string InstallationId, string Root, LocalPhysicalIdentity Identity,
    byte[] Original, byte[] Updated, string OldPath, string NewPath, bool MirrorsMediaPath, string? Entry);
public sealed record InstallRepairRestore(InstallPathRepair Repair, string Root);

/// <summary>Known Windows launch dependencies for one selected installation or component.</summary>
public sealed class LaunchRepair(string stateDirectory)
{
    private const string ConfigName = "install.inf";
    private static readonly Encoding Japanese;
    private static readonly Regex IniLine = new(@"^[ \t]*(?:(?<section>\[[^\r\n]+\])[ \t]*|(?<key>Current|CDDrive)[ \t]*=(?<raw>[^\r\n]*))\r?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    private static readonly Regex IniValue = new(@"^[ \t]*""(?<path>[^""\r\n]+)""[ \t]*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    static LaunchRepair()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Japanese = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public static LaunchDiagnosis Inspect(string root, LaunchProfile? profile = null)
    {
        root = ExistingDirectory(root);
        var findings = new List<string>();
        var entry = profile?.TrackedExecutable ?? profile?.Executable;
        var engine = Detect(root, entry);
        if (profile is null) findings.Add("尚未选择已保存的启动配置。");
        else
        {
            findings.Add($"启动程序／包装器：{profile.Executable}");
            findings.Add($"实际游戏程序：{entry}");
            findings.Add($"工作目录：{profile.WorkingDirectory}");
            if (!File.Exists(profile.Executable)) findings.Add("启动程序路径失效。");
            if (!Directory.Exists(profile.WorkingDirectory)) findings.Add("工作目录不存在。");
            else if (!LocalFiles.Same(profile.WorkingDirectory, root)) findings.Add("工作目录与组件目录不同；依赖相对资源路径的游戏可能无法打开。保留手动配置，请核对。");
            if (profile.TrackedExecutable is { } tracked && !File.Exists(tracked)) findings.Add("实际游戏程序路径失效。");
            if (profile.Arguments.Any(a => Path.IsPathFullyQualified(a) && !LocalFiles.Exists(a))) findings.Add("部分绝对路径参数指向不存在的位置。");
            if (profile.Arguments.Any(a => a.Contains('=') || a.Contains('\"'))) findings.Add("含复合参数，需人工核对其中的路径；不会自动改写。");
        }
        foreach (var path in new[] { entry, Path.Combine(root, "Start.exe"), Path.Combine(root, "AdvHD.exe") }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            if (File.Exists(path) && LocalFiles.Within(root, path))
            {
                LocalFiles.CheckAncestors(path);
                var v = FileVersionInfo.GetVersionInfo(path);
                findings.Add($"引擎证据：{Path.GetFileName(path)} · {v.ProductName ?? "无产品名"} · {v.OriginalFilename ?? "无原文件名"} · {v.FileVersion ?? "无版本"}");
            }
        if (engine == GameEngine.AdvHd)
        {
            try { if (Japanese.GetString(Japanese.GetBytes(root)) != root) findings.Add("当前目录的日文代码页往返结果不同。"); }
            catch (EncoderFallbackException) { findings.Add("完整目录包含 CP932 无法编码的字符。"); }
            if (root.Any(c => !char.IsAscii(c))) findings.Add("若启动仍失败，可单独验证短 ASCII 目录或现有日文 LE 配置。");
            if (root.Length > 120) findings.Add("完整目录较长，旧引擎拼接资源路径时可能受限。");
        }
        if (engine == GameEngine.AdvGame && File.Exists(Path.Combine(root, ConfigName)))
            findings.Add("可检查 install.inf 的安装目录。二进制 env 文件交由游戏自身处理。");
        return new(engine, root, findings);
    }

    public static GameEngine Detect(string root, string? entry = null)
    {
        root = ExistingDirectory(root);
        var names = new[] { entry, Path.Combine(root, "Start.exe"), Path.Combine(root, "AdvHD.exe") };
        var detected = new HashSet<GameEngine>();
        foreach (var path in names.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.IsPathFullyQualified(path) || !LocalFiles.Within(root, path) || !File.Exists(path)) continue;
            LocalFiles.CheckAncestors(path);
            var version = FileVersionInfo.GetVersionInfo(path);
            if (string.Equals(version.ProductName, "AdvGame Module", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(version.OriginalFilename, "AdvGame.exe", StringComparison.OrdinalIgnoreCase)) detected.Add(GameEngine.AdvGame);
            if (string.Equals(version.OriginalFilename, "AdvHD.exe", StringComparison.OrdinalIgnoreCase) ||
                version.ProductName is "ADVPlayerHD" or "ADVプレイヤHD") detected.Add(GameEngine.AdvHd);
        }
        return detected.Count == 1 ? detected.Single() : GameEngine.Unknown;
    }

    public InstallPathRepair? PreviewInstall(string installationId, string root, string? entry = null)
    {
        if (Detect(root, entry) != GameEngine.AdvGame) throw new InvalidOperationException("该安装未确认使用 AdvGame Module。");
        root = ExistingDirectory(root);
        var path = Path.Combine(root, ConfigName);
        var original = ReadConfig(path);
        var result = RewriteInstall(original, root);
        if (result is null) return null;
        if (LocalFiles.Exists(result.Value.Old)) throw new IOException("旧目录仍存在，需核对是否为另一份安装。");
        return new(Guid.NewGuid().ToString("D"), installationId, root, NativeFiles.Identity(root), original, result.Value.Bytes,
            result.Value.Old, root, result.Value.Mirror, entry);
    }

    public static (byte[] Bytes, string Old, bool Mirror)? RewriteInstall(byte[] original, string root)
    {
        var text = Japanese.GetString(original);
        if (text.Contains('\0') || !Japanese.GetBytes(text).AsSpan().SequenceEqual(original)) throw new InvalidDataException("install.inf 不是可无损回写的 CP932 文本。");
        var section = "";
        var matches = new Dictionary<string, (string Value, int Index)>(StringComparer.OrdinalIgnoreCase);
        var pathSeen = false;
        foreach (Match line in IniLine.Matches(text))
        {
            if (line.Groups["section"].Success)
            {
                section = line.Groups["section"].Value;
                if (!section.Equals("[PATH]", StringComparison.OrdinalIgnoreCase)) continue;
                if (pathSeen) throw new InvalidDataException("install.inf 包含重复 PATH 区段。");
                pathSeen = true;
            }
            else if (section.Equals("[PATH]", StringComparison.OrdinalIgnoreCase))
            {
                var raw = line.Groups["raw"];
                var value = IniValue.Match(raw.Value).Groups["path"];
                if (!value.Success || !matches.TryAdd(line.Groups["key"].Value, (value.Value, raw.Index + value.Index)))
                    throw new InvalidDataException("install.inf 路径字段格式不支持或重复。");
            }
        }
        if (!matches.TryGetValue("Current", out var current)) throw new InvalidDataException("未找到可识别的 [PATH] Current 字段。");
        var old = LocalFiles.Full(current.Value);
        root = LocalFiles.Full(root);
        if (LocalFiles.Same(old, root)) return null;
        var changes = new List<(string Value, int Index)> { current };
        if (matches.TryGetValue("CDDrive", out var media) && string.Equals(media.Value.TrimEnd('\\'), old, StringComparison.OrdinalIgnoreCase)) changes.Add(media);
        foreach (var value in changes.OrderByDescending(g => g.Index))
        {
            var replacement = root + (value.Value.EndsWith('\\') ? "\\" : "");
            text = text.Remove(value.Index, value.Value.Length).Insert(value.Index, replacement);
        }
        var bytes = Japanese.GetBytes(text);
        if (Japanese.GetString(bytes) != text) throw new InvalidDataException("目标目录无法按原代码页保存。");
        return (bytes, old, changes.Count > 1);
    }

    public async Task ApplyInstallAsync(InstallPathRepair plan, CancellationToken ct)
    {
        using var lease = await LocalFileLease.AcquireAsync(stateDirectory, "engine-path-repair", ct);
        var path = VerifyRoot(plan);
        if (!ValidRewrite(plan) || Detect(plan.Root, plan.Entry) != GameEngine.AdvGame || LocalFiles.Exists(plan.OldPath))
            throw new InvalidDataException("引擎或路径条件已变更，请重新检查。");
        RequireBytes(path, plan.Original);
        await JsonSnapshotStore.AtomicWriteAsync(Receipt(plan.Id), plan, false, ct);
        await ReplaceConfigAsync(path, plan.Original, plan.Updated, ct);
    }

    public InstallPathRepair? LatestInstallRepair(string installationId, string root)
        => InstallRepairs(installationId).FirstOrDefault(plan => LocalFiles.Same(plan.Root, root));

    internal IEnumerable<InstallPathRepair> InstallRepairs(string installationId)
    {
        var directory = Path.Combine(stateDirectory, "engine-path-repairs");
        LocalFiles.CheckAncestors(directory);
        if (!Directory.Exists(directory)) yield break;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            LocalFiles.CheckAncestors(file);
            if (new FileInfo(file).Length > 65536) continue;
            var plan = RunJournal.Read<InstallPathRepair>(file);
            if (plan.InstallationId == installationId) yield return plan;
        }
    }

    public async Task RestoreInstallAsync(InstallPathRepair plan, CancellationToken ct)
        => await RestoreResolvedAsync(new(plan, plan.Root), ct);

    internal async Task RestoreResolvedAsync(InstallRepairRestore resolved, CancellationToken ct)
    {
        using var lease = await LocalFileLease.AcquireAsync(stateDirectory, "engine-path-repair", ct);
        var plan = resolved.Repair;
        var saved = RunJournal.Read<InstallPathRepair>(Receipt(plan.Id));
        var root = ExistingDirectory(resolved.Root);
        if (NativeFiles.Identity(root) != saved.Identity) throw new IOException("安装目录身份已变化，请重新检查。");
        var path = Path.Combine(root, ConfigName);
        if (LocalArchiveFormat.Hash(saved) != LocalArchiveFormat.Hash(plan) || !ValidRewrite(saved))
            throw new InvalidDataException("备份已变化或包含未知改动，请重新核对。");
        await ReplaceConfigAsync(path, saved.Updated, saved.Original, ct);
    }
    private static bool ValidRewrite(InstallPathRepair plan) =>
        RewriteInstall(plan.Original, plan.Root) is { } change && change.Bytes.AsSpan().SequenceEqual(plan.Updated) &&
        LocalFiles.Same(change.Old, plan.OldPath) && LocalFiles.Same(plan.Root, plan.NewPath) && change.Mirror == plan.MirrorsMediaPath;

    private string Receipt(string id)
    {
        if (!Guid.TryParseExact(id, "D", out _)) throw new ArgumentException("无效的配置修复记录。");
        var path = Path.Combine(stateDirectory, "engine-path-repairs", id + ".json");
        LocalFiles.CheckAncestors(path);
        return path;
    }

    private static string ExistingDirectory(string root)
    {
        root = LocalFiles.Full(root);
        LocalFiles.CheckAncestors(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        return root;
    }

    private static string VerifyRoot(InstallPathRepair plan)
    {
        var root = ExistingDirectory(plan.Root);
        if (NativeFiles.Identity(root) != plan.Identity) throw new IOException("安装目录已变化，请重新检查。");
        return Path.Combine(root, ConfigName);
    }

    private static byte[] ReadConfig(string path)
    {
        LocalFiles.CheckAncestors(path);
        if (new FileInfo(path).Length is < 1 or > 8192) throw new InvalidDataException("install.inf 大小超出已支持范围。");
        return File.ReadAllBytes(path);
    }

    private static void RequireBytes(string path, byte[] expected)
    {
        if (!CryptographicOperations.FixedTimeEquals(ReadConfig(path), expected)) throw new IOException("配置内容已变化，请重新预览。");
    }

    private static async Task ReplaceConfigAsync(string path, byte[] before, byte[] after, CancellationToken ct)
    {
        RequireBytes(path, before);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, ".vn-path-repair-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await stream.WriteAsync(after, ct); stream.Flush(flushToDisk: true); }
            RequireBytes(path, before);
            ct.ThrowIfCancellationRequested();
            File.Replace(temp, path, null);
            RequireBytes(path, after);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
