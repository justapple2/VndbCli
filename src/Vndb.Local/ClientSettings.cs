using Vndb.Contracts;
using System.Text.Json;
using Vndb.Core;

namespace Vndb.Local;

public sealed record ClientSettings(string? Server = null, string? CacheDirectory = null)
{
    public static string UserDirectory => Environment.GetEnvironmentVariable("VN_DESKTOP_HOME") ?? LocalDataRoot.Default;
    public static ClientSettings Load(string? file = null)
    {
        file ??= DefaultFile(UserDirectory);
        return File.Exists(file) ? JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(file), VndbJson.Options)
            ?? throw new ArgumentException("客户端配置为空。") : new();
    }
    public static string DefaultFile(string directory) => File.Exists(Path.Combine(directory, "desktop-settings.json"))
        ? Path.Combine(directory, "desktop-settings.json") : Path.Combine(directory, "client.json");
    public static Uri ServerUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("--server 必须是 HTTP(S) 服务根地址，不能含凭据、查询或子路径。");
        return uri;
    }
}
