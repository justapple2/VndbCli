using System.Security.Cryptography;
using System.Text;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Desktop.Services;

public sealed class CoverCache(HttpClient http, string directory, Uri? server)
{
    private readonly SemaphoreSlim _gate = new(3, 3);
    public Uri? Source(MetadataSnapshot snapshot)
    {
        if (server is not null && snapshot.Cover.Url is { } path)
        {
            var uri = new Uri(server, path);
            if (uri.GetLeftPart(UriPartial.Authority) != server.GetLeftPart(UriPartial.Authority) ||
                !uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal) || !uri.AbsolutePath.EndsWith("/cover", StringComparison.Ordinal) || uri.Query.Length != 0)
                throw new InvalidDataException("拒绝非本服务封面地址。");
            return uri;
        }
        return snapshot.Novel?.Image is { } image ? CoverPolicy.ValidateSource(image.Thumbnail ?? image.Url) : null;
    }
    public async Task<string?> GetAsync(MetadataSnapshot snapshot, CancellationToken ct)
    {
        var uri = Source(snapshot); if (uri is null) return null;
        var file = Path.Combine(directory, "cover-cache", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri + "|" + snapshot.Cover.Sha256))) + ".image");
        if (File.Exists(file)) return file;
        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(file)) return file;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not ("image/jpeg" or "image/png" or "image/webp") ||
                response.Content.Headers.ContentLength > CoverPolicy.MaximumBytes) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var bytes = new MemoryStream(); var buffer = new byte[32768];
            while (await stream.ReadAsync(buffer, timeout.Token) is var count && count > 0)
            {
                if (bytes.Length + count > CoverPolicy.MaximumBytes) throw new InvalidDataException("封面超过10MiB。");
                bytes.Write(buffer, 0, count);
            }
            // Do not cache HTML with a forged image type. Full decode is handled by the view.
            var data = bytes.ToArray();
            if (snapshot.Cover.Sha256 is { } expected && Convert.ToHexStringLower(SHA256.HashData(data)) != expected) return null;
            if (!(data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 || data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 ||
                data.Length >= 12 && Encoding.ASCII.GetString(data, 0, 4) == "RIFF" && Encoding.ASCII.GetString(data, 8, 4) == "WEBP")) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + "." + Guid.NewGuid().ToString("N");
            try { await File.WriteAllBytesAsync(temporary, data, ct); File.Move(temporary, file, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return file;
        }
        finally { _gate.Release(); }
    }
}
