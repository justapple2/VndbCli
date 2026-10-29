using System.CommandLine;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Local;

namespace Vndb.Cli;

public sealed partial class CliApplication
{
    private void AddOrganization(RootCommand root)
    {
        var group = new Command("organize", "规划／同卷直接整理与完整复原；执行需要明确批准摘要及本机JSON回执。");
        var identityDir = new Option<string?>("--identity-dir") { Recursive = true, Description = "稳定设备身份所在目录。" }; group.Add(identityDir);
        var audit = new Command("audit", "只读核对本机已登记安装／组件与启动配置，输出仅供本机保存的JSON；不启动、不移动文件。");
        var auditScope = new Option<string>("--scope") { Required = true };
        var auditTarget = new Option<string>("--target-root") { Required = true };
        var launchDirectory = new Option<string?>("--launch-dir") { Description = "已有launch-profiles.json目录；不自动创建启动配置。" };
        audit.Add(auditScope); audit.Add(auditTarget); audit.Add(launchDirectory);
        Bind(audit, async (_, parsed, ct) =>
        {
            CheckOrganizationOutput(parsed, parsed.GetValue(auditScope)!);
            var workspace = await LocalOrganizationWorkspace.OpenAsync(parsed.GetValue(identityDir), ct);
            return await workspace.AuditAsync(parsed.GetValue(auditScope)!, parsed.GetValue(auditTarget)!, parsed.GetValue(launchDirectory), ct);
        }); group.Add(audit);
        var scan = new Command("scan", "只读发现明确范围内的目录候选；已知完整合集不拆分，未知EXE只作线索。");
        var scanScope = new Argument<string>("scope"); var scanDepth = new Option<int>("--depth") { DefaultValueFactory = _ => 3 };
        var knownJson = new Option<string?>("--known-json") { Description = "离线安装快照数组，可空；默认读取本机JSON安装映射。" };
        scan.Add(scanScope); scan.Add(scanDepth); scan.Add(knownJson);
        Bind(scan, async (_, parsed, ct) =>
        {
            var scopePath = parsed.GetValue(scanScope)!; CheckOrganizationOutput(parsed, scopePath);
            return await LocalOrganizationWorkspace.ScanAsync(parsed.GetValue(identityDir) ?? ClientSettings.UserDirectory, scopePath, parsed.GetValue(scanDepth), parsed.GetValue(knownJson), ct);
        }); group.Add(scan);
        var inspect = new Command("inspect", "只读提取根目录PE、配置／说明、注册表与快捷方式线索，不运行文件。");
        var inspectPath = new Argument<string>("directory"); var query = new Option<string?>("--query") { Description = "通过共享查询路由取得候选，结果不会自动作为会社／游戏关联。" };
        inspect.Add(inspectPath); inspect.Add(query);
        Bind(inspect, async (core, parsed, ct) =>
        {
            var path = parsed.GetValue(inspectPath)!; CheckOrganizationOutput(parsed, path);
            var clues = LocalFiles.Inspect(path); MetadataResponse? candidates = null;
            if (parsed.GetValue(query) is { Length: > 0 } q)
            {
                var scope = QueryScope(parsed);
                using var http = CreateHttpClient(); var uri = scope.Origin is null ? null : ClientSettings.ServerUri(scope.Origin);
                var web = uri is null ? null : new WebLibraryClient(http, uri, Env("VN_LIBRARY_KEY"), TimeSpan.FromSeconds(parsed.GetValue(_timeout)));
                var cache = new JsonSnapshotStore(scope.DirectoryPath, scope.Origin);
                candidates = await new QueryRouter(core, cache, web).QueryAsync(new("vn_search", new(q)), parsed.GetValue(_source)!, ct);
            }
            return new { directory = path, clues, candidates };
        }); group.Add(inspect);
        var get = new Command("installation", "从本机JSON读取安装及全部组件，可用 --output 保存离线规划快照。"); var id = new Argument<string>("id"); get.Add(id);
        Bind(get, async (_, parsed, ct) => (await new LocalLibraryStore(parsed.GetValue(identityDir) ?? ClientSettings.UserDirectory).ReadAsync(ct)).Installations.Single(i => i.Installation.Id == parsed.GetValue(id))); group.Add(get);
        var plan = new Command("plan", "只读生成一个完整物理安装的计划；多组件／特典保留内部结构。");
        var installationId = new Option<string?>("--installation"); var snapshot = new Option<string?>("--installation-json");
        var scope = new Option<string>("--scope") { Required = true }; var target = new Option<string>("--target-root") { Required = true };
        var company = new Option<string?>("--company"); var title = new Option<string?>("--title");
        foreach (var option in new Option[] { installationId, snapshot, scope, target, company, title }) plan.Add(option);
        Bind(plan, async (_, parsed, ct) =>
        {
            var workspace = await LocalOrganizationWorkspace.OpenAsync(parsed.GetValue(identityDir), ct);
            var result = await workspace.PlanAsync(parsed.GetValue(installationId), parsed.GetValue(snapshot), parsed.GetValue(scope)!, parsed.GetValue(target)!, parsed.GetValue(company), parsed.GetValue(title), ct);
            CheckOrganizationOutput(parsed, result.SourcePath, result.DestinationPath);
            await error.WriteLineAsync("计划SHA256（审批使用）：" + ArchiveFormat.Hash(result)); return result;
        }); group.Add(plan);
        var execute = new Command("execute", "人工批准预览摘要后，保存本机计划回执并按同卷rename执行。");
        var planFile = new Argument<string>("plan-json"); var approval = new Option<string>("--approve") { Required = true }; var runDir = new Option<string?>("--run-directory");
        execute.Add(planFile); execute.Add(approval); execute.Add(runDir);
        Bind(execute, async (_, parsed, ct) =>
        {
            CheckOrganizationOutput(parsed);
            var workspace = await LocalOrganizationWorkspace.OpenAsync(parsed.GetValue(identityDir), ct);
            var value = RunJournal.Read<LocalArchivePlan>(parsed.GetValue(planFile)!);
            return await workspace.Runner().ExecuteAsync(value, parsed.GetValue(approval)!, parsed.GetValue(runDir) ?? workspace.RunDirectory(value.Id), ct);
        }); group.Add(execute);
        var cancel = new Command("cancel", "核对原目录身份仍在、目标未出现后安全撤销未移动的计划，不删除文件。");
        var cancelDirectory = new Option<string>("--run-directory") { Required = true }; var cancelApproval = new Option<string>("--approve") { Required = true };
        cancel.Add(cancelDirectory); cancel.Add(cancelApproval);
        Bind(cancel, async (_, parsed, ct) =>
        {
            CheckOrganizationOutput(parsed);
            var workspace = await LocalOrganizationWorkspace.OpenAsync(parsed.GetValue(identityDir), ct);
            return await workspace.Runner().CancelAsync(parsed.GetValue(cancelDirectory)!, parsed.GetValue(cancelApproval)!, ct);
        }); group.Add(cancel);
        foreach (var commandName in new[] { "resume", "restore-plan", "restore" })
        {
            var command = new Command(commandName, commandName == "resume" ? "验证原意图并安全续跑／同步；不重新移动已完成目录。" : commandName == "restore-plan" ? "对当前完整容器生成复原预览，允许保留游玩后新增的存档。" : "明确批准复原预览后完整回迁，不回退个人／VNDB关联。");
            var directory = new Option<string>("--run-directory") { Required = true }; command.Add(directory);
            var restoreFile = new Option<string?>("--plan-json"); var restoreApproval = new Option<string?>("--approve");
            if (commandName == "restore") { command.Add(restoreFile); command.Add(restoreApproval); }
            Bind(command, async (_, parsed, ct) =>
            {
                if (commandName != "restore-plan") CheckOrganizationOutput(parsed);
                var workspace = await LocalOrganizationWorkspace.OpenAsync(parsed.GetValue(identityDir), ct);
                var runner = workspace.Runner();
                if (commandName == "resume") return await runner.ResumeAsync(parsed.GetValue(directory)!, ct);
                if (commandName == "restore-plan")
                {
                    var result = await runner.PreviewRestoreAsync(parsed.GetValue(directory)!, ct); CheckOrganizationOutput(parsed, result.SourcePath, result.DestinationPath);
                    await error.WriteLineAsync("复原SHA256（审批使用）：" + ArchiveFormat.Hash(result)); return result;
                }
                return await runner.RestoreAsync(RunJournal.Read<LocalRestorePlan>(parsed.GetValue(restoreFile) ?? throw new ArgumentException("需要 --plan-json。")), parsed.GetValue(restoreApproval) ?? throw new ArgumentException("需要 --approve。"), parsed.GetValue(directory)!, ct);
            }); group.Add(command);
        }
        root.Add(group);
        AddRegistration(group, identityDir);
    }
    private void CheckOrganizationOutput(ParseResult parsed, params string[] roots)
    {
        if (parsed.GetValue(_outputFile) is not { } file) return;
        if (roots.Length == 0) throw new ArgumentException("执行／续跑／复原结果请使用 --json 标准输出；恢复证据会持久保存，不支持另设 --output。");
        if (roots.Any(root => LocalFiles.Within(root, Path.GetFullPath(file)) || LocalFiles.Same(root, Path.GetFullPath(file)))) throw new ArgumentException("管理输出不能写入游戏物理目录。");
    }
}
