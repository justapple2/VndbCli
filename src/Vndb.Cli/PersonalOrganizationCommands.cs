using System.CommandLine;
using Vndb.Client;
using Vndb.Core;
using Vndb.Local;

namespace Vndb.Cli;

public sealed partial class CliApplication
{
    private void AddPersonalOrganization(RootCommand root)
    {
        var group = new Command("personal", "收藏／虚拟集合／标签；JSON原稿与Web修订同步，不改动游戏目录。");
        foreach (var name in new[] { "status", "queue", "pull", "sync" })
        {
            var command = new Command(name, name switch { "status" => "读取本机已确认分类和离线稿。", "queue" => "登记明确的分类变更JSON（需expected_revision），不联网。", "pull" => "读取Web；未确认原操作不丢弃。", _ => "按原ID同步离线稿。" });
            var file = new Option<string>("--change-json") { Required = true }; var resolve = new Option<bool>("--resolve-conflict");
            if (name == "queue") command.Add(file); if (name == "pull") command.Add(resolve);
            Bind(command, async (_, parsed, ct) =>
            {
                var scope = QueryScope(parsed); var store = new PersonalOrganizationStore(scope.DirectoryPath, scope.Origin);
                if (name == "queue") { await store.EnqueueAsync(RunJournal.Read<PersonalOrganizationChange>(parsed.GetValue(file)!), ct); return await store.ReadAsync(ct); }
                if (name == "status") return await store.ReadAsync(ct);
                var uri = ClientSettings.ServerUri(scope.Origin ?? throw new ArgumentException("需要明确Web地址。"));
                using var http = CreateHttpClient(); var api = new WebLibraryClient(http, uri, Env("VN_LIBRARY_KEY"), TimeSpan.FromSeconds(parsed.GetValue(_timeout)));
                if (name == "pull") return await store.RefreshAsync(api, parsed.GetValue(resolve), ct);
                await store.SyncAsync(api, ct); return await store.ReadAsync(ct);
            }); group.Add(command);
        }
        root.Add(group);
    }
    private void AddResources(RootCommand root)
    {
        var group = new Command("resources", "本机资源描述档案；不上传位置，不解压、应用或执行文件。");
        var directory = new Option<string?>("--identity-dir") { Recursive = true }; group.Add(directory);
        foreach (var name in new[] { "list", "save", "check", "hash" })
        {
            var command = new Command(name, name == "hash" ? "仅对指定文件按需计算SHA256；不自动证明版本或应用状态。" : "读取／登记本机资源资料，不操作游戏文件。");
            var item = new Option<string?>("--item"); var id = new Option<string?>("--id"); var revision = new Option<long?>("--expected-revision"); var file = new Option<string>("--record-json") { Required = true };
            command.Add(id); command.Add(revision); if (name == "list") command.Add(item); if (name == "save") command.Add(file);
            Bind(command, async (_, parsed, ct) =>
            {
                var folder = parsed.GetValue(directory) ?? ClientSettings.UserDirectory; var identity = await LocalDeviceIdentity.LoadOrCreateAsync(folder, ct);
                var archive = new LocalResourceArchive(new(folder), identity.Id); var rows = await archive.ReadAsync(ct: ct);
                CheckPackageOutput(parsed, rows.Select(r => r.Details.RootPath).Distinct().ToArray());
                if (name == "list") return rows.Where(r => parsed.GetValue(item) is not { } selected || r.Details.ItemId == selected).ToArray();
                if (name == "save") { var data = RunJournal.Read<LocalResourceData>(parsed.GetValue(file)!); CheckPackageOutput(parsed, data.RootPath); return await archive.SaveAsync(data, parsed.GetValue(id), parsed.GetValue(revision), ct); }
                var row = rows.Single(r => r.Id == (parsed.GetValue(id) ?? throw new ArgumentException("需要--id。")));
                if (name == "check") return LocalResourceArchive.Inspect(row);
                return await archive.HashAsync(row.Id, parsed.GetValue(revision) ?? throw new ArgumentException("校验需要--expected-revision。"), ct);
            }); group.Add(command);
        }
        root.Add(group);
    }
}
