using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

public sealed record PresenceRecheckPreview(string Origin, string DeviceId, string ServiceInstanceId, string RejectedBatchId,
    string OriginalSha256, string ConflictSha256, string LibrarySha256, string AuthoritySha256, long HeadRevision,
    int ExcludedItems, PresenceBatch? Replacement);
public sealed record PresenceRecoveryIntent(PresenceRecheckPreview Preview, string ApprovedSha256);

public sealed partial class PresenceSync
{
    private sealed record Authority(HashSet<string> Active, string Sha256);
    private sealed record ItemVersion(string Id, long Revision, bool Deleted);
    private static bool Unchanged(IReadOnlyList<PresenceFlag> flags, PresenceHead head) => flags.Count == 0 || flags.All(i => head.Items.Any(p => p.ItemId == i.ItemId && p.IsLocal == i.IsLocal));
    private static IReadOnlyList<PresenceFlag> Observe(LocalLibrary library, string deviceId, PresenceHead head)
    {
        if (head.DeviceId != deviceId || head.Revision < 0 || head.Items is null || head.Items.Any(i => i is null) || head.Items.Select(i => i.ItemId).Distinct().Count() != head.Items.Count)
            throw new InvalidDataException("Web存在标记读取无效。");
        var previous = head.Items.ToDictionary(i => i.ItemId, i => i.IsLocal);
        var installed = library.Installations.Where(i => i.Installation.Details.DeviceId == deviceId)
            .SelectMany(i => i.Components.Where(c => !LocalInstallationRegistration.IsPending(c.Details.ItemId)).Select(c => new {
                c.Details.ItemId, Present = i.Installation.RemovedAt is null && i.Installation.Status == "present",
                Unknown = i.Installation.RemovedAt is null && i.Installation.Status is not ("present" or "missing") }));
        var packages = (library.Packages ?? []).Where(p => p.DeviceId == deviceId && !LocalInstallationRegistration.IsPending(p.ItemId))
            .Select(p => new { p.ItemId, Present = p.Status == "present", Unknown = p.Status == "unknown" });
        return installed.Concat(packages)
            .GroupBy(i => i.ItemId).Where(g => g.Any(i => i.Present) || g.All(i => !i.Unknown) || previous.ContainsKey(g.Key))
            .Select(g => new PresenceFlag(g.Key, g.Any(i => i.Present) || g.Any(i => i.Unknown) && previous.GetValueOrDefault(g.Key)))
            .OrderBy(i => i.ItemId, StringComparer.Ordinal).ToArray();
    }
    private static async Task<Authority> AuthorityAsync(ISyncApi web, CancellationToken ct)
    {
        var active = new HashSet<string>(StringComparer.Ordinal); var removed = new HashSet<string>(StringComparer.Ordinal); var versions = new List<ItemVersion>(); bool? light = null;
        foreach (var deleted in new[] { false, true })
        {
            var target = deleted ? removed : active;
            int? total = null; int? size = null;
            for (var page = 1; ; page++)
            {
                var heads = await web.ItemHeadsAsync(page, deleted, ct);
                if (page == 1 && !deleted) light = heads is not null;
                if (light != (heads is not null)) throw new InvalidDataException("条目头读取能力发生变化，请重新核对。");
                if (heads is not null)
                {
                    total ??= heads.Total; size ??= heads.PageSize;
                    if (total != heads.Total || size != heads.PageSize) throw new InvalidDataException("Web条目头分页期间条目总数变化，请重新核对。");
                    if (heads.Page != page || heads.PageSize is < 1 or > 100 || heads.Total < 0 || heads.Items is null ||
                        heads.More != ((long)page * heads.PageSize < heads.Total) || heads.Items.Count != Math.Min(heads.PageSize, Math.Max(0, heads.Total - (long)(page - 1) * heads.PageSize)) ||
                        heads.Items.Any(i => i is null || string.IsNullOrWhiteSpace(i.Id) || i.Revision < 1 || i.Deleted != deleted || !target.Add(i.Id)))
                        throw new InvalidDataException("Web条目头分页无效。");
                    versions.AddRange(heads.Items.Select(i => new ItemVersion(i.Id, i.Revision, deleted)));
                    if (!heads.More) break;
                }
                else
                {
                    var result = deleted ? await web.RemovedItemsAsync(page, ct) : await web.ItemsAsync(page, ct);
                    if (result.Page != page || result.PageSize is < 1 or > 100 || result.Items is null || result.Items.Count > result.PageSize ||
                        result.Items.Any(i => i?.Item is null || (i.Item.DeletedAt is not null) != deleted || !target.Add(i.Item.Id))) throw new InvalidDataException("Web条目分页或移除状态无效。");
                    versions.AddRange(result.Items.Select(i => new ItemVersion(i.Item.Id, i.Item.Revision, deleted)));
                    if (!result.More) break;
                }
                if (page >= 100) throw new InvalidDataException("条目分页超过核对范围，请缩小资料库。");
            }
        }
        if (active.Overlaps(removed)) throw new InvalidDataException("读取期间条目移除状态变化，请重新核对。");
        return new(active, LocalArchiveFormat.Hash(versions.OrderBy(i => i.Id, StringComparer.Ordinal).ToArray()));
    }
    public async Task<PresenceRecheckPreview> PreviewRecheckAsync(ISyncApi web, string origin, string deviceId, CancellationToken ct = default)
    {
        origin = ClientSettings.ServerUri(origin).AbsoluteUri;
        using var stream = await LocalFileLease.AcquireAsync(local.DirectoryPath, "presence-stream/" + deviceId, ct);
        await ResumeRecoveryAsync(origin, deviceId, ct);
        var state = await StateAsync(origin, deviceId, ct);
        if (state.Status != "conflict" || state.BatchId is null) throw new InvalidOperationException("仅允许替代已明确拒绝的批次；结果不明时请按原ID重试。");
        var bytes = await BytesAsync(Pending, ct); var batch = await ReadAsync<PresenceBatch>(Pending, ct);
        var info = await web.OrganizationInfoAsync(ct); Validate(batch, origin, deviceId, info.ServiceInstanceId);
        if (File.Exists(FileName("presence-receipts", batch.Id))) throw new InvalidDataException("同一批次存在成功回执，不能替代，请先核对。");
        var head = await web.PresenceHeadAsync(deviceId, ct); var authority = await AuthorityAsync(web, ct); var library = await local.ReadSummaryAsync(ct);
        var observations = Observe(library, deviceId, head); var flags = observations.Where(i => authority.Active.Contains(i.ItemId)).ToArray();
        return new(origin, deviceId, info.ServiceInstanceId, batch.Id, Sha(bytes),
            LocalArchiveFormat.Hash(await ReadAsync<PresenceConflict>(FileName("presence-conflicts", batch.Id), ct)), LocalArchiveFormat.Hash(library), authority.Sha256, head.Revision,
            observations.Count - flags.Length, Unchanged(flags, head) ? null : new(Guid.NewGuid().ToString("D"), info.ServiceInstanceId, deviceId, head.Revision, flags));
    }
    public async Task ApplyRecheckAsync(ISyncApi web, PresenceRecheckPreview preview, string approvedSha256, CancellationToken ct = default)
    {
        if (LocalArchiveFormat.Hash(preview) != approvedSha256) throw new ArgumentException("重新核对的批准摘要不符。");
        using var stream = await LocalFileLease.AcquireAsync(local.DirectoryPath, "presence-stream/" + preview.DeviceId, ct);
        var state = await StateAsync(preview.Origin, preview.DeviceId, ct);
        var info = await web.OrganizationInfoAsync(ct); var head = await web.PresenceHeadAsync(preview.DeviceId, ct); var authority = await AuthorityAsync(web, ct);
        if (state.Status != "conflict" || state.BatchId != preview.RejectedBatchId || info.ServiceInstanceId != preview.ServiceInstanceId || head.Revision != preview.HeadRevision || authority.Sha256 != preview.AuthoritySha256 ||
            LocalArchiveFormat.Hash(await local.ReadSummaryAsync(ct)) != preview.LibrarySha256 || Sha(await BytesAsync(Pending, ct)) != preview.OriginalSha256 ||
            LocalArchiveFormat.Hash(await ReadAsync<PresenceConflict>(FileName("presence-conflicts", preview.RejectedBatchId), ct)) != preview.ConflictSha256)
            throw new InvalidDataException("预览后服务、条目、原稿或本机观测变化，请重新核对。");
        var file = FileName("presence-recovery", preview.RejectedBatchId);
        if (File.Exists(file)) throw new InvalidDataException("已有恢复意图，请先续跑。");
        await JsonSnapshotStore.AtomicWriteAsync(file, new PresenceRecoveryIntent(preview, approvedSha256), false, ct);
        await ResumeRecoveryAsync(preview.Origin, preview.DeviceId, ct);
    }
    private async Task ResumeRecoveryAsync(string origin, string deviceId, CancellationToken ct)
    {
        var directory = Path.Combine(local.DirectoryPath, "presence-recovery"); if (!Directory.Exists(directory)) return;
        LocalFileLease.RequireOrdinaryAncestors(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(file); var done = FileName("presence-recovered", id); if (File.Exists(done)) continue;
            var intent = await ReadAsync<PresenceRecoveryIntent>(file, ct); var preview = intent.Preview;
            if (preview.RejectedBatchId != id || LocalArchiveFormat.Hash(preview) != intent.ApprovedSha256 || preview.Origin != origin || preview.DeviceId != deviceId)
                throw new InvalidDataException("恢复意图绑定其他服务或设备，原件保留。");
            if (preview.Replacement is { } replacement) Validate(replacement, origin, deviceId, preview.ServiceInstanceId);
            var rejection = await ReadAsync<PresenceConflict>(FileName("presence-conflicts", id), ct);
            if (LocalArchiveFormat.Hash(rejection) != preview.ConflictSha256 || rejection.Operation.Origin != origin || rejection.Operation.DeviceId != deviceId ||
                rejection.Operation.ServiceInstanceId != preview.ServiceInstanceId || rejection.Operation.BatchId != id || File.Exists(FileName("presence-receipts", id)))
                throw new InvalidDataException("恢复意图缺少匹配的明确拒绝记录，未替代原稿。");
            var archived = FileName("presence-rejected", id);
            using var state = await LocalFileLease.AcquireAsync(local.DirectoryPath, "presence-state", ct);
            if (File.Exists(Pending))
            {
                var current = await ReadAsync<PresenceBatch>(Pending, ct);
                if (current.Id == id)
                {
                    if (Sha(await BytesAsync(Pending, ct)) != preview.OriginalSha256) throw new InvalidDataException("恢复原稿变化，未归档。");
                    if (File.Exists(archived) && Sha(await BytesAsync(archived, ct)) != preview.OriginalSha256) throw new InvalidDataException("已有封存原件不同。");
                    ArchivePending(archived);
                }
                else if (preview.Replacement is null || LocalArchiveFormat.Hash(current) != LocalArchiveFormat.Hash(preview.Replacement)) throw new InvalidDataException("已有其他待同步意图，未覆盖。");
            }
            if (!File.Exists(archived) || Sha(await BytesAsync(archived, ct)) != preview.OriginalSha256) throw new InvalidDataException("拒绝原件未完整封存，未生成替代批次。");
            if (!File.Exists(Pending) && preview.Replacement is { } next) await JsonSnapshotStore.AtomicWriteAsync(Pending, next, false, ct);
            await JsonSnapshotStore.AtomicWriteAsync(done, intent, false, ct);
        }
    }
}
