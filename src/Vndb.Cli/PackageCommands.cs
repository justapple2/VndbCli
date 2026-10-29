using System.CommandLine;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Local;

namespace Vndb.Cli;

public sealed partial class CliApplication
{
    private void AddPackages(Command group, Option<string?> identityDirectory)
    {
        var scan = new Command("package-scan", "只读扫描安装包／镜像文件名；不解压、不自动匹配作品，不创建启动配置。");
        var scope = new Argument<string>("scope"); var depth = new Option<int>("--depth") { DefaultValueFactory = _ => 3 };
        scan.Add(scope); scan.Add(depth);
        Bind(scan, async (_, parsed, ct) =>
        {
            CheckPackageOutput(parsed, parsed.GetValue(scope)!);
            return await Task.Run(() => OwnedPackages.Scan(parsed.GetValue(scope)!, parsed.GetValue(depth), ct), ct);
        }); group.Add(scan);

        var match = new Command("package-query", "使用所选包文件名查询作品候选；默认Web优先，只发送查询标题，不自动确认关联。");
        var matchRoot = new Argument<string>("scope"); var matchRelative = new Option<string>("--relative-path") { Required = true };
        var query = new Option<string?>("--query") { Description = "人工修正文件名产生的查询标题。" };
        match.Add(matchRoot); match.Add(matchRelative); match.Add(query);
        Bind(match, async (core, parsed, ct) =>
        {
            var candidate = OwnedPackages.Candidate(parsed.GetValue(matchRoot)!, parsed.GetValue(matchRelative)!);
            CheckPackageOutput(parsed, candidate.ScanRoot);
            var scope = QueryScope(parsed);
            using var http = CreateHttpClient(); var uri = scope.Origin is null ? null : ClientSettings.ServerUri(scope.Origin);
            var web = uri is null ? null : new WebLibraryClient(http, uri, Env("VN_LIBRARY_KEY"), TimeSpan.FromSeconds(parsed.GetValue(_timeout)));
            var router = new QueryRouter(core, new JsonSnapshotStore(scope.DirectoryPath, scope.Origin), web);
            var result = await router.QueryAsync(new("vn_search", new(parsed.GetValue(query) ?? candidate.QueryTitle)), parsed.GetValue(_source)!, ct);
            return new { candidate, matches = result };
        }); group.Add(match);

        var register = new Command("package-register", "明确关联一个安装包到作品；缺省预览，--confirm后仅写本机JSON。目录永不上传Web。");
        var root = new Option<string>("--scan-root") { Required = true };
        var relative = new Option<string>("--relative-path") { Required = true };
        var item = new Option<string>("--item-json") { Required = true, Description = "明确选定的单条metadata或个人书架条目JSON；查询结果多条须先选出一条。" };
        var confirm = new Option<bool>("--confirm");
        foreach (var option in new Option[] { root, relative, item, confirm }) register.Add(option);
        Bind(register, async (_, parsed, ct) =>
        {
            CheckPackageOutput(parsed, parsed.GetValue(root)!);
            var directory = parsed.GetValue(identityDirectory) ?? ClientSettings.UserDirectory;
            var device = await LocalDeviceIdentity.LoadOrCreateAsync(directory, ct);
            var choice = RegistrationSelection.FromJson(await RegistrationSelection.ReadJsonAsync(parsed.GetValue(item)!, ct));
            var config = settings ?? ClientSettings.Load(parsed.GetValue(_config));
            var origin = parsed.GetValue(_server) ?? Env("VN_LIBRARY_URL") ?? config.Server;
            var service = new OwnedPackages(new(directory), device);
            var preview = await service.PreviewAsync(parsed.GetValue(root)!, parsed.GetValue(relative)!, choice, origin, ct);
            await error.WriteLineAsync("安装包登记预览SHA256：" + LocalArchiveFormat.Hash(preview));
            return parsed.GetValue(confirm) ? (object)await service.CommitAsync(preview, LocalArchiveFormat.Hash(preview), ct) : preview;
        }); group.Add(register);

        var list = new Command("packages", "读取本机安装包清单；包含相对目录，仅供本地保存。");
        Bind(list, async (_, parsed, ct) =>
        {
            var packages = (await new LocalLibraryStore(parsed.GetValue(identityDirectory) ?? ClientSettings.UserDirectory).ReadAsync(ct)).Packages ?? [];
            CheckPackageOutput(parsed, packages.Select(p => p.ScanRoot).ToArray()); return packages;
        });
        group.Add(list);
        var verify = new Command("package-verify", "明确核对已登记安装包：断开根目录为未知，不误报丢失；不读取压缩内容。");
        Bind(verify, async (_, parsed, ct) =>
        {
            var directory = parsed.GetValue(identityDirectory) ?? ClientSettings.UserDirectory;
            var local = new LocalLibraryStore(directory);
            CheckPackageOutput(parsed, ((await local.ReadAsync(ct)).Packages ?? []).Select(p => p.ScanRoot).ToArray());
            var service = new OwnedPackages(local, await LocalDeviceIdentity.LoadOrCreateAsync(directory, ct));
            return (await service.VerifyAsync(ct)).Packages ?? [];
        }); group.Add(verify);
    }
    private void CheckPackageOutput(ParseResult parsed, params string[] roots)
    {
        if (parsed.GetValue(_outputFile) is not { } file) return;
        var output = Path.GetFullPath(file); var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var source in roots)
        {
            var root = OwnedPackages.Root(source);
            if (output.Equals(root, comparison) || output.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, comparison))
                throw new ArgumentException("查询或登记输出不能写入安装包源范围；请另选本机工作目录。");
        }
    }
}
