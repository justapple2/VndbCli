using System.Text.Json;
using System.Text.Json.Serialization;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

public sealed record PersonalEditDraft(string Id, string BookKey, string Origin, string? PlayId, PersonalPlayWrite Request, DateTimeOffset CreatedAt)
{
    // Omitted defaults keep hashes of pre-K6 immutable drafts and receipts unchanged.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PersonalPlayData? Original { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool OriginalKnown { get; init; }
}
public sealed record PersonalEditReceipt(string PayloadSha256, ActivityBinding Binding, PersonalPlayView Saved);
public sealed record PersonalEditConflict(PersonalPlayView? Current, PersonalPlayWrite Incoming, DateTimeOffset DetectedAt,
    bool CurrentKnown = false, long? ItemRevision = null, string? ItemNovelId = null, bool? ItemRemoved = null, int Status = 409, bool ReadFailed = false);
public sealed record PersonalEditInspection(PersonalEditDraft Draft, PersonalOperationState State, PersonalPlayView? Current = null,
    bool CurrentKnown = false, DateTimeOffset? ObservedAt = null, PersonalEditConflict? Conflict = null, bool ReadFailed = false)
{
    public IReadOnlyList<FieldDifference> Differences
    {
        get
        {
            var rows = PersonalComparison.Play(Draft.Original?.ToPersonal(), Draft.OriginalKnown, Current?.Details.ToPersonal(), CurrentKnown, Draft.Request.Details.ToPersonal());
            return CurrentKnown && Current?.ItemId is { } current && Draft.Request.ItemId is { } expected && current != expected
                ? rows.Append(new("item_id", new(expected), new(current), new(expected))).ToArray() : rows;
        }
    }
}

