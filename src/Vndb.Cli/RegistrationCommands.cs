using System.CommandLine;
using System.Text.Json;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Local;

namespace Vndb.Cli;

public sealed partial class CliApplication
{
    private void AddRegistration(Command group, Option<string?> identityDirectory)
    {
        AddPackages(group, identityDirectory);
        var register = new Command("register", "预览／明确确认本机完整安装关联；不移动、不启动游戏，离线可用。");
        var directory = new Option<string>("--directory") { Required = true, Description = "完整物理容器根目录。" };
        var title = new Option<string>("--title") { Required = true, Description = "完整游戏或合集名称。" };
        var company = new Option<string?>("--company");
        var itemJson = new Option<string?>("--item-json") { Description = "明确选定的Web条目或单条metadata／查询导出JSON。" };
        var components = new Option<string?>("--components-json") { Description = "合集的RegistrationComponent数组；不能与--item-json同时使用。" };
        var relative = new Option<string>("--relative-path") { DefaultValueFactory = _ => "." };
        var kind = new Option<string>("--kind") { DefaultValueFactory = _ => "game" };
        var confirm = new Option<bool>("--confirm") { Description = "确认以上明确输入并登记；缺省只预览。" };
        foreach (var option in new Option[] { directory, title, company, itemJson, components, relative, kind, confirm }) register.Add(option);
        Bind(register, async (_, parsed, ct) =>
        {
            var root = parsed.GetValue(directory)!; CheckOrganizationOutput(parsed, root);
            if (parsed.GetValue(itemJson) is not null && parsed.GetValue(components) is not null) throw new ArgumentException("请选择单作品JSON或合集组件JSON之一。");
            RegistrationComponent[] selections = parsed.GetValue(components) is { } file ?
                (await RegistrationSelection.ReadJsonAsync(file, ct)).Deserialize<RegistrationComponent[]>(Vndb.Core.VndbJson.Options) ?? throw new ArgumentException("合集组件JSON为空。") :
                [new(parsed.GetValue(itemJson) is { } single ? RegistrationSelection.FromJson(await RegistrationSelection.ReadJsonAsync(single, ct)) : new(parsed.GetValue(title)!, Company: parsed.GetValue(company)), parsed.GetValue(relative)!, parsed.GetValue(kind)!)];
            var localRoot = parsed.GetValue(identityDirectory) ?? ClientSettings.UserDirectory;
            var device = await LocalDeviceIdentity.LoadOrCreateAsync(localRoot, ct);
            var config = settings ?? ClientSettings.Load(parsed.GetValue(_config));
            var origin = parsed.GetValue(_server) ?? Env("VN_LIBRARY_URL") ?? config.Server;
            var service = new LocalInstallationRegistration(new(localRoot), device);
            var preview = await service.PreviewAsync(new(root, parsed.GetValue(title)!, selections, parsed.GetValue(company), origin), ct);
            await error.WriteLineAsync("登记预览SHA256：" + LocalArchiveFormat.Hash(preview));
            return parsed.GetValue(confirm) ? (object)await service.CommitAsync(preview, LocalArchiveFormat.Hash(preview), ct) : preview;
        }); group.Add(register);

        var sync = new Command("registration-sync", "通过原Web API同步公开条目，再同步存在布尔值；目录／组件不上传。");
        Bind(sync, async (_, parsed, ct) =>
        {
            CheckOrganizationOutput(parsed);
            var root = parsed.GetValue(identityDirectory) ?? ClientSettings.UserDirectory;
            var identity = await LocalDeviceIdentity.LoadOrCreateAsync(root, ct);
            var config = settings ?? ClientSettings.Load(parsed.GetValue(_config));
            var server = parsed.GetValue(_server) ?? Env("VN_LIBRARY_URL") ?? config.Server ?? throw new ArgumentException("同步登记需要明确Web地址。");
            var uri = ClientSettings.ServerUri(server); using var http = CreateHttpClient();
            var api = new WebLibraryClient(http, uri, Env("VN_LIBRARY_KEY"), TimeSpan.FromSeconds(parsed.GetValue(_timeout)));
            var local = new LocalLibraryStore(root);
            var count = await new LocalRegistrationSync(local).SyncAsync(api, uri.AbsoluteUri, identity.Id, ct);
            var presence = await new PresenceSync(local).SubmitAsync(api, uri.AbsoluteUri, identity.Id, ct);
            return new { items = count, presence };
        }); group.Add(sync);
        var export = new Command("registration-export", "导出指定待登记作品的公开活动JSON，可由Web导入，原稿保留。");
        var itemId = new Argument<string>("local-item-id"); export.Add(itemId);
        Bind(export, async (_, parsed, ct) =>
        {
            var local = new LocalLibraryStore(parsed.GetValue(identityDirectory) ?? ClientSettings.UserDirectory);
            CheckOrganizationOutput(parsed, (await local.ReadAsync(ct)).Installations.Select(i => i.Installation.Details.RootPath).ToArray());
            return await new LocalRegistrationSync(local).ExportAsync(parsed.GetValue(itemId)!, ct);
        }); group.Add(export);
    }
}
