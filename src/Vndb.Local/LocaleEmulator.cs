using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Vndb.Local;

public sealed record LocaleProfile(string Id, string Name, string Language, bool Usable, string Description);
public sealed record LocaleLaunchRequest(string ToolPath, string ProfileId);
public sealed record LocaleLaunch(string ToolPath, string ToolSha256, string ProfileId, string ProfileSha256);

/// <summary>Read existing LE profiles; never install tools or modify their configuration.</summary>
public static class LocaleEmulator
{
    public static string ResolveTool(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("转区工具需要完整路径。");
        path = Path.GetFullPath(Directory.Exists(path) ? Path.Combine(path, "LEProc.exe") : path);
        if (!Path.GetFileName(path).Equals("LEProc.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new ArgumentException("请选择已有的LEProc.exe或其所在目录。");
        LocalFiles.CheckAncestors(path); return path;
    }

    private static XElement[] Read(string tool)
    {
        var file = Path.Combine(Path.GetDirectoryName(tool)!, "LEConfig.xml"); LocalFiles.CheckAncestors(file);
        if (!File.Exists(file) || new FileInfo(file).Length > 1024 * 1024) throw new InvalidDataException("LEConfig.xml缺失或超过1MiB。");
        try
        {
            using var reader = XmlReader.Create(file, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
            return XDocument.Load(reader).Root?.Element("Profiles")?.Elements("Profile").ToArray()
                ?? throw new InvalidDataException("Locale Emulator配置结构无效。");
        }
        catch (XmlException e) { throw new InvalidDataException("Locale Emulator配置XML无效，原文件保留。", e); }
    }
    private static bool Usable(XElement profile) => bool.TryParse(profile.Element("RunAsAdmin")?.Value, out var admin) && !admin &&
        bool.TryParse(profile.Element("RunWithSuspend")?.Value, out var suspended) && !suspended &&
        string.IsNullOrWhiteSpace(profile.Element("Parameter")?.Value);

    public static IReadOnlyList<LocaleProfile> Profiles(string toolPath) => Read(ResolveTool(toolPath)).Select(p => new LocaleProfile(
        p.Attribute("Guid")?.Value ?? "", p.Attribute("Name")?.Value ?? "", p.Element("Location")?.Value ?? "", Usable(p),
        Usable(p) ? "可选；保留原语言配置" : "不可选：需关闭管理员／挂起模式，并清空LE附加参数")).ToArray();

    public static async Task<LocaleLaunch> CaptureAsync(LocaleLaunchRequest request, CancellationToken ct = default)
    {
        var tool = ResolveTool(request.ToolPath);
        if (!Guid.TryParseExact(request.ProfileId, "D", out _)) throw new ArgumentException("请选择LEConfig.xml中的配置GUID。");
        var selected = Read(tool).Where(p => p.Attribute("Guid")?.Value == request.ProfileId).ToArray();
        if (selected.Length != 1 || !Usable(selected[0])) throw new ArgumentException("LE配置不存在、重复或需要管理员／挂起／附加参数；请选择普通运行配置。");
        await using var stream = File.OpenRead(tool);
        return new(tool, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)), request.ProfileId,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(selected[0].ToString(SaveOptions.DisableFormatting)))));
    }

    // LEProc rejoins parsed trailing arguments; retain quotes through that second parsing step.
    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}

public sealed record ExecutableCandidate(string Path, string Description);
public static class GameExecutables
{
    public static IReadOnlyList<ExecutableCandidate> Discover(string directory)
    {
        directory = LocalFiles.Full(directory); LocalFiles.CheckAncestors(directory);
        var excluded = new[] { "config", "setting", "setup", "install", "unins", "update", "patch", "checker", "file check", "savedata file", "設定", "破損", "チェック", "修復", "卸载", "设置" };
        var candidates = new List<ExecutableCandidate>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.exe").Order(StringComparer.OrdinalIgnoreCase))
        {
            LocalFiles.CheckAncestors(path); var info = FileVersionInfo.GetVersionInfo(path);
            var description = string.Join(" · ", new[] { info.FileDescription, info.ProductName }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var text = Path.GetFileName(path) + " " + description;
            if (Path.GetFileNameWithoutExtension(path).Equals("inst", StringComparison.OrdinalIgnoreCase) || excluded.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase))) continue;
            candidates.Add(new(path, description));
            if (candidates.Count > 200) throw new IOException("EXE候选过多，请手动选择。");
        }
        return candidates;
    }
}