/// <summary>Explicit personal-field edits use existing revision-guarded CRUD, not activity edits that protect the time base.</summary>
public sealed class PersonalEditOutbox(string directory)
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetFullPath(directory), "personal-edits");
    private string FilePath(string id, string name) { if (!Guid.TryParseExact(id, "D", out _)) throw new ArgumentException("编辑ID无效。"); return Path.Combine(DirectoryPath, id, name + ".json"); }
    private static async Task<T> ReadAsync<T>(string file, CancellationToken ct)
    {
        LocalFileLease.RequireOrdinaryAncestors(file);
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || new FileInfo(file).Length > 1024 * 1024) throw new InvalidDataException("个人编辑JSON不是普通文件或过大。");
        return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(file, ct), VndbJson.Options) ?? throw new InvalidDataException("个人编辑JSON无效。");
    }
    public async Task<IReadOnlyList<PersonalEditDraft>> PendingAsync(string origin, CancellationToken ct = default)
    {
        origin = ClientSettings.ServerUri(origin).AbsoluteUri;
        if (!Directory.Exists(DirectoryPath)) return [];
        var result = new List<PersonalEditDraft>();
        foreach (var folder in Directory.EnumerateDirectories(DirectoryPath))
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("个人编辑目录不能是链接。");
            var id = Path.GetFileName(folder); if (!Guid.TryParseExact(id, "D", out _)) continue;
            if (!File.Exists(FilePath(id, "draft")) || File.Exists(FilePath(id, "receipt")) || File.Exists(FilePath(id, "withdrawn"))) continue;
            var draft = await ReadAsync<PersonalEditDraft>(FilePath(id, "draft"), ct);
            if (draft.Id != id) throw new InvalidDataException("个人编辑ID不符。");
            if (draft.Origin == origin) result.Add(draft);
        }
        return result.OrderBy(d => d.CreatedAt).ToArray();
    }
    public async Task<PersonalEditDraft> EnqueueAsync(string bookKey, string origin, string? playId, PersonalPlayWrite request, CancellationToken ct = default, PersonalPlayView? original = null)
    {
        origin = ClientSettings.ServerUri(origin).AbsoluteUri;
        if (request.Details is null || playId is not null && request.ExpectedRevision is null || playId is null && request.ItemId is null)
            throw new ArgumentException("编辑需要原修订；新建需要明确的Web书架条目。");
        PersonalRules.Validate(request.Details.ToPersonal());
        if (original is not null && (original.Id != playId || original.Revision != request.ExpectedRevision || original.ItemId is { } item && item != request.ItemId))
            throw new ArgumentException("原始记录身份或修订不符，未保存编辑。");
        if (WirePrivacy.ContainsPrivateData(JsonSerializer.SerializeToElement(request, VndbJson.Options))) throw new ArgumentException("个人记录不能包含本地目录或程序信息。");
        using var lease = await LocalFileLease.AcquireAsync(Path.GetDirectoryName(DirectoryPath)!, "personal-create/" + origin + "/" + (playId ?? "new:" + bookKey), ct);

        {
            if ((await PendingAsync(origin, ct)).Any(d => playId is null ? d.PlayId is null && d.BookKey == bookKey : d.PlayId == playId))
                throw new InvalidOperationException("这条记录已有待同步编辑，请先处理原稿。");
            var draft = new PersonalEditDraft(Guid.NewGuid().ToString("D"), bookKey, origin, playId, request, DateTimeOffset.UtcNow)
            { Original = original?.Details, OriginalKnown = playId is null || original is not null };
            await JsonSnapshotStore.AtomicWriteAsync(FilePath(draft.Id, "draft"), draft, false, ct); return draft;
        }
    }
    public async Task<PersonalPlayView> SyncOneAsync(ISyncApi web, string origin, PersonalEditDraft draft, CancellationToken ct = default)
    {
        using var lease = await LocalFileLease.AcquireAsync(Path.GetDirectoryName(DirectoryPath)!, "personal-operation/" + draft.Id, ct);

        {
            origin = ClientSettings.ServerUri(origin).AbsoluteUri;
            if (draft.Origin != origin || await ReadAsync<PersonalEditDraft>(FilePath(draft.Id, "draft"), ct) != draft) throw new InvalidDataException("编辑绑定其他服务或原稿变化。");
            if (File.Exists(FilePath(draft.Id, "withdrawn"))) throw new InvalidOperationException("该稿已经撤回，原件保留。");
            var info = await web.OrganizationInfoAsync(ct);
            if (string.IsNullOrWhiteSpace(info.ServiceInstanceId) || !info.ApiVersions.Contains(1)) throw new ClientApiException("protocol_invalid", 200);
            var binding = new ActivityBinding(origin, info.ServiceInstanceId);
            var bindFile = FilePath(draft.Id, "binding");
            if (File.Exists(bindFile))
            {
                var previous = await ReadAsync<ActivityBinding>(bindFile, ct);
                SyncRules.RequireBinding(previous.Origin, previous.ServiceInstanceId, binding.Origin, binding.ServiceInstanceId);
            }
            else await JsonSnapshotStore.AtomicWriteAsync(bindFile, binding, false, ct);
            if (File.Exists(FilePath(draft.Id, "receipt")))
            {
                var prior = await ReadReceiptAsync(draft, ct);
                if (prior.Binding != binding) throw new InvalidDataException("个人编辑回执不符。");
                return prior.Saved;
            }
            if (File.Exists(FilePath(draft.Id, "conflict"))) throw new ClientApiException("conflict", 409);
            var wasUncertain = false;
            if (File.Exists(FilePath(draft.Id, "attempt")))
            {
                var previous = await ReadAsync<Attempt>(FilePath(draft.Id, "attempt"), ct);
                if (previous.PayloadSha256 != LocalArchiveFormat.Hash(draft)) throw new InvalidDataException("编辑尝试证据不符。");
                wasUncertain = previous.State == "uncertain";
            }
            try
            {
                // An uncertain request may already be committed. Replay it before inspecting newer item state.
                if (!wasUncertain && draft.Request.ItemId is { } itemId)
                {
                    var item = await web.ItemAsync(itemId, ct);
                    if (item.Item.Id != itemId || item.Item.DeletedAt is not null || item.Item.Details.NovelId != draft.Request.Details.NovelId) throw new ClientApiException("conflict", 409);
                }
                await AttemptAsync(draft, "uncertain", ct);
                var saved = await web.SavePersonalPlayAsync(draft.Request, draft.PlayId, "personal-" + draft.Id, ct);
                if (saved.Id.Length == 0 || draft.PlayId is not null && saved.Id != draft.PlayId || saved.Revision != (draft.Request.ExpectedRevision ?? 0) + 1 || saved.Details != draft.Request.Details ||
                    draft.Request.ItemId is { } expectedItem && saved.ItemId is { } actualItem && actualItem != expectedItem)
                    throw new ClientApiException("personal_receipt_invalid", 200);
                if (await ReadAsync<PersonalEditDraft>(FilePath(draft.Id, "draft"), ct) != draft) throw new InvalidDataException("提交期间编辑原稿变化，未确认本机操作。");
                await JsonSnapshotStore.AtomicWriteAsync(FilePath(draft.Id, "receipt"), new PersonalEditReceipt(LocalArchiveFormat.Hash(draft), binding, saved), false, ct);
                return saved;
            }
            catch (ClientApiException e) when (e.Status is 400 or 409)
            {
                var conflict = new PersonalEditConflict(null, draft.Request, DateTimeOffset.UtcNow, Status: e.Status);
                // Record the known rejection before a second network request can fail or be cancelled.
                await JsonSnapshotStore.AtomicWriteAsync(FilePath(draft.Id, "conflict"), conflict, false, CancellationToken.None);
                var observed = await ObserveAsync(web, draft, conflict, ct);
                await JsonSnapshotStore.AtomicWriteAsync(FilePath(draft.Id, "conflict"), observed, true, CancellationToken.None); throw;
            }
            catch (ClientApiException e) when (e.Status is 401 or 403 or 429)
            { await AttemptAsync(draft, wasUncertain ? "uncertain" : "pending", CancellationToken.None); throw; }
        }
    }
    private Task AttemptAsync(PersonalEditDraft draft, string state, CancellationToken ct) => JsonSnapshotStore.AtomicWriteAsync(FilePath(draft.Id, "attempt"),
        new { payload_sha256 = LocalArchiveFormat.Hash(draft), state }, true, ct);
    private sealed record Attempt(string PayloadSha256, string State);
    private async Task<PersonalEditReceipt> ReadReceiptAsync(PersonalEditDraft draft, CancellationToken ct)
    {
        var receipt = await ReadAsync<PersonalEditReceipt>(FilePath(draft.Id, "receipt"), ct);
        var binding = await ReadAsync<ActivityBinding>(FilePath(draft.Id, "binding"), ct);
        if (receipt.Binding != binding || binding.Origin != draft.Origin || string.IsNullOrWhiteSpace(binding.ServiceInstanceId) ||
            receipt.PayloadSha256 != LocalArchiveFormat.Hash(draft) || receipt.Saved is not { Id.Length: > 0 } saved ||
            draft.PlayId is not null && saved.Id != draft.PlayId || saved.Revision != (draft.Request.ExpectedRevision ?? 0) + 1 || saved.Details != draft.Request.Details ||
            draft.Request.ItemId is { } expectedItem && saved.ItemId is { } actualItem && actualItem != expectedItem)
            throw new InvalidDataException("个人编辑回执不符；原件保留，未确认应用。");
        return receipt;
    }
    private static async Task<PersonalEditConflict> ObserveAsync(ISyncApi web, PersonalEditDraft draft, PersonalEditConflict before, CancellationToken ct)
    {
        var result = before with { ReadFailed = false };
        try
        {
            if (draft.PlayId is { } id)
            {
                PersonalPlayView? current;
                try { current = await web.PersonalPlayAsync(id, ct); }
                catch (ClientApiException e) when (e.Status == 404) { current = null; }
                if (current is not null && current.Id != id) throw new InvalidDataException("Web记录身份不符，原稿保留。");
                result = result with { Current = current, CurrentKnown = true, DetectedAt = DateTimeOffset.UtcNow };
            }
            if (draft.Request.ItemId is { } itemId)
            {
                try
                {
                    var item = await web.ItemAsync(itemId, ct);
                    if (item.Item.Id != itemId) throw new InvalidDataException("Web条目身份不符。");
                    result = result with { ItemRevision = item.Item.Revision, ItemNovelId = item.Item.Details.NovelId, ItemRemoved = item.Item.DeletedAt is not null };
                }
                catch (ClientApiException e) when (e.Status == 404) { result = result with { ItemRemoved = true }; }
            }
        }
        catch (Exception e) when (e is ClientApiException or WebUnavailableException or HttpRequestException or OperationCanceledException) { result = result with { ReadFailed = true }; }
        return result;
    }
    public async Task<PersonalEditInspection> InspectAsync(PersonalEditDraft draft, string origin, ISyncApi? web = null, bool refresh = false, CancellationToken ct = default)
    {
        using var lease = await LocalFileLease.AcquireAsync(Path.GetDirectoryName(DirectoryPath)!, "personal-operation/" + draft.Id, ct);
        if (draft.Origin != ClientSettings.ServerUri(origin).AbsoluteUri || await ReadAsync<PersonalEditDraft>(FilePath(draft.Id, "draft"), ct) != draft)
            throw new InvalidDataException("个人编辑身份或原稿变化。");
        PersonalEditConflict? conflict = null; var state = PersonalOperationState.Pending;
        if (File.Exists(FilePath(draft.Id, "attempt")))
        {
            var attempted = await ReadAsync<Attempt>(FilePath(draft.Id, "attempt"), ct);
            if (attempted.PayloadSha256 != LocalArchiveFormat.Hash(draft) || attempted.State is not ("pending" or "uncertain")) throw new InvalidDataException("编辑尝试证据不符。");
            if (attempted.State == "uncertain") state = PersonalOperationState.Uncertain;
        }
        if (File.Exists(FilePath(draft.Id, "conflict")))
        {
            conflict = await ReadAsync<PersonalEditConflict>(FilePath(draft.Id, "conflict"), ct);
            if (conflict.Incoming != draft.Request || conflict.Current is { } current && current.Id != draft.PlayId) throw new InvalidDataException("冲突证据不符。");
            state = PersonalOperationState.Conflict;
        }
        if (File.Exists(FilePath(draft.Id, "withdrawn"))) state = PersonalOperationState.Withdrawn;
        PersonalPlayView? saved = null;
        if (File.Exists(FilePath(draft.Id, "receipt")))
        {
            var receipt = await ReadReceiptAsync(draft, ct);
            state = PersonalOperationState.Applied; saved = receipt.Saved;
        }
        var observed = conflict; var failed = conflict?.ReadFailed ?? false;
        if (refresh)
        {
            if (web is null) failed = true;
            else
            {
                var info = await web.OrganizationInfoAsync(ct);
                if (File.Exists(FilePath(draft.Id, "binding")))
                {
                    var binding = await ReadAsync<ActivityBinding>(FilePath(draft.Id, "binding"), ct);
                    SyncRules.RequireBinding(binding.Origin, binding.ServiceInstanceId, draft.Origin, info.ServiceInstanceId);
                }
                observed = await ObserveAsync(web, draft, conflict ?? new(null, draft.Request, DateTimeOffset.UtcNow), ct);
                failed = observed.ReadFailed || draft.PlayId is not null && !observed.CurrentKnown;
            }
        }
        var hasObservation = observed?.CurrentKnown == true || observed?.Current is not null;
        return new(draft, state, hasObservation ? observed?.Current : saved, hasObservation || saved is not null,
            observed?.DetectedAt, observed, failed);
    }
    public async Task<int> SyncAsync(ISyncApi web, string origin, CancellationToken ct = default)
    {
        var count = 0;
        foreach (var draft in await PendingAsync(origin, ct))
        {
            if (File.Exists(FilePath(draft.Id, "conflict"))) continue;
            try { await SyncOneAsync(web, origin, draft, ct); count++; }
            catch (ClientApiException e) when (e.Status is 400 or 409) { }
        }
        return count;
    }
    public async Task WithdrawConflictAsync(PersonalEditDraft draft, CancellationToken ct = default)
    {
        using var lease = await LocalFileLease.AcquireAsync(Path.GetDirectoryName(DirectoryPath)!, "personal-operation/" + draft.Id, ct);

        {
            if (!File.Exists(FilePath(draft.Id, "conflict")) || File.Exists(FilePath(draft.Id, "receipt"))) throw new InvalidOperationException("仅允许撤回已确认冲突且未应用的编辑；结果不明的请求需按原ID重试。");
            await JsonSnapshotStore.AtomicWriteAsync(FilePath(draft.Id, "withdrawn"), new { at = DateTimeOffset.UtcNow, reason = "user_reload_after_conflict" }, false, ct);
        }
    }
}
