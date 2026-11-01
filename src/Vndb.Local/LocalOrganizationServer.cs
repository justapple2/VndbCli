using System.Security.Cryptography;
using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

/// <summary>Local JSON coordinator for the existing safe rename/recovery engine. No HTTP or database.</summary>
public sealed class LocalOrganizationServer(LocalLibraryStore library, LocalLauncher launcher, string deviceId) : IOrganizationServer
{
    private string Root => Path.Combine(library.DirectoryPath, "organization-state");
    private string RunFile(string id)
    { if (!Guid.TryParseExact(id, "D", out _)) throw new InvalidDataException("无效本机计划ID。"); return Path.Combine(Root, id + ".json"); }
    private FileStream Lock()
    { Directory.CreateDirectory(Root); return new(Path.Combine(Root, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    public Task<ServiceInfo> OrganizationInfoAsync(CancellationToken ct) => Task.FromResult(new ServiceInfo("local-" + deviceId, [1], "0.5.0", 0, false, ["archive_runs", "local_json_only"], [1], false));
    public async Task<LocalArchiveRunView> ArchiveRunAsync(string id, CancellationToken ct) =>
        JsonSerializer.Deserialize<LocalArchiveRunView>(await File.ReadAllTextAsync(RunFile(id), ct), VndbJson.Options) ?? throw new InvalidDataException("本机计划损坏，原件保留。");
    private Task Save(LocalArchiveRunView run, CancellationToken ct) => JsonSnapshotStore.AtomicWriteAsync(RunFile(run.Plan.Id), run, true, ct);
    public async Task<ArchiveReceipt> PlanArchiveAsync(LocalArchivePlan plan, CancellationToken ct)
    {
        using var observation = await LocalFileLease.AcquireAsync(library.DirectoryPath, ObservationResourceIntents.Resource, ct);
        OrganizationPlanner.RequireCompatibleDestination(plan);
        using var held = Lock();
        LocalArchiveFormat.Validate(plan.Before);
        if (plan.Before.Root.Volume != plan.TargetRootIdentity.Volume || plan.SourceScopeIdentity.Volume != plan.TargetRootIdentity.Volume) throw new InvalidDataException("不允许跨卷整理。");
        if (File.Exists(RunFile(plan.Id)))
        { var prior = await ArchiveRunAsync(plan.Id, ct); if (LocalArchiveFormat.Hash(plan) != prior.Receipt.PlanSha256) throw new InvalidDataException("已有不同计划。"); await ObservationResourceIntents.SetAsync(library.DirectoryPath, plan.InstallationId, plan.Id, prior.Receipt.State is "planned" or "restore_planned", ct); return prior.Receipt; }
        foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
        {
            var prior = JsonSerializer.Deserialize<LocalArchiveRunView>(await File.ReadAllTextAsync(file, ct), VndbJson.Options)!;
            if (prior.Receipt.State is "planned" or "restore_planned" && (prior.Plan.InstallationId == plan.InstallationId || LocalFiles.Same(prior.Plan.DestinationPath, plan.DestinationPath))) throw new InvalidDataException("已有未完成本机计划，请先续跑或撤销。");
        }
        var local = await library.ReadAsync(ct); var installation = local.Installations.SingleOrDefault(i => i.Installation.Id == plan.InstallationId)?.Installation
            ?? throw new ArgumentException("安装尚未登记到本机，请先使用本机登记或 organize register，再重新预览计划。");
        if (plan.DeviceId != deviceId || installation.Details.DeviceId != deviceId || installation.Revision != plan.InstallationRevision || !LocalFiles.Same(installation.Details.RootPath, plan.SourcePath)) throw new InvalidDataException("安装身份／修订已改变。");
        if (local.Installations.Any(i => i.Installation.RemovedAt is null && i.Installation.Id != installation.Id && LocalFiles.Same(i.Installation.Details.RootPath, plan.DestinationPath))) throw new IOException("目标已有安装，不覆盖或合并。");
        var now = DateTimeOffset.UtcNow; var receipt = new ArchiveReceipt(plan.Id, LocalArchiveFormat.Hash(plan), deviceId, installation.Id, "planned", 1, installation.Revision, now, now);
        await Save(new(plan, receipt), ct); await ObservationResourceIntents.SetAsync(library.DirectoryPath, plan.InstallationId, plan.Id, true, ct); return receipt;
    }
    public async Task<ArchiveReceipt> PlanRestoreAsync(LocalRestorePlan plan, CancellationToken ct)
    {
        using var observation = await LocalFileLease.AcquireAsync(library.DirectoryPath, ObservationResourceIntents.Resource, ct);
        using var held = Lock(); var run = await ArchiveRunAsync(plan.RunId, ct);
        if (run.RestorePlan is not null)
        { if (!LocalArchiveFormat.MatchesHash(plan, run.Receipt.RestoreSha256 ?? "")) throw new InvalidDataException("复原计划不同。"); await ObservationResourceIntents.SetAsync(library.DirectoryPath, run.Plan.InstallationId, plan.RunId, run.Receipt.State == "restore_planned", ct); return run.Receipt; }
        if (run.Receipt.State != "archived" || run.Receipt.Revision != plan.RunRevision || run.Receipt.InstallationRevision != plan.InstallationRevision || plan.ArchiveSha256 != run.Receipt.PlanSha256 ||
            !LocalFiles.Same(plan.SourcePath, run.Plan.DestinationPath) || !LocalFiles.Same(plan.DestinationPath, run.Plan.SourcePath)) throw new InvalidDataException("复原边界／修订无效。");
        var receipt = run.Receipt with { State = "restore_planned", Revision = run.Receipt.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow, RestoreSha256 = LocalArchiveFormat.Hash(plan) };
        await Save(run with { RestorePlan = plan, Receipt = receipt }, ct); await ObservationResourceIntents.SetAsync(library.DirectoryPath, run.Plan.InstallationId, plan.RunId, true, ct); return receipt;
    }
    public async Task<EvidenceReceipt> UploadEvidenceAsync(string file, string runId, string phase, CancellationToken ct)
    {
        _ = await ArchiveRunAsync(runId, ct);
        if (phase is not ("archive" or "restore" or "cancel") || new FileInfo(file).Length > 64 * 1024 * 1024) throw new InvalidDataException("无效本机证据。");
        var bytes = await File.ReadAllBytesAsync(file, ct); var evidence = JsonSerializer.Deserialize<LocalMoveEvidence>(bytes, VndbJson.Options) ?? throw new InvalidDataException("证据无效。");
        if (evidence.RunId != runId || evidence.Phase != phase) throw new InvalidDataException("证据计划／阶段不符。");
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var directory = Path.Combine(Root, runId, phase); Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, sha + ".json");
        if (!File.Exists(target)) await File.WriteAllBytesAsync(target, bytes, ct);
        else { var existing = await File.ReadAllBytesAsync(target, ct); if (!bytes.SequenceEqual(existing)) throw new InvalidDataException("已有证据字节不符。"); }
        return new(runId, phase, sha, bytes.Length, DateTimeOffset.UtcNow);
    }
    public async Task<ArchiveReceipt> CompleteArchiveAsync(string runId, ArchiveCompletion completion, bool restore, CancellationToken ct)
    {
        using var observation = await LocalFileLease.AcquireAsync(library.DirectoryPath, ObservationResourceIntents.Resource, ct);
        using var held = Lock(); var run = await ArchiveRunAsync(runId, ct); var state = restore ? "restored" : "archived"; var phase = restore ? "restore" : "archive";
        if (run.Receipt.State == state) { await ObservationResourceIntents.SetAsync(library.DirectoryPath, run.Plan.InstallationId, runId, false, ct); return run.Receipt; }
        if (run.Receipt.State != (restore ? "restore_planned" : "planned") || run.Receipt.Revision != completion.ExpectedRevision || !LocalArchiveFormat.IsHash(completion.EvidenceSha256)) throw new InvalidDataException("本机阶段／修订不符。");
        var evidence = RunJournal.Read<LocalMoveEvidence>(Path.Combine(Root, runId, phase, completion.EvidenceSha256 + ".json"));
        var expected = restore ? run.RestorePlan!.Before : run.Plan.Before;
        if (evidence.PlanSha256 != run.Receipt.PlanSha256 || evidence.Phase != phase || !LocalArchiveFormat.MatchesTree(evidence.After, expected)) throw new InvalidDataException("本机证据不符。");
        var source = restore ? run.RestorePlan!.SourcePath : run.Plan.SourcePath; var target = restore ? run.RestorePlan!.DestinationPath : run.Plan.DestinationPath;
        var local = await library.ReadAsync(ct); var view = local.Installations.Single(i => i.Installation.Id == run.Plan.InstallationId); var installation = view.Installation;
        if (LocalFiles.Same(installation.Details.RootPath, source) && installation.Revision == run.Receipt.InstallationRevision)
        {
            installation = installation with { Details = installation.Details with { RootPath = target }, DirectoryIdentity = evidence.After.Root, Status = "present", ObservedAt = DateTimeOffset.UtcNow, Revision = installation.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
            await library.UpdateAsync(current =>
            {
                var previous = current.Installations.Single(i => i.Installation.Id == installation.Id).Installation;
                if (previous.Revision != run.Receipt.InstallationRevision || !LocalFiles.Same(previous.Details.RootPath, source))
                    throw new InvalidDataException("提交期间安装修订或位置变化，保留恢复证据。");
                return current with { Installations = current.Installations.Select(i => i.Installation.Id == installation.Id ? i with { Installation = installation } : i).ToArray() };
            }, ct);
        }
        else if (!LocalFiles.Same(installation.Details.RootPath, target) || installation.Revision != run.Receipt.InstallationRevision + 1) throw new InvalidDataException("本机安装已被其他操作修改。");
        await launcher.RebaseAsync(installation.Id, source, target, ct);
        var receipt = run.Receipt with { State = state, Revision = run.Receipt.Revision + 1, InstallationRevision = installation.Revision, UpdatedAt = DateTimeOffset.UtcNow,
            EvidenceSha256 = restore ? run.Receipt.EvidenceSha256 : completion.EvidenceSha256, RestoreEvidenceSha256 = restore ? completion.EvidenceSha256 : run.Receipt.RestoreEvidenceSha256 };
        await Save(run with { Receipt = receipt }, ct); await ObservationResourceIntents.SetAsync(library.DirectoryPath, run.Plan.InstallationId, runId, false, ct); return receipt;
    }
    public async Task<ArchiveReceipt> CancelArchiveAsync(string runId, ArchiveCompletion completion, CancellationToken ct)
    {
        using var observation = await LocalFileLease.AcquireAsync(library.DirectoryPath, ObservationResourceIntents.Resource, ct);
        using var held = Lock(); var run = await ArchiveRunAsync(runId, ct);
        if (run.Receipt.State == "cancelled") { await ObservationResourceIntents.SetAsync(library.DirectoryPath, run.Plan.InstallationId, runId, false, ct); return run.Receipt; }
        if (run.Receipt.State != "planned" || completion.ExpectedRevision != run.Receipt.Revision || !LocalArchiveFormat.IsHash(completion.EvidenceSha256) || !File.Exists(Path.Combine(Root, runId, "cancel", completion.EvidenceSha256 + ".json"))) throw new InvalidDataException("撤销证据／修订无效。");
        var receipt = run.Receipt with { State = "cancelled", Revision = run.Receipt.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
        await Save(run with { Receipt = receipt }, ct); await ObservationResourceIntents.SetAsync(library.DirectoryPath, run.Plan.InstallationId, runId, false, ct); return receipt;
    }
}
