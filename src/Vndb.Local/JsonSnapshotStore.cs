using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

/// <summary>Mutable query index and immutable pending batches. No automatic pending eviction.</summary>
public sealed class JsonSnapshotStore(string directory, string? serverOrigin = null) : ICatalogSnapshots
{
    async Task<CatalogResult?> ICatalogSnapshots.ReadAsync(CatalogQuery query, CancellationToken ct) =>
        (await ReadAsync(new(query.Kind, query.ToMetadata(), query.Id), ct))?.ToCatalog();
    Task ICatalogSnapshots.SaveAsync(CatalogQuery query, CatalogResult result, CancellationToken ct) =>
        SaveAsync(new(query.Kind, query.ToMetadata(), query.Id), result.ToMetadata(), ct);
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    internal static ClientQuery CacheQuery(ClientQuery query)
    {
        query = query.Validate();
        var detail = query.Kind == "vn_releases" || query.Kind.EndsWith("_get", StringComparison.Ordinal);
        return query with { Request = query.Request with { Mode = "local_first", Query = detail ? "" : query.Request.Query, ProducerId = detail ? null : query.Request.ProducerId,
            Page = detail ? 1 : query.Request.Page, PageSize = detail ? 1 : query.Request.PageSize } };
    }
    private static string LegacyName(ClientQuery query) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        query with { Request = query.Request with { Mode = "local_first" } }, VndbJson.Options))) + ".json";
    internal static string CacheFileName(ClientQuery query) => LegacyName(CacheQuery(query));
    private string Index(ClientQuery query) => Path.Combine(DirectoryPath, "cache", CacheFileName(query));
    public async Task SaveAsync(ClientQuery query, MetadataResponse response, CancellationToken ct)
    {
        await QueryScopeBinding.CheckAsync(DirectoryPath, serverOrigin, true, ct);
        using var lease = await LocalFileLease.AcquireAsync(DirectoryPath, "query-file/cache/" + CacheFileName(query), ct);
        string? batchId = null;
        if (response.PendingImport && response.Items.Count > 0)
        {
            var batch = new MetadataBatch(response.Items, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("D"));
            batchId = batch.BatchId;
            await AtomicWriteAsync(Path.Combine(DirectoryPath, "pending", batch.BatchId + ".json"), batch, false, ct);
        }
        await AtomicWriteAsync(Index(query), new CachedQuery(query, response, batchId, LocalQueryScope.Normalize(serverOrigin)), true, ct);
    }
    public async Task<MetadataResponse?> ReadAsync(ClientQuery query, CancellationToken ct)
    {
        var names = query.Kind.EndsWith("_get", StringComparison.Ordinal)
            ? new[] { CacheFileName(query), LegacyName(query) }.Concat(new[] { "", query.Id! }.SelectMany(q => new[] { 10, 20 }.Select(size => LegacyName(query with { Request = query.Request with { Query = q, PageSize = size } }))))
            : [CacheFileName(query), LegacyName(query)];
        var path = names.Select(n => Path.Combine(DirectoryPath, "cache", n)).FirstOrDefault(File.Exists);
        if (path is null) return null;
        await QueryScopeBinding.CheckAsync(DirectoryPath, serverOrigin, true, ct);
        var cached = JsonSerializer.Deserialize<CachedQuery>(await QueryDataMigration.BytesAsync(path, ct), VndbJson.Options)
            ?? throw new InvalidDataException("本机快照无效。");
        if (LocalArchiveFormat.Hash(CacheQuery(cached.Query)) != LocalArchiveFormat.Hash(CacheQuery(query)) || cached.Origin is { } origin && origin != LocalQueryScope.Normalize(serverOrigin))
            throw new InvalidDataException("查询快照键或来源不符，原件保留。");
        var pending = cached.Response.PendingImport;
        if (pending)
        {
            var batchIds = cached.BatchId is { } known ? new[] { known } : LegacyBatches(cached.Response.Items);
            foreach (var batchId in batchIds)
            {
                if (batchId.Length is < 1 or > 100 || batchId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw new InvalidDataException("快照批次ID无效，原件保留。");
                var receiptFile = Path.Combine(DirectoryPath, "receipts", batchId + ".json");
                if (!File.Exists(receiptFile)) continue;
                var receipt = JsonSerializer.Deserialize<ClientBatchReceipt>(await QueryDataMigration.BytesAsync(receiptFile, ct), VndbJson.Options);
                var original = Path.Combine(DirectoryPath, "pending", batchId + ".json");
                LocalFileLease.RequireOrdinaryAncestors(original);
                if (serverOrigin is not null && receipt?.ClientBatchId == batchId && File.Exists(original) &&
                    (File.GetAttributes(original) & FileAttributes.ReparsePoint) == 0 && new FileInfo(original).Length <= 8 * 1024 * 1024 &&
                    MetadataReceiptProof.Matches(receipt, await File.ReadAllBytesAsync(original, ct), LocalQueryScope.Normalize(serverOrigin)!)) { pending = false; break; }
            }
        }
        return cached.Response with { Source = "client_cache", PendingImport = pending,
            Warnings = [pending ? "本机历史快照，仍待同步。" : "本机历史快照；已接收的批次仍需以 Web 处理回执为准，不代表已核验所有资料。"] };
    }
    public static async Task AtomicWriteAsync<T>(string path, T data, bool overwrite, CancellationToken ct, long? maxBytes = null)
    {
        path = Path.GetFullPath(path);
        LocalFileLease.RequireOrdinaryAncestors(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, data, VndbJson.Options, ct);
                if (maxBytes is { } limit && stream.Length > limit) throw new InvalidDataException("本机JSON将超过读取上限，原件保留；请缩小登记范围。");
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private IEnumerable<string> LegacyBatches(IReadOnlyList<MetadataSnapshot> items)
    {
        var folder = Path.Combine(DirectoryPath, "pending");
        LocalFileLease.RequireOrdinaryAncestors(folder);
        if (!Directory.Exists(folder)) yield break;
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            MetadataBatch? batch;
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 8 * 1024 * 1024) continue;
                batch = JsonSerializer.Deserialize<MetadataBatch>(File.ReadAllText(path), VndbJson.Options);
            }
            catch (Exception e) when (e is JsonException or IOException) { continue; }
            if (batch is not null && JsonSerializer.Serialize(batch.Items, VndbJson.Options) == JsonSerializer.Serialize(items, VndbJson.Options)) yield return batch.BatchId;
        }
    }
    internal sealed record CachedQuery(ClientQuery Query, MetadataResponse Response, string? BatchId = null, string? Origin = null);
    internal static async Task AtomicBytesAsync(string path, byte[] bytes, CancellationToken ct)
    {
        LocalFileLease.RequireOrdinaryAncestors(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            { await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); stream.Flush(true); }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
