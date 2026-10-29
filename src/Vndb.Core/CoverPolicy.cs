namespace Vndb.Core;

public static class CoverPolicy
{
    public const int MaximumBytes = 10 * 1024 * 1024;
    public static Uri ValidateSource(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host is not ("t.vndb.org" or "s.vndb.org") || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw new InvalidDataException("封面地址不在 VNDB 图片域名范围内。");
        return uri;
    }
}
