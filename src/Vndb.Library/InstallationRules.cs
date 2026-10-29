using Vndb.Contracts;

namespace Vndb.Library;

/// <summary>Pure client-path validation; never uses the NAS filesystem to resolve a client path.</summary>
public static class InstallationRules
{
    public static void Text(string? text, int maximum = 500, bool required = true)
    {
        if (required && string.IsNullOrWhiteSpace(text) || text?.Length > maximum || text?.Any(char.IsControl) == true)
            throw new ArgumentException("字段为空、过长或包含控制字符。");
    }
    public static bool Windows(string path) => path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
    public static string PathKey(string path)
    {
        Text(path, 4096);
        var windows = Windows(path); var value = path.Replace('\\', '/');
        if ((!windows && !value.StartsWith('/')) || windows && !value.StartsWith("//", StringComparison.Ordinal) && (value.Length < 3 || value[2] != '/') ||
            value.Split('/').Any(p => p is "." or "..") || value.StartsWith("//?/", StringComparison.Ordinal) || value.StartsWith("//./", StringComparison.Ordinal))
            throw new ArgumentException("安装路径需要规范绝对路径，不能包含 .、.. 或设备路径。");
        if (value.StartsWith("//", StringComparison.Ordinal) && value.Split('/', StringSplitOptions.RemoveEmptyEntries).Length < 2) throw new ArgumentException("UNC路径需要服务器和共享名。");
        var segments = value.TrimStart('/').TrimEnd('/').Split('/');
        if (value != "/" && segments.Any(p => p.Length == 0 || windows && (p.EndsWith('.') || p.EndsWith(' '))))
            throw new ArgumentException("路径不能有重复分隔符或含歧义的Windows名称。");
        value = value.TrimEnd('/');
        if (value.Length == 0) value = "/";
        return windows ? value.ToUpperInvariant() : value;
    }
    public static string Relative(string path)
    {
        Text(path, 4096);
        if (path == ".") return path;
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.Contains(':')) || path.StartsWith('/')) throw new ArgumentException("组件路径须在安装容器内，不能是绝对路径或上级路径。");
        return string.Join('/', parts);
    }
    public static string Join(string root, string relative) => relative == "." ? root : root.TrimEnd('/', '\\') + (Windows(root) ? "\\" : "/") + Relative(relative).Replace('/', Windows(root) ? '\\' : '/');
    public static string Under(string parent, string child)
    {
        var p = PathKey(parent).TrimEnd('/') + "/"; var c = PathKey(child);
        if (!c.StartsWith(p, StringComparison.Ordinal)) throw new ArgumentException("合集中的安装必须位于容器根目录之内。");
        // Keys may fold Windows casing; slice the original normalized text to retain spelling.
        return Relative(child.Replace('\\', '/').TrimEnd('/')[p.Length..]);
    }
    public static void Component(ComponentData data)
    {
        ArgumentNullException.ThrowIfNull(data); Text(data.ItemId, 160); _ = Relative(data.RelativePath); Text(data.Label, required: false);
        if (data.Kind is not ("game" or "bonus" or "edition" or "other")) throw new ArgumentException("无效组件类型。");
    }
}
