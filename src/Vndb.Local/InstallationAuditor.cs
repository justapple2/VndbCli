using Vndb.Contracts;

namespace Vndb.Local;

/// <summary>Bounded read-only checks of explicitly scoped installations. Never configures or launches an EXE.</summary>
public static class InstallationAuditor
{
    public static LocalInstallationAudit Check(string instanceId, string deviceId, string scope, string targetRoot,
        IReadOnlyList<LocalInstallationView> installations, IReadOnlyList<LaunchProfile>? profiles = null, CancellationToken ct = default,
        IReadOnlyList<LibraryItemView>? items = null)
    {
        scope = LocalFiles.Full(scope); targetRoot = LocalFiles.Full(targetRoot);
        if (!Guid.TryParseExact(deviceId, "D", out _) || string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("需要服务实例与本机UUID。");
        if (installations.Count > 2000) throw new ArgumentException("每次最多核对2000个安装，请缩小范围。");
        var rows = new List<LocalInstallationCheck>();
        bool scopeAvailable;
        try { LocalFiles.CheckAncestors(scope); scopeAvailable = (File.GetAttributes(scope) & FileAttributes.Directory) != 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { scopeAvailable = false; }
        foreach (var view in installations.Where(v => v.Installation.Details.DeviceId == deviceId && v.Installation.RemovedAt is null && v.Installation.MergedIntoId is null))
        {
            ct.ThrowIfCancellationRequested(); var installation = view.Installation; var path = installation.Details.RootPath;
            var components = new List<LocalComponentCheck>(); var executables = new List<LocalAuditExecutable>(); var launchStatus = "not_configured";
            string status, reason;
            try
            {
                if (!LocalFiles.Within(scope, path)) continue;
                if (!scopeAvailable) throw new IOException("源范围不可访问，不能据此判断游戏丢失。");
                LocalFiles.CheckAncestors(path);
                if ((File.GetAttributes(path) & FileAttributes.Directory) == 0) throw new IOException("原目录位置不是文件夹。");
                // Successful enumeration distinguishes a readable directory from Directory.Exists swallowing access errors.
                _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToArray();
                status = "present"; reason = "根目录可读取；仅确认文件存在，不证明游戏可运行或注册表依赖完整。";
                foreach (var component in view.Components)
                {
                    var relative = component.Details.RelativePath;
                    try
                    {
                        // Relative paths from Web remain untrusted. Reject rooted, traversal and alternate streams before joining.
                        if (relative != "." && (Path.IsPathRooted(relative) || relative.Replace('\\', '/').Split('/').Any(s => s.Length == 0 || s is "." or ".." || s.Contains(':'))))
                            throw new IOException("组件边界不合法。");
                        var full = relative == "." ? path : LocalFiles.Full(Path.Combine(path, relative.Replace('/', '\\')));
                        if (relative != "." && !LocalFiles.Within(path, full)) throw new IOException("组件越过容器。");
                        LocalFiles.CheckAncestors(full);
                        if ((File.GetAttributes(full) & FileAttributes.Directory) == 0) throw new IOException("组件不是目录。");
                        _ = Directory.EnumerateFileSystemEntries(full).Take(1).ToArray();
                        components.Add(new(component.Id, relative, "present"));
                    }
                    catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                    { components.Add(new(component.Id, relative, "missing")); status = "unverified"; reason = "根目录存在，但有组件缺失；完整容器需人工核对。"; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                    { components.Add(new(component.Id, relative, "unknown")); status = "unverified"; reason = "组件不可读或存在边界问题，未跟随链接。"; }
                }
                if (view.Components.Count == 0) { status = "unverified"; reason = "根目录可读但尚无已确认组件。"; }
                try
                {
                    var directories = components.Where(c => c.Status == "present").Select(c => c.RelativePath == "." ? path : Path.Combine(path, c.RelativePath.Replace('/', '\\')))
                        .Prepend(path).Distinct(StringComparer.OrdinalIgnoreCase);
                    foreach (var directory in directories)
                        executables.AddRange(GameExecutables.Discover(directory).Select(e => new LocalAuditExecutable(e.Path, e.Description)));
                    var bound = (profiles ?? []).Where(p => p.InstallationId == installation.Id &&
                        (p.ComponentId is null || view.Components.Any(c => c.Id == p.ComponentId))).ToArray();
                    launchStatus = bound.Length == 0 ? "not_configured" : bound.Any(p => File.Exists(p.Executable)) ? "configured_not_run" : "profile_path_missing";
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
                { launchStatus = "inspection_unavailable"; }
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            { status = scopeAvailable ? "missing" : "unknown"; reason = scopeAvailable ? "源范围可读，登记目录不存在；不删除任何记录或文件。" : "源范围不可读，不能认作丢失。"; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
            { status = "unknown"; reason = "权限、重解析入口或路径边界未通过；保持Web原状态，不标记为丢失。"; }
            rows.Add(new(installation.Id, installation.Revision, path, status, reason, components, launchStatus,
                executables.DistinctBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray(), Suggest(view, targetRoot, status, items ?? [])));
        }
        var collisions = rows.Where(r => r.Suggestion is { State: "proposed_only", DestinationPath: not null })
            .GroupBy(r => r.Suggestion!.DestinationPath!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).SelectMany(g => g.Select(r => r.InstallationId)).ToHashSet(StringComparer.Ordinal);
        rows = rows.Select(r => collisions.Contains(r.InstallationId) ? r with { Suggestion = new("target_conflict", r.Suggestion!.DestinationPath, "多个版本将指向同一目录；保留各完整安装，不合并，需明确版本名称。") } : r).ToList();
        return new(Guid.NewGuid().ToString("D"), instanceId, deviceId, scope, targetRoot, DateTimeOffset.UtcNow, rows);
    }

    private static LocalOrganizationSuggestion Suggest(LocalInstallationView view, string target, string status, IReadOnlyList<LibraryItemView> items)
    {
        if (status != "present") return new("needs_observation", null, "先核对完整目录，不生成移动建议。");
        if (view.Components.Count != 1) return new("manual_bundle", null, "多组件／完整合集保留外层名称与内部结构，不用第一部作品重命名。");
        var item = items.SingleOrDefault(i => i.Item.Id == view.Components[0].Details.ItemId);
        var title = item?.Item.Details.TitleOverride ?? item?.Metadata?.Novel?.Titles.FirstOrDefault(t => t.Lang == "ja" && t.Main)?.Title
            ?? item?.Metadata?.Novel?.Titles.FirstOrDefault(t => t.Lang == "ja")?.Title;
        if (string.IsNullOrWhiteSpace(title)) return new("manual_title", null, "缺少已确认的日文原名，请先核对关联。");
        try
        {
            var source = LocalFiles.Full(view.Installation.Details.RootPath); string? company = null; var existingCompany = false;
            if (!GameDirectoryNaming.IsCompatibleRoot(target)) return new("manual_target_root", null, "目标根路径含不兼容符号，请在设置中选择兼容目标；不自动改动既有根目录。");
            if (LocalFiles.Within(target, source))
            {
                var relative = Path.GetRelativePath(target, source).Split(Path.DirectorySeparatorChar);
                if (relative.Length != 2) return new("manual_layout", null, "既有层级不是会社／游戏两层，保留结构待人工核对。");
                company = relative[0]; // Preserve established company installation directories even when brand/developer differ.
                existingCompany = true;
                static string Compact(string value) => string.Concat(value.Normalize(System.Text.NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
                var currentName = Compact(OrganizationPlanner.Name(relative[1])); var canonical = Compact(OrganizationPlanner.Name(title));
                var safeCompany = GameDirectoryNaming.IsCompatibleName(company);
                if (!safeCompany) return new("manual_company_name", null, "既有会社目录含不兼容符号；先确认会社整体映射，不逐个游戏擅自更改默认安装目录。");
                if (currentName == canonical && GameDirectoryNaming.IsCompatibleName(relative[1])) return new("already_organized", source, "兼容名与日文原名一致；不会重新加入原文装饰符号，增量保留。");
                if (currentName != canonical && IsReleaseTitle(item?.Metadata, currentName, Compact))
                    return new("preserve_variant", null, "既有名称与该作品的日文发行版本名一致；保留版本差异，不替换为基础作品名。");
                if (currentName != canonical && (currentName.Contains(canonical, StringComparison.Ordinal) ||
                    new[] { "EDITION", "REMAKE", "リメイク", "特典", "予約", "体験", "DL版", "WIN8", "WIN10", "WIN11", "R18版", "コスプレ" }.Any(currentName.Contains))
                    )
                    return new("preserve_variant", null, "既有名称包含版本／特典或附加标题；保留原名，先核对VNDB版本关联，不能截掉差异。");
            }
            company ??= item?.Item.Details.CompanyOverride;
            var group = view.Installation.Details.CompanyGroup;
            if (company is null && group is not null)
                company = group.Length > 1 && group[0] == 'p' && group[1..].All(char.IsAsciiDigit)
                    ? item?.Metadata?.Developers.FirstOrDefault(p => p.Id == group) is { } producer ? producer.Original ?? producer.Name : null : group;
            if (company is null && item?.Metadata?.Developers.Count == 1) company = item.Metadata.Developers[0].Original ?? item.Metadata.Developers[0].Name;
            if (string.IsNullOrWhiteSpace(company)) return new("manual_company", null, "缺少明确归档会社，不从多个开发者中猜测。");
            var destination = LocalFiles.Full(Path.Combine(target, existingCompany ? company : OrganizationPlanner.Name(company), OrganizationPlanner.Name(title)));
            LocalFiles.CheckAncestors(destination);
            if (LocalFiles.Same(source, destination)) return new("already_organized", destination, "符合会社／日文原名布局，增量保留。");
            if (LocalFiles.Exists(destination)) return new("target_conflict", destination, "目标已存在，不覆盖、不合并。");
            return new("proposed_only", destination, "仅增量目录映射建议；未移动。执行前必须重新生成并审阅含完整身份／文件指纹的正式计划。");
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        { return new("manual_layout", null, "名称、路径或重解析边界需人工核对，未生成可执行计划。"); }
    }

    private static bool IsReleaseTitle(MetadataSnapshot? metadata, string current, Func<string, string> compact)
    {
        if (metadata?.Novel?.ReleaseCatalog is not { } catalog) return false;
        foreach (var title in catalog.Items.Where(r => r.Vns.Any(v => v.Id == metadata.VndbId))
            .SelectMany(r => r.Languages).Where(l => l.Lang == "ja" && !string.IsNullOrWhiteSpace(l.Title)))
        {
            try { if (compact(OrganizationPlanner.Name(title.Title!)) == current) return true; }
            catch (ArgumentException) { } // An unusable release title cannot establish a filesystem match.
        }
        return false;
    }
}
