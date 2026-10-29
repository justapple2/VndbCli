using Vndb.Contracts;

namespace Vndb.Client;

public sealed partial class WebLibraryClient
{
    public Task<PresenceHead> PresenceHeadAsync(string deviceId, CancellationToken ct = default) =>
        SendAsync<PresenceHead>(HttpMethod.Get, "api/v1/presence/devices/" + Uri.EscapeDataString(deviceId), null, ct);
    public Task<PresenceReceipt> SubmitPresenceAsync(PresenceBatch batch, CancellationToken ct = default) =>
        SendAsync<PresenceReceipt>(HttpMethod.Post, "api/v1/presence", batch, ct, "presence-" + batch.Id);
}
