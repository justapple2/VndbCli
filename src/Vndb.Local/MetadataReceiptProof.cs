using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

internal sealed record MetadataOperationBinding(string Server, string ServiceInstanceId, string Sha256);

/// <summary>Proof of receipt of the exact wire request; acceptance is not completion of enrichment.</summary>
internal static class MetadataReceiptProof
{
    public static bool Matches(ClientBatchReceipt? local, byte[] original, string origin, string? instance = null)
    {
        if (local?.Receipt is not { } receipt || local.Server != origin || local.LocalSha256 != QueryDataMigration.Hash(original) ||
            string.IsNullOrWhiteSpace(receipt.ServiceInstanceId) || instance is not null && receipt.ServiceInstanceId != instance ||
            local.PayloadSha256 != receipt.PayloadSha256) return false;
        using var json = JsonDocument.Parse(original);
        var batch = json.RootElement.Deserialize<MetadataBatch>(VndbJson.Options);
        if (batch is null || batch.Format != "vn-library-batch" || batch.FormatVersion != 1 || batch.Kind != "metadata" ||
            batch.Items is not { Count: > 0 } || local.ClientBatchId != batch.BatchId || receipt.ClientBatchId != batch.BatchId ||
            !receipt.Accepted || receipt.State is not ("queued" or "processing" or "paused" or "complete") ||
            !LocalArchiveFormat.IsHash(receipt.PayloadSha256) || receipt.Id != "import-" + receipt.PayloadSha256 ||
            receipt.RawSha256 != QueryDataMigration.Hash(JsonSerializer.SerializeToUtf8Bytes(json.RootElement, VndbJson.Options)) ||
            receipt.Total != batch.Items.Count || receipt.Items is null || receipt.Items.Count != batch.Items.Count ||
            receipt.Completed < 0 || receipt.Completed > receipt.Total || receipt.State == "complete" && receipt.Completed != receipt.Total) return false;
        // Reuse the server's pure normalization; never duplicate its operation/payload hashing.
        var parsed = MetadataPayload.Parse(json.RootElement, new(MaximumItems: 5000));
        if (parsed.Items.Any(i => i.Error is not null) || receipt.PayloadSha256 != QueryDataMigration.Hash(JsonSerializer.SerializeToUtf8Bytes(parsed.Items, VndbJson.Options))) return false;
        var seen = new HashSet<int>();
        foreach (var row in receipt.Items)
        {
            if (row is null || row.Index < 0 || row.Index >= batch.Items.Count || !seen.Add(row.Index) ||
                !LocalArchiveFormat.IsHash(row.OperationId) || row.Status is not ("preview" or "existing" or "needs_review" or "imported" or "queued") ||
                receipt.State == "complete" && row.Status == "preview") return false;
            var item = parsed.Items[row.Index];
            if (row.OperationId != item.OperationId || row.VndbId != item.VndbId || row.Title != item.Title) return false;
        }
        return true;
    }
}
