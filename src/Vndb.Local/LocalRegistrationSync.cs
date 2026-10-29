using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

public sealed record LocalRegistrationIntent(string? Origin, ActivityBatch Batch);
public sealed record LocalRegistrationReceipt(ActivityBinding Binding, ActivityReceipt Receipt);

/// <summary>Public item seeds only; installations and paths never enter this journal's wire batches.</summary>
public sealed class LocalRegistrationSync(LocalLibraryStore library)
{
    private string FileName(string id, string name)
    {
        if (!LocalInstallationRegistration.IsPending(id) || !Guid.TryParseExact(id[LocalInstallationRegistration.PendingPrefix.Length..], "D", out _)) throw new ArgumentException("本机待登记条目ID无效。");
        return Path.Combine(library.DirectoryPath, "local-registrations", id, name + ".json");
    }
    private static async Task<T> ReadAsync<T>(string file, CancellationToken ct)
    {
        LocalFileLease.RequireOrdinaryAncestors(file);
        if (new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidDataException("登记JSON超过8MiB。");
        return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(file, ct), VndbJson.Options) ?? throw new InvalidDataException("登记JSON损坏，原件保留。");
    }
    private async Task<LocalRegistrationIntent> IntentAsync(string itemId, CancellationToken ct)
    {
        var intent = await ReadAsync<LocalRegistrationIntent>(FileName(itemId, "intent"), ct); var batch = intent.Batch;
        if (batch?.Items is null || batch.Metadata is null || batch.Installations is null || batch.Plays is null || batch.PlaySessions is null || batch.PlayEdits is null || batch.Device is null ||
            batch.Items.Any(i => i?.Details is null) || batch.Metadata.Any(m => m is null)) throw new InvalidDataException("登记原稿结构无效，原件保留。");
        return intent;
    }
    public async Task PrepareAsync(LibraryItemView item, string? origin, ClientDeviceIdentity device, CancellationToken ct)
    {
        var file = FileName(item.Item.Id, "intent");
        if (File.Exists(file))
        {
            var previous = await IntentAsync(item.Item.Id, ct);
            if (previous.Batch.DeviceId != device.Id || previous.Batch.Items.Count != 1 || previous.Batch.Items[0].Details != item.Item.Details) throw new InvalidDataException("已有登记原稿不符，保留原件。");
            return;
        }
        var batch = new ActivityBatch { BatchId = Guid.NewGuid().ToString("D"), DeviceId = device.Id, Device = new("本机客户端", device.Platform),
            Items = [new(item.Item.Id, item.Item.Details)], Metadata = item.Metadata is null ? [] : [item.Metadata] };
        if (WirePrivacy.ContainsPrivateData(JsonSerializer.SerializeToElement(batch, VndbJson.Options))) throw new ArgumentException("登记公开资料含本机路径，未提交。");
        await JsonSnapshotStore.AtomicWriteAsync(file, new LocalRegistrationIntent(origin, batch), false, ct);
    }
    public async Task<ActivityBatch> ExportAsync(string itemId, CancellationToken ct = default)
    {
        var batch = (await IntentAsync(itemId, ct)).Batch;
        if (batch.Installations.Count != 0 || batch.Plays.Count != 0 || batch.PlayEdits.Count != 0 || batch.PlaySessions.Count != 0 ||
            WirePrivacy.ContainsPrivateData(JsonSerializer.SerializeToElement(batch, VndbJson.Options))) throw new InvalidDataException("登记导出包含非公开条目数据，原件保留。");
        return batch;
    }
    public async Task<int> ExportToDirectoryAsync(IEnumerable<string> itemIds, string directory, CancellationToken ct)
    {
        var root = LocalFiles.Full(directory); LocalFiles.CheckAncestors(root);
        var local = await library.ReadAsync(ct);
        if (local.Installations.Any(i => LocalFiles.Same(i.Installation.Details.RootPath, root) || LocalFiles.Within(i.Installation.Details.RootPath, root)))
            throw new ArgumentException("公开登记资料不能导出到任何已登记游戏目录中。");
        var count = 0;
        foreach (var id in itemIds.Distinct(StringComparer.Ordinal))
        { await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(root, id + ".json"), await ExportAsync(id, ct), false, ct); count++; }
        return count;
    }
    private static void ValidateIntent(LocalRegistrationIntent intent, string itemId, string origin, string? deviceId = null, LibraryItemView? original = null)
    {
        var batch = intent.Batch;
        if (batch.Format != "vn-library-activity" || batch.FormatVersion != 1 || batch.Kind != "play_activity" || !Guid.TryParseExact(batch.BatchId, "D", out _) ||
            batch.Installations.Count != 0 || batch.Plays.Count != 0 || batch.PlaySessions.Count != 0 || batch.PlayEdits.Count != 0 ||
            WirePrivacy.ContainsPrivateData(JsonSerializer.SerializeToElement(batch, VndbJson.Options)) || string.IsNullOrWhiteSpace(batch.DeviceId) ||
            deviceId is not null && batch.DeviceId != deviceId || intent.Origin is not null && intent.Origin != origin || batch.Items.Count != 1 ||
            batch.Items[0].ClientId != itemId || original is not null && (original.Item.Id != itemId || batch.Items[0].Details != original.Item.Details))
            throw new InvalidDataException("登记原稿的设备、服务或条目不符；保留原件。");
    }
    private static ActivityResult ValidateReceipt(LocalRegistrationIntent intent, LocalRegistrationReceipt result, ActivityBinding binding, string itemId)
    {
        var receipt = result.Receipt;
        if (result.Binding != binding || string.IsNullOrWhiteSpace(binding.ServiceInstanceId) || receipt is null || receipt.ServiceInstanceId != binding.ServiceInstanceId ||
            receipt.BatchId != intent.Batch.BatchId || !LocalArchiveFormat.MatchesHash(intent.Batch, receipt.PayloadSha256) || !receipt.Complete || receipt.Results is null ||
            receipt.Results.Count != 1 || receipt.Results[0] is not { Kind: "items", Status: "applied" or "duplicate", ServerId: { Length: > 0 } } row || row.Key != itemId || LocalInstallationRegistration.IsPending(row.ServerId))
            throw new ClientApiException("registration_receipt_invalid", 200);
        return row;
    }
    public async Task<string?> MappedItemAsync(string itemId, string? origin, CancellationToken ct, LibraryItemView? original = null)
    {
        using var operation = await LocalFileLease.AcquireAsync(library.DirectoryPath, "registration-sync/" + itemId, ct);
        var file = FileName(itemId, "receipt"); if (!File.Exists(file)) return null;
        if (origin is null || origin != ClientSettings.ServerUri(origin).AbsoluteUri) throw new InvalidDataException("登记映射缺少明确的服务地址。");
        var intent = await IntentAsync(itemId, ct); ValidateIntent(intent, itemId, origin, original: original);
        var binding = await ReadAsync<ActivityBinding>(FileName(itemId, "binding"), ct);
        if (binding.Origin != origin) throw new InvalidDataException("登记映射服务不符，原件保留。");
        var value = await ReadAsync<LocalRegistrationReceipt>(file, ct);
        var row = ValidateReceipt(intent, value, binding, itemId); var catalog = await library.ReadAsync(ct);
        var target = catalog.Items.SingleOrDefault(i => i.Item.Id == row.ServerId); var seed = intent.Batch.Items[0].Details;
        if (target is null || target.Item.DeletedAt is not null || target.Item.Details.NovelId != seed.NovelId ||
            seed.NovelId is null && (target.Item.Details.Title != seed.Title || target.Item.Details.CompanyHint != seed.CompanyHint) ||
            !OwnedPackages.References(catalog, intent.Batch.DeviceId, row.ServerId!))
            throw new InvalidDataException("登记映射目标缺失或关联变化，原书架保留。");
        return row.ServerId;
    }
    public async Task<int> SyncAsync(ISyncApi api, string origin, string deviceId, CancellationToken ct = default)
    {
        var local = await library.ReadAsync(ct); var pending = local.Items.Where(i => LocalInstallationRegistration.IsPending(i.Item.Id) &&
            OwnedPackages.References(local, deviceId, i.Item.Id)).ToArray();
        if (pending.Length == 0) return 0;
        origin = ClientSettings.ServerUri(origin).AbsoluteUri;
        var info = await api.OrganizationInfoAsync(ct); var binding = new ActivityBinding(origin, info.ServiceInstanceId);
        if (string.IsNullOrWhiteSpace(info.ServiceInstanceId) || !info.Capabilities.Contains("activity_import")) throw new ClientApiException("activity_import_unavailable", 200);
        var completed = 0;
        foreach (var item in pending)
        {
            using var operation = await LocalFileLease.AcquireAsync(library.DirectoryPath, "registration-sync/" + item.Item.Id, ct);
            var intent = await IntentAsync(item.Item.Id, ct);
            ValidateIntent(intent, item.Item.Id, origin, deviceId, item);
            var bind = FileName(item.Item.Id, "binding");
            if (File.Exists(bind)) { var previous = await ReadAsync<ActivityBinding>(bind, ct); SyncRules.RequireBinding(previous.Origin, previous.ServiceInstanceId, origin, info.ServiceInstanceId); }
            else await JsonSnapshotStore.AtomicWriteAsync(bind, binding, false, ct);
            var file = FileName(item.Item.Id, "receipt");
            if (!File.Exists(file)) ImportValidation.Activity(intent.Batch, info.ImportLimits ?? new());
            var result = File.Exists(file) ? await ReadAsync<LocalRegistrationReceipt>(file, ct) : new(binding, await api.SubmitActivityAsync(intent.Batch, ct));
            var row = ValidateReceipt(intent, result, binding, item.Item.Id);
            if (LocalArchiveFormat.Hash(await IntentAsync(item.Item.Id, ct)) != LocalArchiveFormat.Hash(intent))
                throw new InvalidDataException("提交期间登记原稿发生变化，未确认本机操作。");
            if (!File.Exists(file)) await JsonSnapshotStore.AtomicWriteAsync(file, result, false, ct);
            var saved = await api.ItemAsync(row.ServerId!, ct);
            if (saved.Item.Id != row.ServerId || saved.Item.DeletedAt is not null || saved.Item.Details.NovelId != item.Item.Details.NovelId) throw new InvalidDataException("Web条目已移除或关联变化；原稿及回执保留。");
            await library.UpdateAsync(current =>
            {
                var before = current.Items.SingleOrDefault(i => i.Item.Id == item.Item.Id);
                if (before is null)
                {
                    if (current.Items.Any(i => i.Item.Id == saved.Item.Id && i.Item.Details.NovelId == saved.Item.Details.NovelId)) return current;
                    throw new InvalidDataException("提交期间本机登记被移除，原稿及回执保留。");
                }
                if (LocalArchiveFormat.Hash(before) != LocalArchiveFormat.Hash(item)) throw new InvalidDataException("同步期间登记资料变化，未确认本机关联。");
                if (current.Items.Any(i => i.Item.Id == saved.Item.Id && i.Item.Details.NovelId != saved.Item.Details.NovelId)) throw new InvalidDataException("本机已有不同作品关联，未覆盖。");
                if (!OwnedPackages.References(current, deviceId, item.Item.Id)) throw new InvalidDataException("登记期间本机资源关联已变化，原稿及回执保留。");
                return current with { Items = current.Items.Where(i => i.Item.Id != item.Item.Id && i.Item.Id != saved.Item.Id).Append(saved).ToArray(),
                    Packages = current.Packages?.Select(p => p.ItemId != item.Item.Id ? p : p with { ItemId = saved.Item.Id, Revision = p.Revision + 1 }).ToArray(),
                    Installations = current.Installations.Select(v => !v.Components.Any(c => c.Details.ItemId == item.Item.Id) ? v : v with {
                        // The receipt proves an alias of the same work. No physical-container fields or
                        // component identities/paths change; keep approved moves and restore receipts valid.
                        Components = v.Components.Select(c => c.Details.ItemId != item.Item.Id ? c : c with { Details = c.Details with { ItemId = saved.Item.Id }, Revision = c.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow }).ToArray() }).ToArray() };
            }, ct);
            completed++;
        }
        return completed;
    }
}
