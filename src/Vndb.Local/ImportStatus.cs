using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

public sealed record ImportFeedbackRow(string Kind, string Key, string Status, string? Message);
public sealed record LocalImportStatus(ImportLimits? Limits, IReadOnlyList<ImportFeedbackRow> Results, bool Truncated);

/// <summary>Bounded read-only projection of existing consumer receipts. No new queue or business database.</summary>
public static class ImportStatus
{
    public static async Task<LocalImportStatus> ReadAsync(ISyncApi? api, LocalQueryScope scope, string activityDirectory, CancellationToken ct = default)
    {
        var info = api is null ? null : await api.OrganizationInfoAsync(ct);
        await QueryScopeBinding.CheckAsync(scope.DirectoryPath, scope.Origin, false, ct);
        var rows = new List<ImportFeedbackRow>(); var truncated = false; var metadataReceipts = new HashSet<string>(StringComparer.Ordinal);
        async Task ReadReceipts<T>(string directory, Func<T, Task<IEnumerable<ImportFeedbackRow>>> project)
        {
            if (!Directory.Exists(directory)) return;
            LocalFileLease.RequireOrdinaryAncestors(directory);
            var files = Directory.EnumerateFiles(directory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(9).ToArray();
            if (files.Length > 8) truncated = true;
            foreach (var file in files.Take(8))
            {
                var receipt = JsonSerializer.Deserialize<T>(await QueryDataMigration.BytesAsync(file, ct), VndbJson.Options)
                    ?? throw new InvalidDataException("导入回执无效，原件保留。");
                foreach (var row in await project(receipt))
                    if (rows.Count < 100) rows.Add(row); else truncated = true;
            }
        }
        await ReadReceipts<ClientBatchReceipt>(Path.Combine(scope.DirectoryPath, "receipts"), async receipt =>
        {
            if (receipt.Server != scope.Origin) return [];
            var id = receipt.ClientBatchId;
            if (string.IsNullOrEmpty(id) || id.Length > 100 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw new InvalidDataException("metadata回执批次ID无效，原件保留。");
            metadataReceipts.Add(id);
            var original = Path.Combine(scope.DirectoryPath, "pending", id + ".json");
            if (!File.Exists(original) || !MetadataReceiptProof.Matches(receipt, await QueryDataMigration.BytesAsync(original, ct), scope.Origin!, info?.ServiceInstanceId))
                return [new("metadata", id, "unconfirmed", "原请求证明缺失或不符；原件及回执保留，不能据此确认已接收。")];
            return receipt.Receipt.Items.Select(r => new ImportFeedbackRow("metadata", r.VndbId ?? r.OperationId, r.Status, r.Message));
        });
        var pendingDirectory = Path.Combine(scope.DirectoryPath, "pending");
        LocalFileLease.RequireOrdinaryAncestors(pendingDirectory);
        if (Directory.Exists(pendingDirectory))
        {
            var pendingFiles = Directory.EnumerateFiles(pendingDirectory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(9).ToArray();
            if (pendingFiles.Length > 8) truncated = true;
            foreach (var file in pendingFiles.Take(8))
            {
                var id = Path.GetFileNameWithoutExtension(file); if (metadataReceipts.Contains(id)) continue;
                var receiptFile = Path.Combine(scope.DirectoryPath, "receipts", id + ".json");
                if (File.Exists(receiptFile)) { truncated = true; continue; }
                var bound = File.Exists(Path.Combine(scope.DirectoryPath, "metadata-operations", id + ".json"));
                if(rows.Count < 100) rows.Add(new("metadata", id, bound ? "unconfirmed" : "waiting_web", bound ? "已封存原操作，尚无有效接收回执；请沿原ID核对／重试。" : "资料JSON已保存在本机，等待Web接收。")); else truncated = true;
            }
        }
        var bindingFile = Path.Combine(activityDirectory, "binding.json");
        if (File.Exists(bindingFile))
        {
            var binding = JsonSerializer.Deserialize<ActivityBinding>(await QueryDataMigration.BytesAsync(bindingFile, ct), VndbJson.Options);
            if (binding is not null && binding.Origin == scope.Origin && (info is null || binding.ServiceInstanceId == info.ServiceInstanceId))
                await ReadReceipts<ActivityReceipt>(Path.Combine(activityDirectory, "receipts"), receipt => Task.FromResult<IEnumerable<ImportFeedbackRow>>(
                    receipt.ServiceInstanceId == binding.ServiceInstanceId ? receipt.Results.Select(r => new ImportFeedbackRow(r.Kind, r.Key, r.Status, r.Message)) : []));
        }
        return new(info is null ? null : (info.ImportLimits ?? new()).Validate(), rows, truncated);
    }
}
