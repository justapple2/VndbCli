namespace Vndb.Core;

public static class VndbId
{
    public static string Normalize(string value, char prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var id = value.Trim();
        if (Uri.TryCreate(id, UriKind.Absolute, out var url))
        {
            if (url.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(url.Host, "vndb.org", StringComparison.OrdinalIgnoreCase) ||
                !url.IsDefaultPort || url.UserInfo.Length != 0)
            {
                throw new ArgumentException("只接受 https://vndb.org/ 下的条目链接。");
            }
            id = url.AbsolutePath.Trim('/');
        }

        if (id.Length < 2 || char.ToLowerInvariant(id[0]) != prefix || id[1] == '0' ||
            id.AsSpan(1).ContainsAnyExceptInRange('0', '9'))
        {
            throw new ArgumentException($"无效的 VNDB ID：应为 {prefix} 加正整数，例如 {prefix}17。");
        }
        return prefix + id[1..];
    }
}
