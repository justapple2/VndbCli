using Vndb.Contracts;

namespace Vndb.Local;

/// <summary>One local organization use case boundary shared by CLI and desktop composition.</summary>
public sealed class LocalOrganizationWorkspace(string directory, ClientDeviceIdentity identity)
{
    private readonly LocalLibraryStore _library = new(directory);
    private readonly LocalLauncher _launcher = new(directory);
    public static async Task<LocalOrganizationWorkspace> OpenAsync(string? directory, CancellationToken ct) =>
        new(directory ?? ClientSettings.UserDirectory, await LocalDeviceIdentity.LoadOrCreateAsync(directory, ct));
    public OrganizationRunner Runner(Action<string>? progress = null) => new(new LocalOrganizationServer(_library, _launcher, identity.Id), "local:" + identity.Id, identity.Id, progress);
    public string RunDirectory(string id) => Path.Combine(directory, "archive-runs", id);
    public Task<string?> PreviousRootAsync(LocalInstallation installation, CancellationToken ct) =>
        new LocalOrganizationServer(_library, _launcher, identity.Id).PreviousRootAsync(installation, ct);
    public Task<InstallRepairRestore?> PreviewInstallRestoreAsync(string installationId, string root, CancellationToken ct) =>
        new LocalOrganizationServer(_library, _launcher, identity.Id).ResolveInstallRepairAsync(installationId, root, ct);
    public async Task RestoreInstallAsync(InstallRepairRestore preview, CancellationToken ct)
    {
        var current = await PreviewInstallRestoreAsync(preview.Repair.InstallationId, preview.Root, ct);
        if (current is null || LocalArchiveFormat.Hash(current) != LocalArchiveFormat.Hash(preview)) throw new IOException("恢复目标或备份已变化，请重新预览。");
        await new LaunchRepair(directory).RestoreResolvedAsync(current, ct);
    }

