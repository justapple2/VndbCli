using Vndb.Contracts;

namespace Vndb.Client;

public sealed partial class WebLibraryClient
{
    public Task<DatasetSyncStatus> DatasetSyncAsync(CancellationToken ct = default) => SendAsync<DatasetSyncStatus>(HttpMethod.Get, "api/v1/datasets/sync", null, ct);
    public Task<DatasetSyncStatus> CheckDatasetAsync(string key, CancellationToken ct = default) => SendAsync<DatasetSyncStatus>(HttpMethod.Post, "api/v1/datasets/sync/check", null, ct, key);
    public Task<DatasetSyncResult> UpdateDatasetAsync(string key, CancellationToken ct = default) => SendAsync<DatasetSyncResult>(HttpMethod.Post, "api/v1/datasets/sync/update", null, ct, key);
    public Task<DatasetSyncSettings> ConfigureDatasetSyncAsync(DatasetSyncWrite request, string key, CancellationToken ct = default) => SendAsync<DatasetSyncSettings>(HttpMethod.Post, "api/v1/datasets/sync/settings", request, ct, key);
    public Task<IReadOnlyList<DatasetJob>> DatasetJobsAsync(CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<DatasetJob>>(HttpMethod.Get, "api/v1/datasets", null, ct);
    public Task<DatasetJob> CreateDatasetAsync(DatasetRequest request, string key, CancellationToken ct = default) =>
        SendAsync<DatasetJob>(HttpMethod.Post, "api/v1/datasets", request, ct, key);
    public Task<DatasetJob> ControlDatasetAsync(string id, string action, DatasetRevision revision, string key, CancellationToken ct = default)
    {
        if (action is not ("pause" or "resume" or "cancel")) throw new ArgumentException("未知导出任务操作。");
        return SendAsync<DatasetJob>(HttpMethod.Post, "api/v1/datasets/" + Uri.EscapeDataString(id) + "/" + action, revision, ct, key);
    }
}
