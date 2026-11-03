using Vndb.Core;
using Vndb.Contracts;

namespace Vndb.Client;

public sealed partial class WebLibraryClient
{
    public Task<PersonalOrganizationState?> PersonalOrganizationAsync(CancellationToken ct = default) =>
        SendAsync<PersonalOrganizationState?>(HttpMethod.Get, "api/v1/personal-organization", null, ct);
    public async Task<PersonalOrganizationState> SavePersonalOrganizationAsync(PersonalOrganizationChange change, string key, CancellationToken ct = default, string? serviceInstanceId = null)
    {
        serviceInstanceId ??= (await OrganizationInfoAsync(ct)).ServiceInstanceId;
        return await SendAsync<PersonalOrganizationState>(HttpMethod.Post, "api/v1/personal-organization", new PersonalOrganizationWrite(serviceInstanceId, change), ct, key);
    }
}
