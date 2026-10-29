namespace Vndb.Core;

public interface IClientSyncLanes
{
    Task<int> MetadataAsync(CancellationToken ct);
    Task<bool> ActivitiesAsync(CancellationToken ct);
    Task<int> PersonalAsync(CancellationToken ct);
    Task<int> PresenceAsync(CancellationToken ct);
}
public sealed record ClientSyncResult(int MetadataCount, bool ActivitiesComplete, int PersonalPending, int PresenceCount);
public sealed class ClientSyncCoordinator(IClientSyncLanes lanes)
{
    public async Task<ClientSyncResult> SyncAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var metadata = await lanes.MetadataAsync(ct);
        var activity = await lanes.ActivitiesAsync(ct);
        var personal = await lanes.PersonalAsync(ct);
        var presence = await lanes.PresenceAsync(ct);
        return new(metadata, activity, personal, presence);
    }
}
public static class SyncRules
{
    public static void RequireBinding(string expectedOrigin, string expectedInstance, string origin, string instance)
    {
        if (string.IsNullOrWhiteSpace(instance) || expectedOrigin != origin || expectedInstance != instance)
            throw new InvalidDataException("待同步内容绑定其他服务地址或实例，保留原件并明确核对。");
    }
    public static bool AcceptCheckpoint(long previousSequence, bool previousEnded, long sequence, bool ended)
    {
        if (sequence < previousSequence) return false;
        if (previousEnded && !ended) throw new InvalidDataException("已结束会话不能被后续检查点重新打开。");
        return true;
    }
}