    public Task<LocalArchivePlan> PreviewEngineAliasAsync(LocalInstallationView installation, string componentRoot, string? entry,
        string alias, CancellationToken ct)
    {
        EnsureDevice(installation);
        return Task.Run(() =>
        {
            var root = LocalFiles.Full(installation.Installation.Details.RootPath);
            if ((!LocalFiles.Same(root, componentRoot) && !LocalFiles.Within(root, componentRoot)) || LaunchRepair.Detect(componentRoot, entry) != GameEngine.AdvHd)
                throw new InvalidOperationException("短目录预览仅用于已确认的 AdvHD 安装。");
            if (alias.Length is < 1 or > 32 || !alias.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                throw new ArgumentException("短目录名仅支持 1–32 个英文字母、数字、连字符和下划线。");
            var companyDirectory = Path.GetDirectoryName(root)!;
            var scope = Path.GetDirectoryName(companyDirectory) ?? throw new ArgumentException("需要会社／游戏两级目录。");
            var company = Path.GetFileName(companyDirectory);
            if (!GameDirectoryNaming.IsCompatibleName(company) || Path.Combine(companyDirectory, alias).Any(c => !char.IsAscii(c)))
                throw new ArgumentException("上层目录也需兼容 ASCII。请在目录整理中明确选择兼容目标根和会社目录。");
            return OrganizationPlanner.Create(installation, new(scope, scope), company, installation.Installation.Details.Title, ct, directoryAlias: alias);
        }, ct);
    }
    public Task<DirectoryScan> ScanAsync(string scope, IReadOnlyList<LocalInstallationView> known, CancellationToken ct) =>
        Task.Run(() => DirectoryScanner.Scan(scope, known: known.Where(i => i.Installation.Details.DeviceId == identity.Id).ToArray(), ct: ct), ct);
    public Task<LocalArchivePlan> PreviewAsync(LocalInstallationView installation, string scope, string target, string company, string title, CancellationToken ct)
    {
        EnsureDevice(installation);
        return Task.Run(() => OrganizationPlanner.Create(installation, new(scope, target), company, title, ct), ct);
    }
    private void EnsureDevice(LocalInstallationView view)
    {
        if (identity.Id != view.Installation.Details.DeviceId) throw new ArgumentException("安装不属于本机UUID；请先核对并认领旧设备。");
    }
    public async Task<LocalInstallationAudit> AuditAsync(string scope, string target, string? launchDirectory, CancellationToken ct)
    {
        var local = await _library.ReadAsync(ct);
        var profiles = await (launchDirectory is null ? _launcher : new LocalLauncher(launchDirectory)).ReadAsync(ct);
        return InstallationAuditor.Check("local-" + identity.Id, identity.Id, scope, target, local.Installations, profiles, ct, local.Items);
    }
    public async Task<LocalInstallationAudit> RecheckAsync(string scope, string target, CancellationToken ct)
    {
        var local = await _library.ReadAsync(ct);
        var profiles = await _launcher.ReadAsync(ct);
        var report = await Task.Run(() => InstallationAuditor.Check("local-" + identity.Id, identity.Id, scope, target, local.Installations, profiles, ct, local.Items), ct);
        var rows = report.Items.Where(i => i.Status != "unknown").ToDictionary(i => i.InstallationId);
        var observed = local.Installations.ToDictionary(i => i.Installation.Id, i => LocalArchiveFormat.Hash(i));
        await _library.UpdateAsync(current => current with { Installations = current.Installations.Select(i =>
        {
            if (!rows.TryGetValue(i.Installation.Id, out var row)) return i;
            if (i.Installation.Revision != row.ExpectedRevision || LocalArchiveFormat.Hash(i) != observed[i.Installation.Id])
                throw new InvalidDataException("核对期间安装修订或内容变化，请重新核对；未覆盖。");
            return i with { Installation = i.Installation with { Status = row.Status, ObservedAt = report.ObservedAt, Revision = i.Installation.Revision + 1, UpdatedAt = report.ObservedAt } };
        }).ToArray() }, ct);
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(directory, "local-checks", report.Id + ".json"), report, false, ct);
        return report;
    }
    public static async Task<DirectoryScan> ScanAsync(string directory, string scope, int depth, string? snapshot, CancellationToken ct)
    {
        var known = snapshot is null ? (await new LocalLibraryStore(directory).ReadAsync(ct)).Installations : RunJournal.Read<LocalInstallationView[]>(snapshot);
        return DirectoryScanner.Scan(scope, depth, known, ct);
    }
    public async Task<LocalArchivePlan> PlanAsync(string? installationId, string? snapshot, string scope, string target, string? company, string? title, CancellationToken ct)
    {
        LocalInstallationView view; MetadataSnapshot? metadata = null;
        if (snapshot is not null)
        { if (installationId is not null) throw new ArgumentException("请选择安装ID或离线快照之一。"); view = RunJournal.Read<LocalInstallationView>(snapshot); }
        else
        {
            var local = await _library.ReadAsync(ct);
            view = local.Installations.Single(i => i.Installation.Id == (installationId ?? throw new ArgumentException("需要本机 --installation 或 --installation-json。")));
            if (view.Components.Count > 0) metadata = local.Items.SingleOrDefault(i => i.Item.Id == view.Components[0].Details.ItemId)?.Metadata;
        }
        EnsureDevice(view);
        var group = company ?? view.Installation.Details.CompanyGroup;
        if (group is { Length: > 1 } g && g[0] == 'p' && g[1..].All(char.IsAsciiDigit))
            group = metadata?.Developers.FirstOrDefault(d => d.Id == g) is { } producer ? producer.Original ?? producer.Name : null;
        if (group is null && metadata?.Developers.Count == 1) group = metadata.Developers[0].Original ?? metadata.Developers[0].Name;
        var japaneseTitle = title ?? metadata?.Novel?.Titles.FirstOrDefault(t => t.Lang == "ja")?.Title;
        if (view.Components.Count > 1 && title is null) throw new ArgumentException("多作品容器请用 --title 明确完整合集名，不能以第一章标题改外层。");
        return OrganizationPlanner.Create(view, new(scope, target), group ?? throw new ArgumentException("请用 --company 明确归档会社，不能猜测多个开发者。"), japaneseTitle ?? throw new ArgumentException("请用 --title 明确游戏日文原名。"), ct);
    }
}
