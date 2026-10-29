using System.Security.Cryptography;
using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

public sealed class PendingMetadataSync(ISyncApi web, string server, string directory)
{
    public async Task<IReadOnlyList<ClientBatchReceipt>> SubmitAsync(CancellationToken ct = default, int maximumFiles = int.MaxValue)
    {
        server = ClientSettings.ServerUri(server).AbsoluteUri;
        var pending = Path.Combine(Path.GetFullPath(directory), "pending");
        if (!Directory.Exists(pending)) return [];
        LocalFileLease.RequireOrdinaryAncestors(pending);
        await QueryScopeBinding.CheckAsync(directory, server, true, ct);
        var info = await web.OrganizationInfoAsync(ct);
        var results = new List<ClientBatchReceipt>();
        var submitted = 0;
        foreach (var path in Directory.EnumerateFiles(pending, "*.json").Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("待同步文件不是普通 JSON 或超过8MiB。");
            var bytes = await File.ReadAllBytesAsync(path, ct);
            var localSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            using var json = JsonDocument.Parse(bytes);
            var payload = json.RootElement;
            if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("batch_id", out var batchId) || batchId.ValueKind != JsonValueKind.String) throw new InvalidDataException("待同步 JSON 缺少 batch_id。");
            var id = batchId.GetString();
            if (string.IsNullOrEmpty(id) || id.Length > 100 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw new InvalidDataException("无效的 pending batch_id。");
            using var lease = await LocalFileLease.AcquireAsync(directory, "metadata-submit/" + id, ct);
            if (QueryDataMigration.Hash(await QueryDataMigration.BytesAsync(path, ct)) != localSha) throw new InvalidDataException("等待提交时原批次变化。");
            var operationFile = Path.Combine(directory, "metadata-operations", id + ".json"); var operation = new MetadataOperationBinding(server, info.ServiceInstanceId, localSha);
            if (File.Exists(operationFile) && JsonSerializer.Deserialize<MetadataOperationBinding>(await QueryDataMigration.BytesAsync(operationFile, ct), VndbJson.Options) != operation)
                throw new InvalidDataException("未确认批次已绑定其他实例或原字节变化；未重用操作ID。");
            var receiptPath = Path.Combine(directory, "receipts", id + ".json");
            if (File.Exists(receiptPath))
            {
                var previous = JsonSerializer.Deserialize<ClientBatchReceipt>(await QueryDataMigration.BytesAsync(receiptPath, ct), VndbJson.Options);
                if (MetadataReceiptProof.Matches(previous, bytes, server, info.ServiceInstanceId)) { results.Add(previous!); continue; }
                throw new InvalidDataException("已有回执缺少原请求证明或内容不符；仍待确认，原件保留，未更换操作ID。");
            }
            if (submitted >= maximumFiles) break;
            ImportValidation.Metadata(payload, info.ImportLimits ?? new());
            if (!File.Exists(operationFile)) await JsonSnapshotStore.AtomicWriteAsync(operationFile, operation, false, ct);
            var remote = await web.SyncBatchAsync(payload, "sync-" + id, ct);
            submitted++;
            var result = new ClientBatchReceipt(id, remote.PayloadSha256, server, remote, DateTimeOffset.UtcNow, localSha);
            if (!MetadataReceiptProof.Matches(result, bytes, server, info.ServiceInstanceId))
            {
                await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(directory, "metadata-operations", "rejected", id, Guid.NewGuid().ToString("N") + ".json"), result, false, ct);
                throw new ClientApiException("sync_receipt_invalid", 200, "回执缺少原请求证明或逐项结果不符；仍待确认，原件及异常回执保留，请核对后按原操作重试。");
            }
            if (Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, ct))) != localSha)
                throw new InvalidDataException("提交期间待同步元数据发生变化，未确认本机操作。");
            await JsonSnapshotStore.AtomicWriteAsync(receiptPath, result, true, ct);
            results.Add(result);
        }
        return results;
    }
}
