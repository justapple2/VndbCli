using System.Security.Cryptography;
using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

public sealed record QueryMigrationFile(string Source, string RelativeTarget, string Sha256, string Proof);
public sealed record QueryMigrationReview(string Source, string Sha256, string Reason);
public sealed record QueryMigrationPreview(string Id, string? Origin, string TargetDirectory, IReadOnlyList<string> Sources,
    IReadOnlyList<QueryMigrationFile> Files, IReadOnlyList<QueryMigrationReview> Review);
public sealed record QueryMigrationIntent(QueryMigrationPreview Preview, string ApprovedSha256);

/// <summary>Explicit, copy-only adoption of proven query JSON. Unknown files stay outside the active queue.</summary>
public sealed class QueryDataMigration(LocalQueryScope scope)
{
    internal static async Task<byte[]> BytesAsync(string file, CancellationToken ct)
    {
        LocalFileLease.RequireOrdinaryAncestors(file);
        if (new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidDataException("查询状态文件超过8MiB，原件保留。");
        return await File.ReadAllBytesAsync(file, ct);
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static T Read<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, VndbJson.Options) ?? throw new InvalidDataException("查询迁移JSON无效，原件保留。");
    public async Task<QueryMigrationPreview> PreviewAsync(IReadOnlyList<string>? sources = null, CancellationToken ct = default)
    {
        var roots = (sources ?? scope.LegacyDirectories).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        var files = new List<QueryMigrationFile>(); var review = new List<QueryMigrationReview>();
        foreach (var root in roots)
        {
            LocalFileLease.RequireOrdinaryAncestors(root);
            if (string.Equals(root, scope.DirectoryPath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("迁移源不能是当前查询目录。");
            if (!Directory.Exists(root)) continue;
            var proven = QueryScopeBinding.IsHistoricalScope(root, scope.Origin); var foreign = false;
            var marker = Path.Combine(root, "query-scope.json");
            if (File.Exists(marker))
            {
                var binding = Read<QueryScopeBinding>(await BytesAsync(marker, ct));
                proven = binding.FormatVersion == 1 && binding.Origin == scope.Origin;
                foreign = !proven;
            }
            var batches = new Dictionary<string, MetadataBatch>(StringComparer.Ordinal);
            foreach (var kind in new[] { "pending", "metadata-operations", "receipts", "cache" })
            {
                var folder = Path.Combine(root, kind); LocalFileLease.RequireOrdinaryAncestors(folder); if (!Directory.Exists(folder)) continue;
                var paths = Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal).Take(10001).ToArray();
                if (paths.Length > 10000) throw new InvalidDataException("迁移条目过多，请明确更小来源目录。");
                foreach (var path in paths)
                {
                    ct.ThrowIfCancellationRequested(); var bytes = await BytesAsync(path, ct); var sha = Hash(bytes); var target = Path.Combine(kind, Path.GetFileName(path));
                    try
                    {
                        var proof = proven ? "scope" : null;
                        if (kind == "pending")
                        {
                            var batch = Read<MetadataBatch>(bytes);
                            if (batch.Format != "vn-library-batch" || batch.FormatVersion != 1 || batch.Kind != "metadata" || batch.BatchId != Path.GetFileNameWithoutExtension(path) || batch.Items is null) throw new InvalidDataException("无效批次");
                            var receiptFile = Path.Combine(root, "receipts", batch.BatchId + ".json");
                            if (File.Exists(receiptFile))
                            {
                                var receipt = Read<ClientBatchReceipt>(await BytesAsync(receiptFile, ct));
                                if (receipt.Server == scope.Origin && receipt.LocalSha256 == sha && receipt.Receipt.Accepted && receipt.ClientBatchId == batch.BatchId && receipt.Receipt.ClientBatchId == batch.BatchId) proof ??= "receipt_sha256";
                                else if (!proven) proof = null;
                            }
                            if (proof is not null) batches[batch.BatchId] = batch;
                        }
                        else if (kind == "metadata-operations")
                        {
                            using var op = JsonDocument.Parse(bytes); var id = Path.GetFileNameWithoutExtension(path);
                            if (!batches.ContainsKey(id) || !op.RootElement.TryGetProperty("server", out var server) || server.GetString() != scope.Origin ||
                                !op.RootElement.TryGetProperty("service_instance_id", out var instance) || string.IsNullOrWhiteSpace(instance.GetString()) ||
                                !op.RootElement.TryGetProperty("sha256", out var hash) || hash.GetString() != Hash(await BytesAsync(Path.Combine(root, "pending", id + ".json"), ct))) proof = null;
                            else proof ??= "batch_operation";
                        }
                        else if (kind == "receipts")
                        {
                            var receipt = Read<ClientBatchReceipt>(bytes);
                            if (!batches.ContainsKey(receipt.ClientBatchId) || receipt.Server != scope.Origin) proof = null;
                            else proof = "batch_receipt";
                        }
                        else
                        {
                            var cached = Read<JsonSnapshotStore.CachedQuery>(bytes);
                            if (cached.Origin is { } origin && origin != scope.Origin) proof = null;
                            else if (cached.BatchId is { } id && batches.TryGetValue(id, out var batch) && LocalArchiveFormat.Hash(cached.Response.Items) == LocalArchiveFormat.Hash(batch.Items)) proof ??= "batch_receipt";
                            else if (cached.BatchId is null && batches.Values.Count(b => LocalArchiveFormat.Hash(b.Items) == LocalArchiveFormat.Hash(cached.Response.Items)) == 1) proof ??= "unique_batch_receipt";
                            target = Path.Combine("cache", JsonSnapshotStore.CacheFileName(cached.Query));
                        }
                        if (proof is null || foreign) review.Add(new(path, sha, foreign ? "foreign_scope" : "origin_unproven"));
                        else files.Add(new(path, target, sha, proof));
                    }
                    catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException or NullReferenceException)
                    { review.Add(new(path, sha, "invalid_json_preserved")); }
                }
            }
        }
        return new(Guid.NewGuid().ToString("D"), scope.Origin, scope.DirectoryPath, roots, files, review);
    }
    public async Task ApplyAsync(QueryMigrationPreview preview, string approvedSha256, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(preview.Id, "D", out _) || LocalArchiveFormat.Hash(preview) != approvedSha256 || preview.Origin != scope.Origin || preview.TargetDirectory != scope.DirectoryPath)
            throw new ArgumentException("查询迁移批准摘要或作用域不符。");
        var current = await PreviewAsync(preview.Sources, ct);
        if (LocalArchiveFormat.Hash(current with { Id = preview.Id }) != approvedSha256) throw new InvalidDataException("迁移来源变化，请重新预览；原件保留。");
        await QueryScopeBinding.CheckAsync(scope.DirectoryPath, scope.Origin, true, ct);
        var file = Path.Combine(scope.DataRoot, "query-migrations", preview.Id + ".json");
        using var lease = await LocalFileLease.AcquireAsync(scope.DataRoot, "query-migration/" + preview.Id, ct);
        var intent = new QueryMigrationIntent(preview, approvedSha256);
        if (File.Exists(file)) { if (LocalArchiveFormat.Hash(Read<QueryMigrationIntent>(await BytesAsync(file, ct))) != LocalArchiveFormat.Hash(intent)) throw new InvalidDataException("已有迁移意图不同。"); }
        else await JsonSnapshotStore.AtomicWriteAsync(file, intent, false, ct);
        if (preview.Files.GroupBy(f => f.RelativeTarget, StringComparer.OrdinalIgnoreCase).Any(g => g.Select(f => f.Sha256).Distinct().Count() > 1))
            throw new InvalidDataException("来源有同名异内容，请明确选择一个来源后重新预览。");
        foreach (var item in preview.Files)
        {
            var target = Path.GetFullPath(Path.Combine(scope.DirectoryPath, item.RelativeTarget));
            if (!LocalFiles.Within(scope.DirectoryPath, target)) throw new InvalidDataException("迁移目标越界。");
            if (File.Exists(target) && Hash(await BytesAsync(target, ct)) != item.Sha256) throw new InvalidDataException("目标有同名异内容，未覆盖。");
        }
        foreach (var item in preview.Files)
        {
            var target = Path.GetFullPath(Path.Combine(scope.DirectoryPath, item.RelativeTarget));
            if (!LocalFiles.Within(scope.DirectoryPath, target)) throw new InvalidDataException("迁移目标越界。");
            using var resource = await LocalFileLease.AcquireAsync(scope.DirectoryPath, "query-file/" + item.RelativeTarget.Replace('\\', '/'), ct);
            var bytes = await BytesAsync(item.Source, ct); if (Hash(bytes) != item.Sha256) throw new InvalidDataException("迁移来源字节变化，未复制。");
            if (File.Exists(target))
            {
                if (Hash(await BytesAsync(target, ct)) != item.Sha256) throw new InvalidDataException("目标有同名异内容，未覆盖；原件和迁移意图保留。");
                continue;
            }
            await JsonSnapshotStore.AtomicBytesAsync(target, bytes, ct);
        }
        var done = Path.Combine(scope.DataRoot, "query-migrations", preview.Id + ".completed.json");
        if (!File.Exists(done)) await JsonSnapshotStore.AtomicWriteAsync(done, intent, false, ct);
    }
}
