using Vndb.Contracts;

namespace Vndb.Local;

public sealed record LocalArchiveIntent(LocalArchivePlan Plan, ArchiveReceipt Receipt, string ServiceInstanceId, string Origin);
public sealed record LocalRestoreIntent(LocalRestorePlan Plan, ArchiveReceipt Receipt);
public sealed record OrganizationResult(string RunId, string State, string RunDirectory, ArchiveReceipt? Receipt = null);

public sealed class OrganizationRunner(IOrganizationServer server, string origin, string deviceId, Action<string>? checkpoint = null)
{
    public async Task<OrganizationResult> ExecuteAsync(LocalArchivePlan plan, string approval, string directory, CancellationToken ct = default)
    {
        Validate(plan, approval); directory = RunPath(directory, plan);
        if (File.Exists(Path.Combine(directory, "intent.json"))) return await ResumeAsync(directory, ct);
        OrganizationPlanner.RequireCompatibleDestination(plan);
        using var journal = new RunJournal(directory);
        CheckScopes(plan); RequireTree(plan.SourcePath, plan.Before, ct);
        if (LocalFiles.Exists(plan.DestinationPath)) throw new IOException("目标已存在，未执行。");
        var info = await server.OrganizationInfoAsync(ct); if (!info.Capabilities.Contains("archive_runs")) throw new InvalidOperationException("Web尚未支持整理回执。");
        var receipt = await server.PlanArchiveAsync(plan, ct); CheckReceipt(plan, receipt);
        var intent = new LocalArchiveIntent(plan, receipt, info.ServiceInstanceId, origin);
        RunJournal.Write(Path.Combine(directory, "intent.json"), intent); journal.BindValue("approved", intent);
        checkpoint?.Invoke("after_intent");
        return await ContinueAsync(directory, intent, null, journal, ct);
    }
    public async Task<OrganizationResult> ResumeAsync(string directory, CancellationToken ct = default)
    {
        var intent = RunJournal.Read<LocalArchiveIntent>(Path.Combine(directory, "intent.json")); Validate(intent.Plan, intent.Receipt.PlanSha256);
        directory = RunPath(directory, intent.Plan); CheckReceipt(intent.Plan, intent.Receipt);
        if (intent.Origin != origin) throw new InvalidDataException("必须使用原Web地址续跑，不能将恢复日志交给其他服务。");
        using var journal = new RunJournal(directory); journal.BindValue("approved", intent);
        if (File.Exists(Path.Combine(directory, "cancel-evidence.json"))) return await FinishCancelAsync(directory, intent, journal, ct);
        var restoreFile = Path.Combine(directory, "restore-intent.json"); var restore = File.Exists(restoreFile) ? RunJournal.Read<LocalRestoreIntent>(restoreFile) : null;
        if (restore is not null) journal.BindValue("restore_approved", restore);
        return await ContinueAsync(directory, intent, restore, journal, ct);
    }
    public async Task<LocalRestorePlan> PreviewRestoreAsync(string directory, CancellationToken ct = default)
    {
        var intent = RunJournal.Read<LocalArchiveIntent>(Path.Combine(directory, "intent.json")); Validate(intent.Plan, intent.Receipt.PlanSha256);
        if (intent.Origin != origin) throw new InvalidDataException("Web地址与原记录不符。");
        var info = await server.OrganizationInfoAsync(ct); if (info.ServiceInstanceId != intent.ServiceInstanceId) throw new InvalidDataException("Web服务实例已改变。");
        var run = await server.ArchiveRunAsync(intent.Plan.Id, ct);
        if (run.Receipt.State != "archived" || run.Receipt.PlanSha256 != intent.Receipt.PlanSha256) throw new InvalidOperationException("任务尚未完成归档或已进入复原。");
        CheckScopes(intent.Plan); var tree = LocalFiles.Snapshot(intent.Plan.DestinationPath, ct);
        if (tree.Root != intent.Plan.Before.Root || LocalFiles.Exists(intent.Plan.SourcePath)) throw new IOException("原路径被占用或安装身份已改变，不能复原。");
        var revision = await server.CurrentInstallationRevisionAsync(intent.Plan.InstallationId, ct) ?? run.Receipt.InstallationRevision;
        return new(run.Receipt.Id, run.Receipt.PlanSha256, intent.Plan.DestinationPath, intent.Plan.SourcePath, tree, revision, run.Receipt.Revision, DateTimeOffset.UtcNow);
    }
    public async Task<OrganizationResult> CancelAsync(string directory, string approval, CancellationToken ct = default)
    {
        var intent = RunJournal.Read<LocalArchiveIntent>(Path.Combine(directory, "intent.json")); Validate(intent.Plan, approval); directory = RunPath(directory, intent.Plan);
        if (intent.Origin != origin || File.Exists(Path.Combine(directory, "restore-intent.json"))) throw new InvalidDataException("只能在原服务撤销尚未归档的计划。");
        using var journal = new RunJournal(directory); journal.BindValue("approved", intent);
        return await FinishCancelAsync(directory, intent, journal, ct);
    }
    private async Task<OrganizationResult> FinishCancelAsync(string directory, LocalArchiveIntent intent, RunJournal journal, CancellationToken ct)
    {
        var plan = intent.Plan; var info = await server.OrganizationInfoAsync(ct);
        if (info.ServiceInstanceId != intent.ServiceInstanceId || intent.Origin != origin) throw new InvalidDataException("撤销服务实例不符。");
        var remote = await server.ArchiveRunAsync(plan.Id, ct); var receiptFile = Path.Combine(directory, "server-cancelled.json");
        if (remote.Receipt.State == "cancelled")
        { RunJournal.Write(receiptFile, remote.Receipt); journal.BindValue("server_cancelled", remote.Receipt); return new(plan.Id, "cancelled", directory, remote.Receipt); }
        if (remote.Receipt.State != "planned" || remote.Receipt.PlanSha256 != intent.Receipt.PlanSha256) throw new InvalidOperationException("仅能撤销尚未完成归档且原位置仍在的任务。");
        CheckScopes(plan); if (LocalFiles.Exists(plan.DestinationPath)) throw new IOException("目标已出现，不能证明尚未移动；请核对／续跑原任务。");
        var after = LocalFiles.Snapshot(plan.SourcePath, ct); if (after.Root != plan.Before.Root) throw new IOException("原目录身份不符，不能自动释放位置保留。");
        var file = Path.Combine(directory, "cancel-evidence.json");
        if (!File.Exists(file)) RunJournal.Write(file, new LocalMoveEvidence(plan.Id, intent.Receipt.PlanSha256, "cancel", plan.Before, after, journal.Head, DateTimeOffset.UtcNow));
        var evidence = RunJournal.Read<LocalMoveEvidence>(file);
        if (!LocalArchiveFormat.MatchesTree(after, evidence.After)) throw new IOException("撤销意图后目录再次改变，保留记录停止。");
        journal.BindValue("cancel_intent", evidence); // Persist before remote cancellation: an offline resume must never rename this run.
        var uploaded = await server.UploadEvidenceAsync(file, plan.Id, "cancel", ct);
        var result = await server.CancelArchiveAsync(plan.Id, new(uploaded.Sha256, intent.Receipt.Revision), ct);
        RunJournal.Write(receiptFile, result); journal.BindValue("server_cancelled", result); return new(plan.Id, "cancelled", directory, result);
    }
    public async Task<OrganizationResult> RestoreAsync(LocalRestorePlan plan, string approval, string directory, CancellationToken ct = default)
    {
        if (!LocalArchiveFormat.MatchesHash(plan, approval)) throw new ArgumentException("请明确批准本次复原预览的SHA256。");
        var intent = RunJournal.Read<LocalArchiveIntent>(Path.Combine(directory, "intent.json")); Validate(intent.Plan, intent.Receipt.PlanSha256); directory = RunPath(directory, intent.Plan);
        using var journal = new RunJournal(directory); journal.BindValue("approved", intent);
        var file = Path.Combine(directory, "restore-intent.json");
        LocalRestoreIntent restore;
        if (File.Exists(file))
        {
            restore = RunJournal.Read<LocalRestoreIntent>(file); if (!LocalArchiveFormat.MatchesHash(restore.Plan, approval)) throw new InvalidDataException("已有不同的复原意图。");
        }
        else
        {
            if (intent.Origin != origin || plan.RunId != intent.Plan.Id || plan.ArchiveSha256 != intent.Receipt.PlanSha256 || !LocalFiles.Same(plan.SourcePath, intent.Plan.DestinationPath) || !LocalFiles.Same(plan.DestinationPath, intent.Plan.SourcePath) || plan.Before.Root != intent.Plan.Before.Root) throw new InvalidDataException("复原不能改变原始边界或物理身份。");
            CheckScopes(intent.Plan); RequireTree(plan.SourcePath, plan.Before, ct);
            if (LocalFiles.Exists(plan.DestinationPath)) throw new IOException("复原目标已存在；不合并或覆盖。");
            var info = await server.OrganizationInfoAsync(ct); if (info.ServiceInstanceId != intent.ServiceInstanceId) throw new InvalidDataException("Web服务实例不符。");
            restore = new(plan, await server.PlanRestoreAsync(plan, ct)); RunJournal.Write(file, restore);
        }
        journal.BindValue("restore_approved", restore); checkpoint?.Invoke("after_restore_intent");
        return await ContinueAsync(directory, intent, restore, journal, ct);
    }
    private async Task<OrganizationResult> ContinueAsync(string directory, LocalArchiveIntent intent, LocalRestoreIntent? restore, RunJournal journal, CancellationToken ct)
    {
        var plan = intent.Plan; var phase = restore is null ? "archive" : "restore"; var completedState = restore is null ? "archived" : "restored";
        if (restore is not null && (restore.Plan.RunId != plan.Id || restore.Plan.ArchiveSha256 != intent.Receipt.PlanSha256 || restore.Plan.Before.Root != plan.Before.Root ||
            !LocalFiles.Same(restore.Plan.SourcePath, plan.DestinationPath) || !LocalFiles.Same(restore.Plan.DestinationPath, plan.SourcePath) || restore.Receipt.PlanSha256 != intent.Receipt.PlanSha256 ||
            !LocalArchiveFormat.MatchesHash(restore.Plan, restore.Receipt.RestoreSha256 ?? ""))) throw new InvalidDataException("复原持久意图与原始边界／回执不符。");
        var accepted = restore?.Receipt ?? intent.Receipt;
        if (accepted.State != (restore is null ? "planned" : "restore_planned")) throw new InvalidDataException("缺少有效的执行前回执。");
        var savedReceipt = Path.Combine(directory, "server-" + completedState + ".json");
        if (File.Exists(savedReceipt)) return new(plan.Id, completedState, directory, RunJournal.Read<ArchiveReceipt>(savedReceipt));
        try
        {
            var info = await server.OrganizationInfoAsync(ct); if (info.ServiceInstanceId != intent.ServiceInstanceId) throw new InvalidDataException("服务实例改变，不能静默提交到另一数据库。");
            var remote = await server.ArchiveRunAsync(plan.Id, ct);
            if (remote.Receipt.PlanSha256 != intent.Receipt.PlanSha256) throw new InvalidDataException("Web计划摘要不符。");
            if (remote.Receipt.State == completedState)
            {
                RunJournal.Write(savedReceipt, remote.Receipt); journal.BindValue("server_" + completedState, remote.Receipt); return new(plan.Id, completedState, directory, remote.Receipt);
            }
            if (remote.Receipt.State != accepted.State || remote.Receipt.Revision != accepted.Revision) throw new InvalidDataException("Web任务状态变化，停止离线意图。");
        }
        catch (WebUnavailableException) { } // Only an already-durable accepted intent may proceed offline.
        CheckScopes(plan);
        var source = restore?.Plan.SourcePath ?? plan.SourcePath; var destination = restore?.Plan.DestinationPath ?? plan.DestinationPath; var expected = restore?.Plan.Before ?? plan.Before;
        var evidenceFile = Path.Combine(directory, phase + "-evidence.json");
        if (!File.Exists(evidenceFile))
        {
            LocalTreeProof after;
            if (LocalFiles.Exists(source) && !LocalFiles.Exists(destination))
            {
                if (restore is null) OrganizationPlanner.RequireCompatibleDestination(plan);
                using var parents = Parents(destination, restore is null ? plan.Policy.TargetRoot : plan.Policy.SourceScope);
                using var handle = NativeFiles.Open(source, rename: true);
                if (NativeFiles.Identity(handle, expected.Root.Volume) != expected.Root) throw new IOException("源目录身份已替换。");
                journal.Append(phase + "_rename_intent", expected.Sha256); checkpoint?.Invoke("before_rename");
                RequireTree(source, expected, ct); ct.ThrowIfCancellationRequested();
                if (LocalFiles.Exists(destination)) throw new IOException("目标在执行前出现，停止无覆盖移动。");
                NativeFiles.Rename(handle, destination); checkpoint?.Invoke("after_rename");
                after = RequireTree(destination, expected, ct); journal.Append(phase + "_rename_done", after.Sha256);
            }
            else if (!LocalFiles.Exists(source) && LocalFiles.Exists(destination) && journal.Has(phase + "_rename_intent"))
            {
                after = RequireTree(destination, expected, ct); journal.Append(phase + "_rename_recovered", after.Sha256);
            }
            else throw new IOException("源／目标状态不唯一；保留全部文件及日志，禁止重复移动、覆盖或合并。");
            var evidence = new LocalMoveEvidence(plan.Id, intent.Receipt.PlanSha256, phase, expected, after, journal.Head, DateTimeOffset.UtcNow);
            RunJournal.Write(evidenceFile, evidence); journal.BindValue(phase + "_evidence", evidence);
        }
        else
        {
            var saved = RunJournal.Read<LocalMoveEvidence>(evidenceFile);
            if (saved.PlanSha256 != intent.Receipt.PlanSha256 || saved.Phase != phase || saved.Before.Sha256 != expected.Sha256 || !LocalArchiveFormat.MatchesTree(saved.After, expected)) throw new InvalidDataException("已有证据与批准计划不符。");
            RequireTree(destination, expected, ct); if (LocalFiles.Exists(source)) throw new IOException("提交前原路径再次出现，停止自动提交。");
            journal.BindValue(phase + "_evidence", saved);
        }
        try
        {
            checkpoint?.Invoke("before_sync");
            var evidence = await server.UploadEvidenceAsync(evidenceFile, plan.Id, phase, ct);
            if (evidence.RunId != plan.Id || evidence.Phase != phase || !LocalArchiveFormat.MatchesHash(RunJournal.Read<LocalMoveEvidence>(evidenceFile), evidence.Sha256)) throw new InvalidDataException("Web证据回执摘要不符。");
            var result = await server.CompleteArchiveAsync(plan.Id, new(evidence.Sha256, accepted.Revision), restore is not null, ct);
            checkpoint?.Invoke("after_commit"); RunJournal.Write(savedReceipt, result); journal.BindValue("server_" + completedState, result);
            return new(plan.Id, completedState, directory, result);
        }
        catch (WebUnavailableException) { journal.Append(phase + "_awaiting_sync", expected.Sha256); return new(plan.Id, "awaiting_sync", directory); }
    }
    private void Validate(LocalArchivePlan plan, string approval)
    {
        if (plan.Format != "vn-archive-plan" || plan.FormatVersion != 1 || plan.DeviceId != deviceId || !LocalArchiveFormat.MatchesHash(plan, approval)) throw new InvalidDataException("需要本机身份及人工批准的规范计划摘要；导入元数据不能执行。");
        LocalArchiveFormat.Validate(plan.Before);
        if (!LocalFiles.Within(plan.Policy.SourceScope, plan.SourcePath) || !LocalFiles.Within(plan.Policy.TargetRoot, plan.DestinationPath) || LocalFiles.Within(plan.SourcePath, plan.DestinationPath) || LocalFiles.Within(plan.DestinationPath, plan.SourcePath) || LocalFiles.Same(plan.SourcePath, plan.DestinationPath)) throw new InvalidDataException("计划范围交叠或越界。");
    }
    private static void CheckReceipt(LocalArchivePlan plan, ArchiveReceipt receipt)
    {
        if (receipt.Id != plan.Id || !LocalArchiveFormat.MatchesHash(plan, receipt.PlanSha256) || receipt.DeviceId != plan.DeviceId || receipt.InstallationId != plan.InstallationId) throw new InvalidDataException("Web planned回执不匹配本机计划。");
    }
    private static string RunPath(string directory, LocalArchivePlan plan)
    {
        directory = Path.GetFullPath(directory); LocalFiles.CheckAncestors(directory);
        if (LocalFiles.Within(plan.SourcePath, directory) || LocalFiles.Within(plan.DestinationPath, directory) || LocalFiles.Same(plan.SourcePath, directory) || LocalFiles.Same(plan.DestinationPath, directory)) throw new ArgumentException("管理日志必须在游戏物理目录之外。");
        return directory;
    }
    private static void CheckScopes(LocalArchivePlan plan)
    {
        LocalFiles.CheckAncestors(plan.Policy.SourceScope); LocalFiles.CheckAncestors(plan.Policy.TargetRoot);
        if (NativeFiles.Identity(plan.Policy.SourceScope) != plan.SourceScopeIdentity || NativeFiles.Identity(plan.Policy.TargetRoot) != plan.TargetRootIdentity || plan.TargetRootIdentity.Volume != plan.Before.Root.Volume || plan.SourceScopeIdentity.Volume != plan.Before.Root.Volume) throw new IOException("源范围／目标根身份或卷已改变。");
    }
    private static LocalTreeProof RequireTree(string path, LocalTreeProof expected, CancellationToken ct)
    {
        var actual = LocalFiles.Snapshot(path, ct); if (!LocalArchiveFormat.MatchesTree(actual, expected)) throw new IOException("目录内容、隐藏项或文件身份与批准指纹不符；未删除或覆盖任何文件。"); return actual;
    }
    private sealed class Gates : IDisposable
    {
        public List<IDisposable> Handles { get; } = [];
        public void Dispose() { foreach (var handle in Handles.AsEnumerable().Reverse()) handle.Dispose(); }
    }
    private static Gates Parents(string destination, string scope)
    {
        if (!LocalFiles.Within(scope, destination)) throw new IOException("目标越过批准根。");
        var gates = new Gates();
        try
        {
            // Pin every existing ancestor as well as each created parent, preventing junction/parent replacement during rename.
            var ancestors = new Stack<string>(); for (var p = Path.GetDirectoryName(destination); p is not null; p = Path.GetDirectoryName(p)) ancestors.Push(p);
            foreach (var parent in ancestors)
            {
                LocalFiles.CheckAncestors(parent);
                if (!LocalFiles.Exists(parent))
                {
                    if (!LocalFiles.Within(scope, parent)) throw new IOException("缺失父目录不在允许范围内。");
                    Directory.CreateDirectory(parent);
                }
                gates.Handles.Add(NativeFiles.Open(parent, gate: true));
            }
            return gates;
        }
        catch { gates.Dispose(); throw; }
    }
}
