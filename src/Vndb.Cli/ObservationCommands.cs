using System.CommandLine;
using Vndb.Client;
using Vndb.Local;

namespace Vndb.Cli;

public sealed partial class CliApplication
{
    private void AddObservation(RootCommand root)
    {
        var group = new Command("observe", "本机只读元数据检查与候选；不联网、不常驻、不移动或删除游戏。");
        var directory = new Option<string?>("--identity-dir") { Recursive = true }; group.Add(directory);
        var add = new Command("root-add", "明确添加管理根并捕获身份；不自动启用桌面监听，请先退出客户端再修改设置。");
        var path = new Option<string>("--root") { Required = true }; var kind = new Option<string>("--kind") { DefaultValueFactory = _ => "games" };
        var depth = new Option<int>("--depth") { DefaultValueFactory = _ => 2 }; add.Add(path); add.Add(kind); add.Add(depth);
        Bind(add, async (_, parsed, ct) =>
        {
            var settings = new ObservationSettingsStore(parsed.GetValue(directory) ?? ClientSettings.UserDirectory); var value = await settings.ReadAsync(ct);
            var captured = await Task.Run(() => settings.Capture(parsed.GetValue(path)!, parsed.GetValue(kind)!, parsed.GetValue(depth)), ct);
            value = value with { Roots = (value.Roots ?? []).Append(captured).ToArray() }; await settings.SaveAsync(value, ct); return value;
        }); group.Add(add);
        foreach (var name in new[] { "status", "check", "candidates" })
        {
            var command = new Command(name, name == "check" ? "一次性检查，缺失需间隔至少10秒再次核对；只更新本机JSON。" : "只读取本机观测JSON。");
            Bind(command, async (_, parsed, ct) =>
            {
                var folder = parsed.GetValue(directory) ?? ClientSettings.UserDirectory; var settings = await new ObservationSettingsStore(folder).ReadAsync(ct);
                var identity = await LocalDeviceIdentity.LoadOrCreateAsync(folder, ct); var service = new LocalObservation(new(folder), identity.Id);
                if (name == "status") return new { settings, snapshot = await service.ReadAsync(ct) };
                if (name == "candidates") return (object)(await service.ReadAsync(ct)).Candidates;
                return await Task.Run(async () =>
                {
                    var result = await service.CheckAsync(settings, await new LocalLauncher(folder).ReadAsync(ct), ct: ct);
                    return new { result.Snapshot, result.Metrics, result.Degraded, result.Deferred, result.CheckedAt }; // Local paths are explicit CLI output, never sent to Web.
                }, ct);
            }); group.Add(command);
        }
        root.Add(group);
    }
}
