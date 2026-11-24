using Vndb.Contracts;

namespace Vndb.Local;

public static class OrganizationPlanner
{
    public static string Name(string value) => GameDirectoryNaming.Name(value);
    public static void RequireCompatibleDestination(LocalArchivePlan plan)
    {
        var root = LocalFiles.Full(plan.Policy.TargetRoot); var destination = LocalFiles.Full(plan.DestinationPath);
        var parts = Path.GetRelativePath(root, destination).Split(Path.DirectorySeparatorChar);
        if (!GameDirectoryNaming.IsCompatibleRoot(root) || parts.Length != 2 || parts.Any(p => !GameDirectoryNaming.IsCompatibleName(p)))
            throw new InvalidDataException("目标根或会社／游戏目录名含不兼容符号；尚未移动的旧计划请撤销后重新预览，不改写原恢复证据。");
    }
    public static LocalArchivePlan Create(LocalInstallationView installation, LocalArchivePolicy policy, string company, string japaneseTitle, CancellationToken ct = default, string? directoryAlias = null)
    {
        var source = LocalFiles.Full(installation.Installation.Details.RootPath); var scope = LocalFiles.Full(policy.SourceScope); var target = LocalFiles.Full(policy.TargetRoot);
        if (policy.Naming != "company-japanese-v1" || policy.Conflict != "stop" || !LocalFiles.Within(scope, source) || LocalFiles.Within(source, target) || LocalFiles.Same(source, target)) throw new ArgumentException("源安装必须在明确源范围内；目标不能位于安装中，规则必须为无覆盖停止。");
        LocalFiles.CheckAncestors(source); LocalFiles.CheckAncestors(target);
        if (!Directory.Exists(scope) || !Directory.Exists(target)) throw new ArgumentException("源范围与目标根必须预先存在，以绑定安全锚点。");
        var companyName = Name(company);
        if (LocalFiles.Within(target, source))
        {
            var existing = Path.GetRelativePath(target, source).Split(Path.DirectorySeparatorChar);
            if (existing.Length == 2 && GameDirectoryNaming.IsCompatibleName(existing[0]) && Name(existing[0]).Equals(companyName, StringComparison.OrdinalIgnoreCase))
                companyName = existing[0]; // Keep a compatible established company folder, including its width/case.
        }
        var destination = LocalFiles.Full(Path.Combine(target, companyName, Name(directoryAlias ?? japaneseTitle)));
        if (!GameDirectoryNaming.IsCompatibleRoot(target)) throw new ArgumentException("目标根路径含不兼容符号，请选择兼容的整理目标根；不会改名系统目录。");
        LocalFiles.CheckAncestors(destination);
        if (LocalFiles.Same(source, destination) || LocalFiles.Exists(destination) || LocalFiles.Within(destination, source)) throw new IOException("目标已存在、与源相同或边界重叠；不合并／覆盖。");
        var sourceIdentity = NativeFiles.Identity(scope); var targetIdentity = NativeFiles.Identity(target); var tree = LocalFiles.Snapshot(source, ct);
        if (sourceIdentity.Volume != tree.Root.Volume || targetIdentity.Volume != tree.Root.Volume) throw new IOException("拒绝跨卷整理；不会复制后删除。");
        if (installation.Installation.RemovedAt is not null || installation.Components.Count == 0) throw new ArgumentException("请选择活动完整安装及已确认的作品／特典组件。");
        return new(Guid.NewGuid().ToString("D"), installation.Installation.Details.DeviceId, installation.Installation.Id, installation.Installation.Revision,
            source, destination, policy with { SourceScope = scope, TargetRoot = target }, sourceIdentity, targetIdentity, tree,
            installation.Components.Select(c => new LocalArchiveComponent(c.Id, c.Details.RelativePath)).ToArray(), LocalFiles.Inspect(source), company, japaneseTitle, DateTimeOffset.UtcNow);
    }
}
