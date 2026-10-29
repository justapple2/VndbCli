using System.CommandLine;
using Vndb.Client;
using Vndb.Local;

namespace Vndb.Cli;

public sealed partial class CliApplication
{
    private void AddPresence(RootCommand root)
    {
        var group = new Command("presence", "存在布尔值同步、状态和明确拒绝后的恢复；不扫描游戏。");
        var identityDir = new Option<string?>("--identity-dir") { Recursive = true }; group.Add(identityDir);
        foreach (var name in new[] { "status", "retry", "recheck", "apply" })
        {
            var command = new Command(name, name switch { "status" => "读取本机待处理状态。", "retry" => "按原批次重试或提交当前观测。", "recheck" => "预览已明确拒绝批次的替代意图。", _ => "明确批准预览摘要后封存拒绝原件；不自动提交替代批次。" });
            var preview = new Option<string?>("--preview-json"); var approve = new Option<string?>("--approve");
            if (name == "apply") { command.Add(preview); command.Add(approve); }
            Bind(command, async (_, parsed, ct) =>
            {
                var directory = parsed.GetValue(identityDir) ?? ClientSettings.UserDirectory;
                var device = await LocalDeviceIdentity.LoadOrCreateAsync(directory, ct);
                var config = settings ?? ClientSettings.Load(parsed.GetValue(_config));
                var origin = ClientSettings.ServerUri(parsed.GetValue(_server) ?? Env("VN_LIBRARY_URL") ?? config.Server ?? throw new ArgumentException("需要明确的Web地址。"));
                var service = new PresenceSync(new(directory));
                if (name == "status") return await service.StateAsync(origin.AbsoluteUri, device.Id, ct);
                using var http = CreateHttpClient(); var api = new WebLibraryClient(http, origin, Env("VN_LIBRARY_KEY"), TimeSpan.FromSeconds(parsed.GetValue(_timeout)));
                if (name == "retry") { await service.SubmitAsync(api, origin.AbsoluteUri, device.Id, ct); return await service.StateAsync(origin.AbsoluteUri, device.Id, ct); }
                if (name == "recheck")
                {
                    var result = await service.PreviewRecheckAsync(api, origin.AbsoluteUri, device.Id, ct);
                    await error.WriteLineAsync("重新核对批准SHA256：" + LocalArchiveFormat.Hash(result)); return result;
                }
                var plan = RunJournal.Read<PresenceRecheckPreview>(parsed.GetValue(preview) ?? throw new ArgumentException("需要--preview-json。"));
                if (plan.Origin != origin.AbsoluteUri || plan.DeviceId != device.Id) throw new ArgumentException("预览绑定其他服务或设备。");
                await service.ApplyRecheckAsync(api, plan, parsed.GetValue(approve) ?? throw new ArgumentException("需要--approve。"), ct);
                return await service.StateAsync(origin.AbsoluteUri, device.Id, ct);
            }); group.Add(command);
        }
        root.Add(group);
    }
}
