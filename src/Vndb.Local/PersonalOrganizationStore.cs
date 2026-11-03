using System.Text.Json;
using System.Text.Json.Serialization;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

public sealed record PersonalOrganizationDraft(string Id, PersonalOrganizationChange Change, string ResultSha256);
public sealed record PersonalOrganizationDocument(string Origin, string? ServiceInstanceId, PersonalOrganizationState Confirmed,
    IReadOnlyList<PersonalOrganizationDraft> Pending, bool Conflict = false, string Format = "vn-personal-organization", int FormatVersion = 1)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}

/// <summary>Small service-scoped JSON projection and immutable offline operations. Never contains resource locations.</summary>
public sealed class PersonalOrganizationStore(string directory, string? server)
{
    private readonly string _origin = string.IsNullOrWhiteSpace(server) ? "local" : ClientSettings.ServerUri(server).AbsoluteUri;
    private readonly JsonStateFile<PersonalOrganizationDocument> _file = new(directory, "personal-organization.json");
    public string ScopeKey => Path.GetFullPath(directory) + "\0" + _origin;
    private static void Safe(object value)
    { if (WirePrivacy.ContainsPrivateData(JsonSerializer.SerializeToElement(value, VndbJson.Options))) throw new ArgumentException("分类不可包含本机位置、程序或参数。"); }
    public static PersonalOrganizationState View(PersonalOrganizationDocument doc)
    {
        if (doc.Format != "vn-personal-organization" || doc.FormatVersion != 1 || doc.Pending is null || doc.Pending.Count > 100 || doc.Confirmed is null ||
            doc.Pending.Any(p => p is null || !Guid.TryParseExact(p.Id, "D", out _)) || doc.Pending.Select(p => p.Id).Distinct().Count() != doc.Pending.Count)
            throw new InvalidDataException("个人分类JSON格式无效；原件保留。");
        var state = doc.Confirmed; PersonalOrganizationRules.Validate(state);
        foreach (var pending in doc.Pending)
        {
            state = PersonalOrganizationRules.Apply(state, pending.Change);
            if (LocalArchiveFormat.Hash(state) != pending.ResultSha256) throw new InvalidDataException("分类原稿校验不符。");
        }
        Safe(state); return state;
    }
    public async Task<PersonalOrganizationDocument> ReadAsync(CancellationToken ct = default)
    {
        var doc = await _file.ReadAsync(ct) ?? new(_origin, null, PersonalOrganizationRules.Empty, []);
        if (doc.Origin != _origin) throw new InvalidDataException("分类属于其他服务；原件保留。");
        _ = View(doc); return doc;
    }
    public async Task EnqueueAsync(PersonalOrganizationChange change, CancellationToken ct = default)
    {
        Safe(change);
        using var lease = await LocalFileLease.AcquireAsync(directory, "personal-organization-operation", ct);
        var doc = await ReadAsync(ct);
        if (doc.Conflict || doc.Pending.Count >= 100) throw new InvalidOperationException("请先处理分类冲突或同步已有原稿。");
        var result = PersonalOrganizationRules.Apply(View(doc), change);
        await _file.SaveAsync(doc with { Pending = doc.Pending.Append(new(Guid.NewGuid().ToString("D"), change, LocalArchiveFormat.Hash(result))).ToArray() }, ct);
    }
    public async Task<int> SyncAsync(ISyncApi web, CancellationToken ct = default)
    {
        using var lease = await LocalFileLease.AcquireAsync(directory, "personal-organization-operation", ct);
        var doc = await ReadAsync(ct); if (doc.Pending.Count == 0 || doc.Conflict) return doc.Pending.Count;
        var info = await web.OrganizationInfoAsync(ct);
        RequireService(doc, info);
        if (doc.ServiceInstanceId is null) { doc = doc with { ServiceInstanceId = info.ServiceInstanceId }; await _file.SaveAsync(doc, ct); }
        foreach (var draft in doc.Pending.ToArray())
        {
            try
            {
                var saved = await web.SavePersonalOrganizationAsync(draft.Change, "organization-" + draft.Id, ct, doc.ServiceInstanceId);
                PersonalOrganizationRules.Validate(saved); Safe(saved);
                if (LocalArchiveFormat.Hash(saved) != draft.ResultSha256) throw new ClientApiException("organization_receipt_invalid", 200);
                await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(directory, "personal-organization-receipts", draft.Id + ".json"),
                    new { draft.Id, draft.ResultSha256, service_instance_id = info.ServiceInstanceId, origin = _origin }, true, ct);
                doc = doc with { Confirmed = saved, Pending = doc.Pending.Skip(1).ToArray() }; await _file.SaveAsync(doc, ct);
            }
            catch (ClientApiException e) when (e.Status is 400 or 409)
            { await _file.SaveAsync(doc with { Conflict = true }, ct); break; }
        }
        return doc.Pending.Count;
    }
    private void RequireService(PersonalOrganizationDocument doc, ServiceInfo info)
    {
        if (_origin == "local" || string.IsNullOrWhiteSpace(info.ServiceInstanceId) || !info.ApiVersions.Contains(1) || !info.Capabilities.Contains("personal_organization")) throw new ClientApiException("unsupported", 404);
        if (doc.ServiceInstanceId is { } instance && instance != info.ServiceInstanceId) throw new InvalidDataException("服务实例已改变；分类原稿保留，不能自动改绑。");
    }
    public async Task<PersonalOrganizationDocument> RefreshAsync(ISyncApi web, bool resolveConflict = false, CancellationToken ct = default)
    {
        using var lease = await LocalFileLease.AcquireAsync(directory, "personal-organization-operation", ct);
        var doc = await ReadAsync(ct);
        if (doc.Pending.Count > 0 && !(resolveConflict && doc.Conflict)) return doc;
        var info = await web.OrganizationInfoAsync(ct);
        if (!info.Capabilities.Contains("personal_organization")) return doc; // Existing services remain readable.
        RequireService(doc, info);
        var state = await web.PersonalOrganizationAsync(ct) ?? throw new ClientApiException("protocol_invalid", 200);
        PersonalOrganizationRules.Validate(state); Safe(state);
        if (state.Revision < doc.Confirmed.Revision) throw new InvalidDataException("分类修订发生回退；保留本机原件并核对服务恢复状态。");
        if (resolveConflict && doc.Conflict)
            await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(directory, "personal-organization-conflicts", Guid.NewGuid().ToString("D") + ".json"), doc, false, ct);
        doc = doc with { Confirmed = state, Pending = [], Conflict = false, ServiceInstanceId = info.ServiceInstanceId };
        await _file.SaveAsync(doc, ct); return doc;
    }
}
