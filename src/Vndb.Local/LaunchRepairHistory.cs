using Vndb.Contracts;

namespace Vndb.Local;

public sealed partial class LocalOrganizationServer
{
    // Historical receipts stay immutable. Only completed, evidenced moves can relocate a backup.
    internal async Task<InstallRepairRestore?> ResolveInstallRepairAsync(string installationId, string root, CancellationToken ct)
    {
        var local = await library.ReadAsync(ct);
        var view = local.Installations.Single(i => i.Installation.Id == installationId);
        var installation = view.Installation;
        if (installation.Details.DeviceId != deviceId || installation.RemovedAt is not null) throw new InvalidDataException("安装不属于当前设备或已移除。");
        var current = LocalFiles.Full(installation.Details.RootPath);
        root = LocalFiles.Full(root);
        LocalFiles.CheckAncestors(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("组件目录不存在。");
        var relative = Path.GetRelativePath(current, root);
        if ((!LocalFiles.Same(current, root) && !LocalFiles.Within(current, root)) ||
            view.Components.Count > 0 && !view.Components.Any(c => LocalFiles.Same(ComponentPath(current, c.Details.RelativePath), root)))
            throw new InvalidDataException("恢复范围与登记的组件不一致。");
        var identity = NativeFiles.Identity(root);
        var candidates = new LaunchRepair(library.DirectoryPath).InstallRepairs(installationId).Where(p => p.Identity == identity).ToArray();
        if (candidates.Length == 0) return null;
        var edges = new List<(long Revision, string Source, string Target)>();
        var runs = Path.Combine(library.DirectoryPath, "archive-runs");
        LocalFiles.CheckAncestors(runs);
        if (Directory.Exists(runs))
            foreach (var run in Directory.EnumerateDirectories(runs))
            {
                ct.ThrowIfCancellationRequested();
                var id = Path.GetFileName(run);
                if (!Guid.TryParseExact(id, "D", out _)) continue;
                LocalFiles.CheckAncestors(RunFile(id));
                if (!File.Exists(RunFile(id))) continue;
                var row = await ArchiveRunAsync(id, ct);
                if (row.Plan.InstallationId != installationId || row.Plan.DeviceId != deviceId || row.Plan.Before.Root != NativeFiles.Identity(current) ||
                    !LocalArchiveFormat.MatchesHash(row.Plan, row.Receipt.PlanSha256)) continue;
                foreach (var phase in new[] { "archive", "restore" })
                {
                    var file = Path.Combine(run, phase == "archive" ? "server-archived.json" : "server-restored.json");
                    LocalFiles.CheckAncestors(file);
                    if (!File.Exists(file) || new FileInfo(file).Length > 8192) continue;
                    var receipt = RunJournal.Read<ArchiveReceipt>(file);
                    var sha = phase == "archive" ? row.Receipt.EvidenceSha256 : row.Receipt.RestoreEvidenceSha256;
                    if (receipt.Id != id || receipt.InstallationId != installationId || receipt.DeviceId != deviceId || receipt.PlanSha256 != row.Receipt.PlanSha256 ||
                        receipt.State != (phase == "archive" ? "archived" : "restored") || receipt.InstallationRevision > installation.Revision || !LocalArchiveFormat.IsHash(sha ?? "") ||
                        (phase == "archive" ? receipt.EvidenceSha256 : receipt.RestoreEvidenceSha256) != sha) continue;
                    var evidenceFile = Path.Combine(Root, id, phase, sha + ".json");
                    LocalFiles.CheckAncestors(evidenceFile);
                    if (!File.Exists(evidenceFile) || new FileInfo(evidenceFile).Length > 64 * 1024 * 1024) continue;
                    var evidence = RunJournal.Read<LocalMoveEvidence>(evidenceFile);
                    if (FileHash(evidenceFile) != sha || evidence.RunId != id || evidence.Phase != phase || evidence.PlanSha256 != receipt.PlanSha256 ||
                        !LocalArchiveFormat.MatchesTree(evidence.After, phase == "archive" ? row.Plan.Before : row.RestorePlan?.Before ?? row.Plan.Before)) continue;
                    edges.Add((receipt.InstallationRevision, phase == "archive" ? row.Plan.SourcePath : row.Plan.DestinationPath,
                        phase == "archive" ? row.Plan.DestinationPath : row.Plan.SourcePath));
                }
            }
        if (edges.GroupBy(e => e.Revision).Any(g => g.Distinct().Count() > 1)) throw new IOException("移动链有歧义，请核对归档记录。");
        foreach (var plan in candidates)
        {
            var position = plan.Root;
            if (!LocalFiles.Same(position, root))
                foreach (var edge in edges.OrderBy(e => e.Revision))
                    if (LocalFiles.Same(position, ComponentPath(edge.Source, relative))) position = ComponentPath(edge.Target, relative);
            if (!LocalFiles.Same(position, root)) continue;
            var config = Path.Combine(root, "install.inf");
            LocalFiles.CheckAncestors(config);
            if (new FileInfo(config).Length > 8192 || !File.ReadAllBytes(config).AsSpan().SequenceEqual(plan.Updated)) continue;
            return new(plan, root);
        }
        throw new IOException("配置已变化或缺少已完成的移动链，无法安全恢复；请核对配置及归档回执。");
    }

    private static string FileHash(string file)
    { using var input = File.OpenRead(file); return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(input)); }
    private static string ComponentPath(string root, string relative) => relative == "." ? root : Path.Combine(root, relative);
}
