using Microsoft.Data.Sqlite;
using Vndb.Contracts;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    private static void InitializeM4(SqliteConnection db, SqliteTransaction tx) => Execute(db, tx, """
        CREATE TABLE IF NOT EXISTS archive_runs(id TEXT PRIMARY KEY,plan_json TEXT NOT NULL,receipt_json TEXT NOT NULL,restore_json TEXT);
        CREATE TABLE IF NOT EXISTS archive_unit_leases(installation_id TEXT PRIMARY KEY REFERENCES installations(id),run_id TEXT NOT NULL REFERENCES archive_runs(id),device_id TEXT NOT NULL,target_path_key TEXT NOT NULL,UNIQUE(device_id,target_path_key));
        CREATE TABLE IF NOT EXISTS archive_evidence(run_id TEXT NOT NULL REFERENCES archive_runs(id),phase TEXT NOT NULL,sha256 TEXT NOT NULL,receipt_json TEXT NOT NULL,binding_json TEXT NOT NULL,PRIMARY KEY(run_id,phase,sha256));
        PRAGMA user_version=8;
        """);
    private static void EnsureNoArchiveLease(SqliteConnection db, SqliteTransaction tx, string installationId)
    {
        using var query = Command(db, tx, "SELECT run_id FROM archive_unit_leases WHERE installation_id=$id", ("$id", installationId));
        if (query.ExecuteScalar() is not null) throw new PlayConflictException("安装有未完成的整理／复原回执；请在原客户端安全续跑，不改写位置或组件。");
    }
    private static void EnsureArchiveDestinationAvailable(SqliteConnection db, SqliteTransaction tx, string installationId, string deviceId, string path)
    {
        var key = InstallationRules.PathKey(path);
        using var query = Command(db, tx, "SELECT target_path_key FROM archive_unit_leases WHERE device_id=$device AND installation_id<>$id", ("$device", deviceId), ("$id", installationId));
        using var reader = query.ExecuteReader();
        while (reader.Read())
        {
            var reserved = reader.GetString(0);
            if (reserved == key || reserved.StartsWith(key.TrimEnd('/') + "/", StringComparison.Ordinal) || key.StartsWith(reserved.TrimEnd('/') + "/", StringComparison.Ordinal)) throw new PlayConflictException("目标位置已有未完成整理／复原预留，不能另建安装覆盖。");
        }
    }
    private static void Lease(SqliteConnection db, SqliteTransaction tx, string id, string run, string device, string target)
    {
        EnsureArchiveDestinationAvailable(db, tx, id, device, target);
        var key = InstallationRules.PathKey(target);
        foreach (var other in Rows<Installation>(db, tx, "installations").Where(i => i.Id != id && i.RemovedAt is null && i.Details.DeviceId == device))
        {
            var path = InstallationRules.PathKey(other.Details.RootPath);
            if (path == key || path.StartsWith(key.TrimEnd('/') + "/", StringComparison.Ordinal) || key.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal)) throw new PlayConflictException("目标与另一个完整安装边界相交；请先核对现有记录。");
        }
        Execute(db, tx, "INSERT INTO archive_unit_leases VALUES($installation,$run,$device,$target)", ("$installation", id), ("$run", run), ("$device", device), ("$target", key));
    }
    private static ArchiveRunView? ArchiveRun(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        using var query = Command(db, tx, "SELECT plan_json,receipt_json,restore_json FROM archive_runs WHERE id=$id", ("$id", id)); using var reader = query.ExecuteReader();
        return reader.Read() ? new(Read<ArchivePlan>(reader.GetString(0)), Read<ArchiveReceipt>(reader.GetString(1)), reader.IsDBNull(2) ? null : Read<RestorePlan>(reader.GetString(2))) : null;
    }
    public ArchiveRunView? GetArchiveRun(string id) { using var db = Open(); return ArchiveRun(db, null, id); }
    public IReadOnlyList<ArchiveReceipt> ArchiveRuns()
    {
        using var db = Open(); using var query = Command(db, null, "SELECT receipt_json FROM archive_runs ORDER BY rowid DESC LIMIT 100"); using var reader = query.ExecuteReader();
        var rows = new List<ArchiveReceipt>(); while (reader.Read()) rows.Add(Read<ArchiveReceipt>(reader.GetString(0))); return rows;
    }
    private static void SaveRun(SqliteConnection db, SqliteTransaction tx, ArchiveReceipt receipt)
    {
        var previous = ArchiveRun(db, tx, receipt.Id)!.Receipt;
        Execute(db, tx, "UPDATE archive_runs SET receipt_json=$json WHERE id=$id", ("$json", Json(receipt)), ("$id", receipt.Id));
        Event(db, tx, receipt.Id, "organization-" + receipt.State, Json(previous), Json(receipt));
    }
    public ArchiveReceipt PlanArchive(ArchivePlan plan, string key)
    {
        ArgumentNullException.ThrowIfNull(plan); ArchiveFormat.Validate(plan.Before);
        if (plan.Format != "vn-archive-plan" || plan.FormatVersion != 1 || !Guid.TryParseExact(plan.Id, "D", out _) || plan.Policy.Naming != "company-japanese-v1" || plan.Policy.Conflict != "stop") throw new ArgumentException("计划格式或规则无效。");
        InstallationRules.Text(plan.Company); InstallationRules.Text(plan.Title);
        _ = InstallationRules.Under(plan.Policy.SourceScope, plan.SourcePath);
        if (InstallationRules.Under(plan.Policy.TargetRoot, plan.DestinationPath).Split('/').Length != 2 ||
            InstallationRules.PathKey(plan.DestinationPath).StartsWith(InstallationRules.PathKey(plan.SourcePath).TrimEnd('/') + "/", StringComparison.Ordinal) ||
            plan.Before.Root.Volume != plan.SourceScopeIdentity.Volume || plan.Before.Root.Volume != plan.TargetRootIdentity.Volume) throw new ArgumentException("计划边界／卷身份无效。");
        return Mutate("organization/plan/" + plan.Id, key, plan, (db, tx) =>
        {
            var hash = ArchiveFormat.Hash(plan);
            if (ArchiveRun(db, tx, plan.Id) is { } existing) return ArchiveFormat.MatchesHash(plan, existing.Receipt.PlanSha256) ? existing.Receipt : throw new PlayConflictException("同一任务ID不能更换计划。");
            var device = ActiveDevice(db, tx, plan.DeviceId);
            if (device.Legacy) throw new ArgumentException("请先注册并明确认领本机设备身份。");
            var installation = ActiveInstallation(db, tx, plan.InstallationId, plan.InstallationRevision);
            if (installation.Details.DeviceId != plan.DeviceId || InstallationRules.PathKey(installation.Details.RootPath) != InstallationRules.PathKey(plan.SourcePath)) throw new PlayConflictException("计划与当前设备／安装路径不符。");
            var components = Rows<InstallationComponent>(db, tx, "installation_components").Where(c => c.InstallationId == installation.Id).Select(c => new ArchiveComponent(c.Id, c.Details.RelativePath)).OrderBy(c => c.Id).ToArray();
            if (components.Length == 0 || plan.Components is null || ArchiveFormat.Hash(components) != ArchiveFormat.Hash(plan.Components.OrderBy(c => c.Id).ToArray())) throw new PlayConflictException("完整物理容器的组件映射已改变，请重新规划。");
            EnsureNoArchiveLease(db, tx, installation.Id);
            var now = DateTimeOffset.UtcNow; var receipt = new ArchiveReceipt(plan.Id, hash, plan.DeviceId, plan.InstallationId, "planned", 1, installation.Revision, now, now);
            Execute(db, tx, "INSERT INTO archive_runs VALUES($id,$plan,$receipt,NULL)", ("$id", plan.Id), ("$plan", Json(plan)), ("$receipt", Json(receipt)));
            Lease(db, tx, installation.Id, plan.Id, plan.DeviceId, plan.DestinationPath);
            Event(db, tx, plan.Id, "organization-planned", null, Json(receipt)); return receipt;
        });
    }
    public ArchiveReceipt PlanRestore(string id, RestorePlan plan, string key)
    {
        ArchiveFormat.Validate(plan.Before);
        return Mutate("organization/restore-plan/" + id, key, plan, (db, tx) =>
        {
            var run = ArchiveRun(db, tx, id) ?? throw new ArgumentException("整理任务不存在。"); var receipt = run.Receipt;
            if (receipt.State == "restore_planned" && receipt.RestoreSha256 is { } oldHash && ArchiveFormat.MatchesHash(plan, oldHash)) return receipt;
            if (receipt.State != "archived" || plan.RunRevision != receipt.Revision || plan.RunId != id || plan.ArchiveSha256 != receipt.PlanSha256) throw new PlayConflictException("复原计划与当前任务修订不符。");
            var installation = ActiveInstallation(db, tx, receipt.InstallationId, plan.InstallationRevision);
            if (InstallationRules.PathKey(installation.Details.RootPath) != InstallationRules.PathKey(plan.SourcePath) || InstallationRules.PathKey(plan.SourcePath) != InstallationRules.PathKey(run.Plan.DestinationPath) ||
                InstallationRules.PathKey(plan.DestinationPath) != InstallationRules.PathKey(run.Plan.SourcePath) || plan.Before.Root != run.Plan.Before.Root) throw new PlayConflictException("安装位置／物理身份变化，不能应用旧复原计划。");
            EnsureNoArchiveLease(db, tx, installation.Id);
            Execute(db, tx, "UPDATE archive_runs SET restore_json=$json WHERE id=$id", ("$json", Json(plan)), ("$id", id));
            Lease(db, tx, installation.Id, id, run.Plan.DeviceId, plan.DestinationPath);
            var result = receipt with { State = "restore_planned", RestoreSha256 = ArchiveFormat.Hash(plan), InstallationRevision = installation.Revision, Revision = receipt.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
            SaveRun(db, tx, result); return result;
        });
    }
    private sealed record EvidenceBinding(string BeforeSha256, string AfterSha256, PhysicalIdentity Root, DateTimeOffset CompletedAt);
    public EvidenceReceipt RegisterArchiveEvidence(MoveEvidence evidence, string sha, long bytes, string key)
    {
        ArchiveFormat.Validate(evidence.Before); ArchiveFormat.Validate(evidence.After);
        if (!ArchiveFormat.IsHash(sha) || !ArchiveFormat.IsHash(evidence.JournalHead) || bytes <= 0 || evidence.CompletedAt == default || evidence.CompletedAt > DateTimeOffset.UtcNow.AddMinutes(5)) throw new ArgumentException("证据摘要／时间无效。");
        return Mutate("organization/evidence/" + evidence.RunId + "/" + evidence.Phase, key, new { sha, bytes }, (db, tx) =>
        {
            var run = ArchiveRun(db, tx, evidence.RunId) ?? throw new ArgumentException("计划不存在。");
            var expected = evidence.Phase is "archive" or "cancel" ? run.Plan.Before : evidence.Phase == "restore" ? run.RestorePlan?.Before : null;
            if (expected is null || evidence.PlanSha256 != run.Receipt.PlanSha256 || evidence.Before.Sha256 != expected.Sha256 || evidence.Phase != "cancel" && !ArchiveFormat.MatchesTree(evidence.After, expected) || evidence.After.Root != expected.Root || evidence.Phase == "cancel" && run.Receipt.State != "planned") throw new PlayConflictException("证据不匹配预先批准的完整目录指纹。");
            using var found = Command(db, tx, "SELECT receipt_json FROM archive_evidence WHERE run_id=$id AND phase=$phase AND sha256=$sha", ("$id", evidence.RunId), ("$phase", evidence.Phase), ("$sha", sha));
            if (found.ExecuteScalar() is string old) return Read<EvidenceReceipt>(old);
            var receipt = new EvidenceReceipt(evidence.RunId, evidence.Phase, sha, bytes, DateTimeOffset.UtcNow);
            Execute(db, tx, "INSERT INTO archive_evidence VALUES($id,$phase,$sha,$receipt,$binding)", ("$id", evidence.RunId), ("$phase", evidence.Phase), ("$sha", sha), ("$receipt", Json(receipt)), ("$binding", Json(new EvidenceBinding(evidence.Before.Sha256, evidence.After.Sha256, evidence.After.Root, evidence.CompletedAt))));
            Event(db, tx, evidence.RunId, "organization-evidence", null, Json(receipt)); return receipt;
        });
    }
    public ArchiveReceipt CancelArchive(string id, ArchiveCompletion completion, string key) => Mutate("organization/cancel/" + id, key, completion, (db, tx) =>
    {
        var run = ArchiveRun(db, tx, id) ?? throw new ArgumentException("任务不存在。"); var r = run.Receipt;
        if (r.State != "planned" || r.Revision != completion.ExpectedRevision) throw new PlayConflictException("只能撤销尚未完成归档的计划。");
        using var evidence = Command(db, tx, "SELECT binding_json FROM archive_evidence WHERE run_id=$id AND phase='cancel' AND sha256=$sha", ("$id", id), ("$sha", completion.EvidenceSha256));
        var binding = evidence.ExecuteScalar() is string json ? Read<EvidenceBinding>(json) : throw new ArgumentException("请先上传原目录身份仍在的核对证据。");
        if (binding.Root != run.Plan.Before.Root) throw new PlayConflictException("原目录身份不符，不能安全撤销。");
        _ = ActiveInstallation(db, tx, r.InstallationId, r.InstallationRevision);
        Execute(db, tx, "DELETE FROM archive_unit_leases WHERE installation_id=$installation AND run_id=$run", ("$installation", r.InstallationId), ("$run", id));
        var result = r with { State = "cancelled", Revision = r.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow, EvidenceSha256 = completion.EvidenceSha256 }; SaveRun(db, tx, result); return result;
    });
    public ArchiveReceipt CompleteArchive(string id, ArchiveCompletion completion, bool restore, string key) => Mutate("organization/complete/" + id + "/" + restore, key, completion, (db, tx) =>
    {
        var run = ArchiveRun(db, tx, id) ?? throw new ArgumentException("任务不存在。"); var r = run.Receipt; var phase = restore ? "restore" : "archive";
        if (r.Revision != completion.ExpectedRevision || r.State != (restore ? "restore_planned" : "planned")) throw new PlayConflictException("任务修订／状态冲突。");
        using var evidence = Command(db, tx, "SELECT binding_json FROM archive_evidence WHERE run_id=$id AND phase=$phase AND sha256=$sha", ("$id", id), ("$phase", phase), ("$sha", completion.EvidenceSha256));
        var binding = evidence.ExecuteScalar() is string json ? Read<EvidenceBinding>(json) : throw new ArgumentException("请先上传与计划绑定的证据。");
        var installation = ActiveInstallation(db, tx, r.InstallationId, r.InstallationRevision);
        var destination = restore ? run.Plan.SourcePath : run.Plan.DestinationPath;
        var details = installation.Details with { RootPath = destination };
        if (!restore) details = details with { CompanyGroup = run.Plan.Company, Title = run.Plan.Title };
        var now = DateTimeOffset.UtcNow;
        var updated = installation with { Details = details, Revision = installation.Revision + 1, UpdatedAt = now, Status = "present", ObservedAt = binding.CompletedAt };
        WriteInstallation(db, tx, updated); // No item/VN/personal edits: manual associations survive restoration.
        Execute(db, tx, "DELETE FROM archive_unit_leases WHERE installation_id=$installation AND run_id=$run", ("$installation", r.InstallationId), ("$run", id));
        var result = r with { State = restore ? "restored" : "archived", Revision = r.Revision + 1, InstallationRevision = updated.Revision, UpdatedAt = now,
            EvidenceSha256 = restore ? r.EvidenceSha256 : completion.EvidenceSha256, RestoreEvidenceSha256 = restore ? completion.EvidenceSha256 : null };
        SaveRun(db, tx, result); return result;
    });
}
