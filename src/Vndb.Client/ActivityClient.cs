using Vndb.Contracts;

namespace Vndb.Client;

public sealed partial class WebLibraryClient
{
    public async Task<ActivityReceipt> SubmitActivityAsync(ActivityBatch batch, CancellationToken ct = default)
    {
        var info = await OrganizationInfoAsync(ct);
        if (!info.Capabilities.Contains("activity_import")) throw new ClientApiException("activity_import_unavailable", 200);
        try { ImportValidation.Activity(batch, DiscoveredLimits(info)); }
        catch (ImportValidationException e) { throw new ClientApiException(e.Code, e.Status, e.Message); }
        return await SendAsync<ActivityReceipt>(HttpMethod.Post, "api/v1/activity/batches", batch, ct, "activity-" + batch.BatchId);
    }
    public Task<IReadOnlyList<PlaySession>> SessionsAsync(string? playId = null, CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<PlaySession>>(HttpMethod.Get, "api/v1/play-sessions" + (playId is null ? "" : "?play_id=" + Uri.EscapeDataString(playId)), null, ct);
    public Task<PlayTimeSummary> PlayTimeAsync(string id, CancellationToken ct = default) => SendAsync<PlayTimeSummary>(HttpMethod.Get, "api/v1/plays/" + Uri.EscapeDataString(id) + "/time", null, ct);
    public Task<IReadOnlyList<PlayEditConflict>> PlayEditConflictsAsync(CancellationToken ct = default) => SendAsync<IReadOnlyList<PlayEditConflict>>(HttpMethod.Get, "api/v1/play-edit-conflicts", null, ct);
    public Task<PlayEditConflict> ResolvePlayEditAsync(string id, PlayEditDecision decision, string key, CancellationToken ct = default) => SendAsync<PlayEditConflict>(HttpMethod.Post, "api/v1/play-edit-conflicts/" + Uri.EscapeDataString(id) + "/decision", decision, ct, key);
}
