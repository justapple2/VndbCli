using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

public sealed record LocalPlayTarget(string Id, ActivityBatch Dependencies, string PlayRef, string Title,
    PersonalPlayData Details, long ExpectedRevision);
public sealed record ActivityBinding(string Origin, string ServiceInstanceId);
public sealed record ActivityAcknowledgement(string PayloadSha256, ActivityBinding Binding, ActivityReceipt Receipt);

/// <summary>Durable personal outbox, separate from metadata cache. Original operations are never evicted.</summary>
public sealed class ActivityOutbox(string directory)
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetFullPath(directory), "play-activity");
    public async Task<IReadOnlyList<LocalPlayTarget>> TargetsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        var results = new List<LocalPlayTarget>();
        foreach (var folder in Directory.EnumerateDirectories(DirectoryPath).Order(StringComparer.Ordinal))
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 || !Guid.TryParseExact(Path.GetFileName(folder), "D", out _)) continue;
            var file = Path.Combine(folder, "target.json");
            if (File.Exists(file)) { var target = await ReadAsync<LocalPlayTarget>(file, ct); RequireId(target.Id); if (target.Id != Path.GetFileName(folder)) throw new InvalidDataException("活动目录与ID不符。"); results.Add(target); }
        }
        return results;
    }
    public async Task<LocalPlayTarget> CreateTargetAsync(ClientDeviceIdentity device, string title, MetadataSnapshot? metadata,
        string? itemId = null, PersonalPlayView? play = null, CancellationToken ct = default, LibraryItemData? pendingItem = null)
    {
        itemId ??= play?.ItemId; ShelfPlayResolver.RequireSelection(itemId, play);
        var id = Guid.NewGuid().ToString("D"); var item = Guid.NewGuid().ToString("D"); var playId = Guid.NewGuid().ToString("D");
        var details = play?.Details ?? new(title, metadata?.VndbId, "playing", DateOnly.FromDateTime(DateTime.Now), null, 0, null, null, null, null, null);
        var pending = itemId is not null && LocalInstallationRegistration.IsPending(itemId);
        if (pending && (pendingItem is null || pendingItem.NovelId != metadata?.VndbId)) throw new ArgumentException("待登记作品需要原条目资料，不能猜测游玩关联。");
        var batch = new ActivityBatch { BatchId = id, DeviceId = device.Id, Device = new(device.Name, device.Platform),
            Metadata = play is null && (itemId is null || pending) && metadata is not null ? [metadata] : [],
            Items = play is null && pending ? [new(itemId!, pendingItem!)] : play is null && itemId is null ? [new(item, new(title, metadata?.VndbId))] : [],
            Plays = play is null ? [new(playId, pending ? "client:" + itemId : itemId ?? "client:" + item, details)] : [] };
        var target = new LocalPlayTarget(id, batch, play?.Id ?? "client:" + playId, title, details, play?.Revision ?? 1);
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(DirectoryPath, id, "target.json"), target, false, ct); return target;
    }
    public async Task SaveSessionAsync(string targetId, SessionSample sample, CancellationToken ct = default)
    {
        RequireId(targetId); RequireId(sample.Id);
        sample = sample with { InstallationId = null, ComponentId = null };
        using var lease = await LocalFileLease.AcquireAsync(Path.GetDirectoryName(DirectoryPath)!, "activity-target/" + targetId, ct);
        var file = Path.Combine(DirectoryPath, targetId, "play_sessions", sample.Id + ".json");
        if (File.Exists(file))
        {
            var prior = await ReadAsync<SessionSample>(file, ct);
            if (prior.DeviceId != sample.DeviceId || prior.PlayId != sample.PlayId || prior.StartedAt != sample.StartedAt)
                throw new InvalidDataException("会话身份或开始时间发生变化。");
            if (!SyncRules.AcceptCheckpoint(prior.Sequence, prior.EndedAt is not null, sample.Sequence, sample.EndedAt is not null)) return;
            if (prior.Sequence == sample.Sequence)
            { if (prior != sample) throw new InvalidDataException("同一会话序号对应不同检查点。"); return; }
            if (sample.CumulativeSeconds < prior.CumulativeSeconds) throw new InvalidDataException("累计时长不能倒退。");
        }
        await JsonSnapshotStore.AtomicWriteAsync(file, sample, true, ct);
    }
    public async Task<LocalPlayTarget> EditAsync(LocalPlayTarget target, string status, string? review, CancellationToken ct = default)
    {
        if (status is not ("not_started" or "playing" or "played" or "completed" or "on_hold" or "dropped"))
            throw new ArgumentException("无效的游玩状态；本机未保存该编辑。");
        using var lease = await LocalFileLease.AcquireAsync(Path.GetDirectoryName(DirectoryPath)!, "activity-target/" + target.Id, ct);

        {
            var current = await ReadAsync<LocalPlayTarget>(Path.Combine(DirectoryPath, target.Id, "target.json"), ct);
            // Recover an edit committed just before a crash interrupted target.json replacement.
            var prior = (await FilesAsync<PlayEditOperation>(Path.Combine(DirectoryPath, target.Id, "play_edits"), ct)).OrderBy(e => e.ExpectedRevision).LastOrDefault();
            if (prior is not null && prior.ExpectedRevision >= current.ExpectedRevision) current = current with { Details = prior.Details, ExpectedRevision = prior.ExpectedRevision + 1 };
            var details = current.Details with { Status = status, Review = review };
            PersonalRules.Validate(details.ToPersonal());
            if (status == "not_started" && (details.StartedOn is not null || details.FinishedOn is not null || details.DurationMinutes is > 0))
                throw new ArgumentException("未玩状态不能包含实际游玩日期或时长；本机未保存该编辑。");
            var operation = new PlayEditOperation(Guid.NewGuid().ToString("D"), current.PlayRef, current.ExpectedRevision, details);
            await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(DirectoryPath, target.Id, "play_edits", operation.Id + ".json"), operation, false, ct);
            var changed = current with { Details = details, ExpectedRevision = current.ExpectedRevision + 1 };
            await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(DirectoryPath, target.Id, "target.json"), changed, true, ct); return changed;
        }
    }
    public async Task<IReadOnlyList<ActivityBatch>> BatchesAsync(bool includeAcknowledged = false, CancellationToken ct = default)
    {
        var result = new List<ActivityBatch>();
        foreach (var initial in await TargetsAsync(ct))
        {
            using var lease = await LocalFileLease.AcquireAsync(Path.GetDirectoryName(DirectoryPath)!, "activity-target/" + initial.Id, ct);
            var folder = Path.Combine(DirectoryPath, initial.Id);
            var target = await ReadAsync<LocalPlayTarget>(Path.Combine(folder, "target.json"), ct);
            var samples = await FilesAsync<SessionSample>(Path.Combine(folder, "play_sessions"), ct);
            var edits = await FilesAsync<PlayEditOperation>(Path.Combine(folder, "play_edits"), ct);
            if (!includeAcknowledged)
            {
                var activeSamples = new List<SessionSample>();
                foreach (var sample in samples) if (!await AcknowledgedAsync("play_sessions", sample.Id + ":" + sample.Sequence, sample, ct)) activeSamples.Add(sample);
                samples = activeSamples;
                var activeEdits = new List<PlayEditOperation>();
                foreach (var edit in edits) if (!await AcknowledgedAsync("play_edits", edit.Id, edit, ct)) activeEdits.Add(edit);
                edits = activeEdits;
            }
            var operations = samples.OrderBy(s => s.StartedAt).ThenBy(s => s.Sequence).Select(s => (Sample: (SessionSample?)s, Edit: (PlayEditOperation?)null))
                .Concat(edits.OrderBy(e => e.ExpectedRevision).Select(e => (Sample: (SessionSample?)null, Edit: (PlayEditOperation?)e))).ToArray();
            foreach (var chunk in operations.Chunk(200))
            {
                var batch = target.Dependencies with { BatchId = Guid.Empty.ToString("D"), Device = new("本机客户端", target.Dependencies.Device.Platform), Installations = [], PlaySessions = chunk.Where(o => o.Sample is not null).Select(o => o.Sample! with { InstallationId = null, ComponentId = null }).ToArray(), PlayEdits = chunk.Where(o => o.Edit is not null).Select(o => o.Edit!).ToArray() };
                batch = batch with { BatchId = new Guid(Convert.FromHexString(LocalArchiveFormat.Hash(batch))[..16]).ToString("D") }; result.Add(batch);
            }
        }
        return result;
    }
    public async Task<IReadOnlyList<ActivityReceipt>> SyncAsync(ISyncApi web, string origin, CancellationToken ct = default)
    {
        if ((await BatchesAsync(ct: ct)).Count == 0) return [];
        origin = ClientSettings.ServerUri(origin).AbsoluteUri;
        var info = await web.OrganizationInfoAsync(ct);
        if (!info.Capabilities.Contains("activity_import")) throw new ClientApiException("activity_import_unavailable", 200);
        var binding = new ActivityBinding(origin, info.ServiceInstanceId); var file = Path.Combine(DirectoryPath, "binding.json");
        using (await LocalFileLease.AcquireAsync(Path.GetDirectoryName(DirectoryPath)!, "activity-binding", ct))
        {
            if (File.Exists(file))
            {
                var previous = await ReadAsync<ActivityBinding>(file, ct);
                SyncRules.RequireBinding(previous.Origin, previous.ServiceInstanceId, binding.Origin, binding.ServiceInstanceId);
            }
            else await JsonSnapshotStore.AtomicWriteAsync(file, binding, false, ct);
        }
        var receipts = new List<ActivityReceipt>();
        foreach (var batch in await BatchesAsync(ct: ct))
        {
            using var operation = await LocalFileLease.AcquireAsync(Path.GetDirectoryName(DirectoryPath)!, "activity-submit/" + batch.BatchId, ct);
            ImportValidation.Activity(batch, info.ImportLimits ?? new());
            if (WirePrivacy.ContainsPrivateData(JsonSerializer.SerializeToElement(batch, VndbJson.Options))) throw new InvalidDataException("活动批次包含本机私有信息，未提交。 ");
            var receipt = await web.SubmitActivityAsync(batch, ct);
            var expected = batch.Items.Select(i => (Kind: "items", Key: i.ClientId)).Concat(batch.Installations.Select(i => ("installations", i.ClientId)))
                .Concat(batch.Plays.Select(p => ("plays", p.ClientId))).Concat(batch.PlaySessions.Select(s => ("play_sessions", s.Id + ":" + s.Sequence)))
                .Concat(batch.PlayEdits.Select(e => ("play_edits", e.Id))).ToHashSet();
            if (receipt.BatchId != batch.BatchId || !LocalArchiveFormat.MatchesHash(batch, receipt.PayloadSha256) || receipt.ServiceInstanceId != binding.ServiceInstanceId || receipt.Results is null ||
                receipt.Results.Any(r => r is null || !expected.Contains((r.Kind, r.Key)) || r.Status is not ("applied" or "duplicate" or "dependency_pending" or "conflict")) ||
                receipt.Results.Select(r => (r.Kind, r.Key)).Distinct().Count() != expected.Count || receipt.Results.Count != expected.Count ||
                receipt.Complete != receipt.Results.All(r => r.Status is "applied" or "duplicate"))
                throw new ClientApiException("activity_receipt_invalid", 200);
            await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(DirectoryPath, "receipts", receipt.BatchId + ".json"), receipt, true, ct);
            foreach (var sample in batch.PlaySessions)
                if (receipt.Results.Any(r => r.Kind == "play_sessions" && r.Key == sample.Id + ":" + sample.Sequence && r.Status is "applied" or "duplicate"))
                    await AcknowledgeAsync("play_sessions", sample.Id + ":" + sample.Sequence, sample, binding, receipt, ct);
            foreach (var edit in batch.PlayEdits)
                if (receipt.Results.Any(r => r.Kind == "play_edits" && r.Key == edit.Id && r.Status is "applied" or "duplicate"))
                    await AcknowledgeAsync("play_edits", edit.Id, edit, binding, receipt, ct);
            receipts.Add(receipt);
        }
        return receipts;
    }
    public async Task<int> ExportAsync(string directory, CancellationToken ct = default)
    {
        var count = 0;
        foreach (var batch in await BatchesAsync(includeAcknowledged: true, ct))
        { await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(directory, batch.BatchId + ".json"), batch, false, ct); count++; }
        return count;
    }
    private string AckPath(string kind, string key) => Path.Combine(DirectoryPath, "acknowledgements", kind + "-" + LocalArchiveFormat.Hash(key) + ".json");
    private async Task<bool> AcknowledgedAsync<T>(string kind, string key, T payload, CancellationToken ct)
    {
        var file = AckPath(kind, key); if (!File.Exists(file)) return false;
        var sha = (await ReadAsync<ActivityAcknowledgement>(file, ct)).PayloadSha256;
        return LocalArchiveFormat.MatchesHash(payload, sha) || payload is SessionSample sample && LocalArchiveFormat.MatchesHash(sample with { InstallationId = null, ComponentId = null }, sha);
    }
    private Task AcknowledgeAsync<T>(string kind, string key, T payload, ActivityBinding binding, ActivityReceipt receipt, CancellationToken ct) =>
        JsonSnapshotStore.AtomicWriteAsync(AckPath(kind, key), new ActivityAcknowledgement(LocalArchiveFormat.Hash(payload), binding, receipt), true, ct);
    private static async Task<IReadOnlyList<T>> FilesAsync<T>(string directory, CancellationToken ct)
    {
        if (!Directory.Exists(directory)) return [];
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("活动目录不能是链接。");
        var results = new List<T>(); foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal)) results.Add(await ReadAsync<T>(file, ct)); return results;
    }
    private static async Task<T> ReadAsync<T>(string file, CancellationToken ct)
    {
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidDataException("活动状态不是普通JSON或超过8MiB。");
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
        return await JsonSerializer.DeserializeAsync<T>(stream, VndbJson.Options, ct) ?? throw new InvalidDataException("活动JSON损坏，原文件保留。");
    }
    private static void RequireId(string value) { if (!Guid.TryParseExact(value, "D", out _)) throw new ArgumentException("无效活动ID。"); }
}
