using System.CommandLine;
using Vndb.Client;
using Vndb.Local;

namespace Vndb.Cli;

public sealed partial class CliApplication
{
    private void AddLocalData(RootCommand root)
    {
        var group = new Command("local-data", "本机登记JSON索引的明确迁移；不联网、不访问游戏目录。");
        var directory = new Option<string?>("--identity-dir") { Recursive = true, Description = "本机数据根；默认用户文档目录。" }; group.Add(directory);
        var preview = new Command("index-preview", "预览拆分大小与原件摘要；不改动登记。");
        Bind(preview, async (_, parsed, ct) =>
        {
            var result = await new LocalLibraryStore(parsed.GetValue(directory) ?? ClientSettings.UserDirectory).PreviewIndexAsync(ct);
            await error.WriteLineAsync("索引迁移批准SHA256：" + LocalArchiveFormat.Hash(result)); return result;
        }); group.Add(preview);
        var apply = new Command("index-apply", "按批准的预览启用v2索引；保留旧登记原件，不删除缓存。");
        var file = new Option<string>("--preview-json") { Required = true }; var approval = new Option<string>("--approve") { Required = true }; apply.Add(file); apply.Add(approval);
        Bind(apply, async (_, parsed, ct) =>
        {
            var store = new LocalLibraryStore(parsed.GetValue(directory) ?? ClientSettings.UserDirectory);
            await store.ApplyIndexAsync(RunJournal.Read<LocalIndexPreview>(parsed.GetValue(file)!), parsed.GetValue(approval)!, ct);
            var summary = await store.ReadSummaryAsync(ct); return new { format_version = 2, items = summary.Items.Count, installations = summary.Installations.Count };
        }); group.Add(apply); root.Add(group);
    }
}
