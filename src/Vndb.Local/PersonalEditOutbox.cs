using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

public sealed record PersonalEditDraft(string Id, string BookKey, string Origin, string? PlayId, PersonalPlayWrite Request, DateTimeOffset CreatedAt);
public sealed record PersonalEditReceipt(string PayloadSha256, ActivityBinding Binding, PersonalPlayView Saved);

/// <summary>Explicit personal-field edits use existing revision-guarded CRUD, not activity edits that protect the time base.</summary>
public sealed class PersonalEditOutbox(string directory)
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetFullPath(directory), "personal-edits");
    private string FilePath(string id, string name) { if (!Guid.TryParseExact(id, "D", out _)) throw new ArgumentException("编辑ID无效。"); return Path.Combine(DirectoryPath, id, name + ".json"); }
    private static async Task<T> ReadAsync<T>(string file, CancellationToken ct)
    {
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
    public async Task<PersonalEditDraft> EnqueueAsync(string bookKey, string origin, string? playId, PersonalPlayWrite request, CancellationToken ct = default)
    {
        origin = ClientSettings.ServerUri(origin).AbsoluteUri;
        if (request.Details is null || playId is not null && request.ExpectedRevision is null || playId is null && request.ItemId is null)
            throw new ArgumentException("编辑需要原修订；新建需要明确的Web书架条目。");
        PersonalRules.Validate(request.Details.ToPersonal());
        if (WirePrivacy.ContainsPrivateData(JsonSerializer.SerializeToElement(request, VndbJson.Options))) throw new ArgumentException("个人记录不能包含本地目录或程序信息。");
        using var lease = await LocalFileLease.AcquireAsync(Path.GetDirectoryName(DirectoryPath)!, "personal-create/" + origin + "/" + (playId ?? "new:" + bookKey), ct);

        {
            if ((await PendingAsync(origin, ct)).Any(d => playId is null ? d.PlayId is null && d.BookKey == bookKey : d.PlayId == playId))
                throw new InvalidOperationException("这条记录已有待同步编辑，请先处理原稿。");
            var draft = new PersonalEditDraft(Guid.NewGuid().ToString("D"), bookKey, origin, playId, request, DateTimeOffset.UtcNow);
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
                var prior = await ReadAsync<PersonalEditReceipt>(FilePath(draft.Id, "receipt"), ct);
                if (prior.Binding != binding || prior.PayloadSha256 != LocalArchiveFormat.Hash(draft)) throw new InvalidDataException("个人编辑回执不符。");
                return prior.Saved;
            }
            if (File.Exists(FilePath(draft.Id, "conflict"))) throw new ClientApiException("conflict", 409);
            try
            {
                if (draft.Request.ItemId is { } itemId)
                {
                    var item = await web.ItemAsync(itemId, ct);
                    if (item.Item.DeletedAt is not null || item.Item.Details.NovelId != draft.Request.Details.NovelId) throw new ClientApiException("conflict", 409);
                }
                var saved = await web.SavePersonalPlayAsync(draft.Request, draft.PlayId, "personal-" + draft.Id, ct);
                if (saved.Id.Length == 0 || draft.PlayId is not null && saved.Id != draft.PlayId || saved.Revision != (draft.Request.ExpectedRevision ?? 0) + 1 || saved.Details != draft.Request.Details ||
                    draft.Request.ItemId is { } expectedItem && saved.ItemId is { } actualItem && actualItem != expectedItem)
                    throw new ClientApiException("personal_receipt_invalid", 200);
                if (await ReadAsync<PersonalEditDraft>(FilePath(draft.Id, "draft"), ct) != draft) throw new InvalidDataException("提交期间编辑原稿变化，未确认本机操作。");
                await JsonSnapshotStore.AtomicWriteAsync(FilePath(draft.Id, "receipt"), new PersonalEditReceipt(LocalArchiveFormat.Hash(draft), binding, saved), false, ct);
                return saved;
            }
            catch (ClientApiException e) when (e.Status == 409)
            {
                PersonalPlayView? current = null;
                if (draft.PlayId is not null) current = await web.PersonalPlayAsync(draft.PlayId, ct);
                await JsonSnapshotStore.AtomicWriteAsync(FilePath(draft.Id, "conflict"), new { current, incoming = draft.Request, detected_at = DateTimeOffset.UtcNow }, false, ct); throw;
            }
        }
    }
    public async Task<int> SyncAsync(ISyncApi web, string origin, CancellationToken ct = default)
    {
        var count = 0;
        foreach (var draft in await PendingAsync(origin, ct))
        {
            if (File.Exists(FilePath(draft.Id, "conflict"))) continue;
            try { await SyncOneAsync(web, origin, draft, ct); count++; }
            catch (ClientApiException e) when (e.Status == 409) { }
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
