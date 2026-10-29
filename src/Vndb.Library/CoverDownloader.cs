using System.Security.Cryptography;
using Vndb.Core;

namespace Vndb.Library;

public sealed class CoverDownloader(HttpClient httpClient, string dataDirectory)
{
    private const int MaxBytes = CoverPolicy.MaximumBytes;
    public async Task<CoverAsset> DownloadAsync(NovelImage image, CancellationToken cancellationToken)
    {
        var uri = CoverPolicy.ValidateSource(image.Url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("封面超过 10 MiB。");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var bytes = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token)) != 0)
        {
            if (bytes.Length + read > MaxBytes) throw new InvalidDataException("封面超过 10 MiB。");
            bytes.Write(buffer, 0, read);
        }
        var content = bytes.ToArray();
        var (extension, contentType) = Detect(content);
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var asset = new CoverAsset(hash, "covers/" + hash + extension, contentType, content.Length, uri.ToString(), DateTimeOffset.UtcNow);
        var path = LibraryRules.CoverPath(dataDirectory, asset);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) return asset;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, content, cancellationToken);
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return asset;
    }
    private static (string, string) Detect(byte[] content)
    {
        if (content.AsSpan().StartsWith(new byte[] { 0xff, 0xd8, 0xff })) return (".jpg", "image/jpeg");
        if (content.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return (".png", "image/png");
        if (content.Length >= 12 && content.AsSpan(0, 4).SequenceEqual("RIFF"u8) && content.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return (".webp", "image/webp");
        throw new InvalidDataException("封面不是支持的 JPEG、PNG 或 WebP 图片。");
    }
}
