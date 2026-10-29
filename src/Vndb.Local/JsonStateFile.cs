using System.Security.Cryptography;
using System.Text.Json;
using Vndb.Core;

namespace Vndb.Local;

/// <summary>JSON read/compare/replace primitive, not a second business database.</summary>
internal sealed class JsonStateFile<T>(string directory, string name) where T : class
{
    private readonly string _file = Path.Combine(directory, name);
    private string? _observed;
    private static string Hash(byte[]? bytes) => bytes is null ? "missing" : Convert.ToHexStringLower(SHA256.HashData(bytes));
    private async Task<byte[]?> BytesAsync(CancellationToken ct)
    {
        LocalFileLease.RequireOrdinaryAncestors(_file);
        if (!File.Exists(_file)) return null;
        if ((File.GetAttributes(_file) & FileAttributes.ReparsePoint) != 0 || new FileInfo(_file).Length > 8 * 1024 * 1024) throw new InvalidDataException("本机JSON不是普通文件或过大。");
        return await File.ReadAllBytesAsync(_file, ct);
    }
    public async Task ObserveAsync(CancellationToken ct) => _observed = Hash(await BytesAsync(ct));
    public async Task<T?> ReadAsync(CancellationToken ct, bool observe = true)
    {
        var bytes = await BytesAsync(ct);
        var value = bytes is null ? null : JsonSerializer.Deserialize<T>(bytes, VndbJson.Options) ?? throw new InvalidDataException("本机JSON无效，原件保留。");
        if (observe) _observed = Hash(bytes); return value;
    }
    public async Task SaveAsync(T value, CancellationToken ct)
    {
        using var lease = await LocalFileLease.AcquireAsync(directory, "json-state/" + name, ct);
        var current = await BytesAsync(ct); var hash = Hash(current);
        if (current is not null && JsonSerializer.Serialize(JsonSerializer.Deserialize<T>(current, VndbJson.Options), VndbJson.Options) == JsonSerializer.Serialize(value, VndbJson.Options))
        { _observed = hash; return; } // Do not rewrite equivalent legacy bytes or unknown extension fields.
        if (current is not null && _observed is null || _observed is not null && _observed != hash)
            throw new InvalidDataException("本机JSON已被其他客户端修改，请重新读取后编辑；原件保留。");
        await JsonSnapshotStore.AtomicWriteAsync(_file, value, true, ct, 8 * 1024 * 1024);
        _observed = Hash(await BytesAsync(ct));
    }
    public async Task<T> UpdateAsync(Func<T?, T> update, CancellationToken ct)
    {
        using var lease = await LocalFileLease.AcquireAsync(directory, "json-state/" + name, ct);
        var current = await BytesAsync(ct);
        var prior = current is null ? null : JsonSerializer.Deserialize<T>(current, VndbJson.Options) ?? throw new InvalidDataException("本机JSON无效，原件保留。");
        var value = update(prior);
        if (current is not null && JsonSerializer.Serialize(prior, VndbJson.Options) == JsonSerializer.Serialize(value, VndbJson.Options))
        { _observed = Hash(current); return value; }
        await JsonSnapshotStore.AtomicWriteAsync(_file, value, true, ct, 8 * 1024 * 1024);
        _observed = Hash(await BytesAsync(ct)); return value;
    }
}
