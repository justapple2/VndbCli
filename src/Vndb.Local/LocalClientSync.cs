using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

/// <summary>Typed lanes share scheduling and binding rules, not payload or conflict semantics.</summary>
public sealed class LocalClientSync(ISyncApi web, string origin, string stateDirectory,
    ActivityOutbox activities, PersonalEditOutbox personal, LocalLibraryStore local, string? deviceId) : IClientSyncLanes
{
    public Task<ClientSyncResult> SyncAsync(CancellationToken ct = default) => new ClientSyncCoordinator(this).SyncAsync(ct);
    async Task<int> IClientSyncLanes.MetadataAsync(CancellationToken ct) => (await new PendingMetadataSync(web, origin, stateDirectory).SubmitAsync(ct)).Count;
    async Task<bool> IClientSyncLanes.ActivitiesAsync(CancellationToken ct)
    {
        if (deviceId is not null) await new LocalRegistrationSync(local).SyncAsync(web, origin, deviceId, ct);
        return (await activities.SyncAsync(web, origin, ct)).All(r => r.Complete);
    }
    async Task<int> IClientSyncLanes.PersonalAsync(CancellationToken ct)
    {
        await personal.SyncAsync(web, origin, ct);
        var remaining = await new PersonalOrganizationStore(stateDirectory, origin).SyncAsync(web, ct);
        return (await personal.PendingAsync(origin, ct)).Count + remaining;
    }
    Task<int> IClientSyncLanes.PresenceAsync(CancellationToken ct) => deviceId is null ? Task.FromResult(0) : new PresenceSync(local).SubmitAsync(web, origin, deviceId, ct);
}
