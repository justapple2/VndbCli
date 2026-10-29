using Vndb.Contracts;

namespace Vndb.Client;

public sealed partial class WebLibraryClient
{
    public Task<IReadOnlyList<LibraryDevice>> DevicesAsync(CancellationToken ct = default) => SendAsync<IReadOnlyList<LibraryDevice>>(HttpMethod.Get, "api/v1/devices", null, ct);
    public Task<LibraryDevice> RegisterDeviceAsync(DeviceWrite write, string operationKey, CancellationToken ct = default) => SendAsync<LibraryDevice>(HttpMethod.Post, "api/v1/devices", write, ct, operationKey);
    public Task<LibraryDevice> DeviceSeenAsync(string id, long revision, string operationKey, CancellationToken ct = default) => SendAsync<LibraryDevice>(HttpMethod.Post, "api/v1/devices/" + Uri.EscapeDataString(id) + "/seen", new RevisionRequest(revision), ct, operationKey);
    public Task<LibraryDevice> ClaimDeviceAsync(string legacyId, LegacyDeviceClaim claim, string operationKey, CancellationToken ct = default) => SendAsync<LibraryDevice>(HttpMethod.Post, "api/v1/devices/" + Uri.EscapeDataString(legacyId) + "/claim", claim, ct, operationKey);
    public Task<InstallationPage> InstallationsAsync(string? deviceId = null, int page = 1, CancellationToken ct = default) => SendAsync<InstallationPage>(HttpMethod.Get,
        "api/v1/installations?page=" + page + (deviceId is null ? "" : "&device_id=" + Uri.EscapeDataString(deviceId)), null, ct);
}
