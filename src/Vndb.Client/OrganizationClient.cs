using Vndb.Contracts;

namespace Vndb.Client;

public sealed partial class WebLibraryClient
{
    public Task<ServiceInfo> OrganizationInfoAsync(CancellationToken ct) => SendAsync<ServiceInfo>(HttpMethod.Get, "api/v1/info", null, ct);
    public Task<LibraryItemView> ItemAsync(string id, CancellationToken ct = default) => SendAsync<LibraryItemView>(HttpMethod.Get, "api/v1/items/" + Uri.EscapeDataString(id), null, ct);
}
