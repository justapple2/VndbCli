using System.Security.Cryptography;
using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

public sealed record PresenceOperation(string Origin, string ServiceInstanceId, string DeviceId, string BatchId, string PayloadSha256);
public sealed record PresenceConflict(PresenceOperation Operation, DateTimeOffset RejectedAt, string Reason = "revision_or_removal");
public sealed record PresenceState(string Status, string? BatchId, int Items, string Message);
public sealed class PresenceConflictException(string? batchId = null, int items = 0) : InvalidOperationException("存在标记已被Web明确拒绝；原稿保留，请预览并确认重新核对。")
{ public PresenceState State { get; } = new("conflict", batchId, items, "原稿保留；请使用 presence recheck 预览重新核对。"); }

/// <summary>Uncertain requests keep their ID; only typed rejections can be replaced.</summary>
public sealed partial class PresenceSync(LocalLibraryStore local)
{
    private sealed record SavedReceipt(string Origin, PresenceBatch Batch, PresenceReceipt Receipt);
    private string Pending => Path.Combine(local.DirectoryPath, "presence-pending.json");
    private string FileName(string folder, string id)
    {
        if (!Guid.TryParseExact(id, "D", out _)) throw new InvalidDataException("存在批次ID无效，原件保留。");
        return Path.Combine(local.DirectoryPath, folder, id + ".json");
    }
    private static async Task<byte[]> BytesAsync(string path, CancellationToken ct)
    {
        LocalFileLease.RequireOrdinaryAncestors(path);
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("存在标记不是普通JSON或过大。");
        return await File.ReadAllBytesAsync(path, ct);
    }
    private static async Task<T> ReadAsync<T>(string path, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await BytesAsync(path, ct), VndbJson.Options) ?? throw new InvalidDataException("存在标记JSON无效，原件保留。");
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static PresenceOperation Operation(PresenceBatch batch, string origin) => new(origin, batch.ServiceInstanceId, batch.DeviceId, batch.Id, LocalArchiveFormat.Hash(batch));
    private void Validate(PresenceBatch batch, string origin, string deviceId, string instance)
    {
        _ = FileName("presence-operations", batch.Id);
        if (batch.Format != "vn-library-presence" || batch.FormatVersion != 1 || batch.DeviceId != deviceId || string.IsNullOrWhiteSpace(instance) || batch.ServiceInstanceId != instance || batch.ExpectedRevision < 0 ||
            batch.Items is null || batch.Items.Count is < 1 or > 2000 || batch.Items.Any(i => i is null || string.IsNullOrWhiteSpace(i.ItemId)) || batch.Items.Select(i => i.ItemId).Distinct().Count() != batch.Items.Count)
            throw new InvalidDataException("存在批次的实例、设备或内容不符，原件保留。");
        _ = ClientSettings.ServerUri(origin);
    }
    public async Task<PresenceState> StateAsync(string origin, string deviceId, CancellationToken ct = default)
    {
        if (!File.Exists(Pending)) return new("idle", null, 0, "没有待提交的存在标记；同步时会核对当前观测。");
        var batch = await ReadAsync<PresenceBatch>(Pending, ct); var expected = Operation(batch, ClientSettings.ServerUri(origin).AbsoluteUri);
        Validate(batch, origin, deviceId, batch.ServiceInstanceId);
        if (batch.DeviceId != deviceId) throw new InvalidDataException("待同步存在标记属于其他设备。");
        var binding = FileName("presence-operations", batch.Id);
        if (File.Exists(binding) && await ReadAsync<PresenceOperation>(binding, ct) != expected) throw new InvalidDataException("存在标记绑定其他服务或原稿变化。");
        var rejected = FileName("presence-conflicts", batch.Id);
        if (File.Exists(rejected))
        {
            if ((await ReadAsync<PresenceConflict>(rejected, ct)).Operation != expected) throw new InvalidDataException("拒绝记录与原稿不符。");
            return new("conflict", batch.Id, batch.Items.Count, "Web已明确拒绝，原稿保留；请先预览重新核对。");
        }
        return new(File.Exists(binding) ? "uncertain" : "pending", batch.Id, batch.Items.Count,
            File.Exists(binding) ? "结果尚未确认，请按原批次重试；不能替换原稿。" : "存在标记待提交，原稿保留。");
    }
    public async Task<int> SubmitAsync(ISyncApi web, string origin, string deviceId, CancellationToken ct = default)
    {
        origin = ClientSettings.ServerUri(origin).AbsoluteUri;
        using var stream = await LocalFileLease.AcquireAsync(local.DirectoryPath, "presence-stream/" + deviceId, ct);
        await ResumeRecoveryAsync(origin, deviceId, ct);
        var info = await web.OrganizationInfoAsync(ct);
        if (string.IsNullOrWhiteSpace(info.ServiceInstanceId) || !info.Capabilities.Contains("boolean_presence")) throw new ClientApiException("privacy_boundary_upgrade_required", 200);
        PresenceBatch batch;
        if (File.Exists(Pending)) batch = await ReadAsync<PresenceBatch>(Pending, ct);
        else
        {
            var head = await web.PresenceHeadAsync(deviceId, ct); var library = await local.ReadSummaryAsync(ct);
            var observed = Observe(library, deviceId, head); if (Unchanged(observed, head)) return 0;
            var authority = await AuthorityAsync(web, ct);
            var flags = observed.Where(i => authority.Active.Contains(i.ItemId)).ToArray();
            if (Unchanged(flags, head)) return 0;
            batch = new(Guid.NewGuid().ToString("D"), info.ServiceInstanceId, deviceId, head.Revision, flags);
            using (await LocalFileLease.AcquireAsync(local.DirectoryPath, "presence-state", ct))
                await JsonSnapshotStore.AtomicWriteAsync(Pending, batch, false, ct);
        }
        Validate(batch, origin, deviceId, info.ServiceInstanceId);
        var operation = Operation(batch, origin); var binding = FileName("presence-operations", batch.Id);
        if (File.Exists(binding)) { if (await ReadAsync<PresenceOperation>(binding, ct) != operation) throw new InvalidDataException("存在标记服务绑定或内容不符，原件保留。"); }
        else await JsonSnapshotStore.AtomicWriteAsync(binding, operation, false, ct);
        var conflict = FileName("presence-conflicts", batch.Id);
        if (File.Exists(conflict))
        {
            if ((await ReadAsync<PresenceConflict>(conflict, ct)).Operation != operation) throw new InvalidDataException("冲突记录不符。");
            throw new PresenceConflictException(batch.Id, batch.Items.Count);
        }
        var completed = FileName("presence-receipts", batch.Id); PresenceReceipt receipt;
        if (File.Exists(completed))
        {
            var prior = await ReadAsync<SavedReceipt>(completed, ct);
            if (prior.Origin != origin || LocalArchiveFormat.Hash(prior.Batch) != operation.PayloadSha256) throw new InvalidDataException("原存在标记回执不匹配。");
            receipt = prior.Receipt;
        }
        else
        {
            try { receipt = await web.SubmitPresenceAsync(batch, ct); }
            catch (ClientApiException error) when (error.Status == 409 && error.Code == "conflict")
            {
                await RequirePendingAsync(batch, ct);
                await JsonSnapshotStore.AtomicWriteAsync(conflict, new PresenceConflict(operation, DateTimeOffset.UtcNow), false, ct);
                throw new PresenceConflictException(batch.Id, batch.Items.Count);
            }
        }
        if (receipt.Id != batch.Id || receipt.ServiceInstanceId != info.ServiceInstanceId || !LocalArchiveFormat.MatchesHash(batch, receipt.PayloadSha256) || receipt.Revision != batch.ExpectedRevision + 1)
            throw new InvalidDataException("本地标记回执不匹配，保留待同步JSON。");
        using (await LocalFileLease.AcquireAsync(local.DirectoryPath, "presence-state", ct))
        {
            await RequirePendingAsync(batch, ct);
            if (!File.Exists(completed)) await JsonSnapshotStore.AtomicWriteAsync(completed, new SavedReceipt(origin, batch, receipt), false, ct);
            ArchivePending(FileName("presence-sent", batch.Id));
        }
        return batch.Items.Count;
    }
    private async Task RequirePendingAsync(PresenceBatch batch, CancellationToken ct)
    {
        if (!File.Exists(Pending) || LocalArchiveFormat.Hash(await ReadAsync<PresenceBatch>(Pending, ct)) != LocalArchiveFormat.Hash(batch))
            throw new InvalidDataException("提交期间存在标记变化，未确认原操作。");
    }
    private void ArchivePending(string target)
    {
        LocalFileLease.RequireOrdinaryAncestors(target); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(Pending, File.Exists(target) ? target + ".reconfirmed-" + Guid.NewGuid().ToString("N") + ".json" : target);
    }
}

public static class PresenceSyncExtensions
{
    public static Task<int> SyncPresenceAsync(this LocalLibraryStore local, ISyncApi web, string origin, string deviceId, CancellationToken ct = default) =>
        new PresenceSync(local).SubmitAsync(web, origin, deviceId, ct);
}
