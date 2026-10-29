using System.CommandLine;
using Vndb.Local;

namespace Vndb.Cli;

public sealed partial class CliApplication
{
    private void AddQueryData(RootCommand root)
    {
        var group = new Command("query-data", "本机查询作用域诊断及明确批准的旧JSON迁移；不访问Web或游戏目录。");
        var status = new Command("status", "显示实际数据根、作用域和旧资料待核对数量。");
        Bind(status, async (_, parsed, ct) => await QueryScope(parsed).StatusAsync(ct)); group.Add(status);
        var preview = new Command("preview", "仅预览可证明归属的旧查询JSON，未知pending留原处隔离。");
        var source = new Option<string?>("--legacy-directory") { Description = "明确的旧查询目录；缺省检查旧snapshots／cache及覆盖根。" }; preview.Add(source);
        Bind(preview, async (_, parsed, ct) =>
        {
            var result = await new QueryDataMigration(QueryScope(parsed)).PreviewAsync(parsed.GetValue(source) is { } path ? [path] : null, ct);
            await error.WriteLineAsync("查询迁移批准SHA256：" + LocalArchiveFormat.Hash(result)); return result;
        }); group.Add(preview);
        var apply = new Command("apply", "按批准摘要复制已证明来源文件并保存迁移意图／清单；不删除或覆盖原件。");
        var file = new Option<string>("--preview-json") { Required = true }; var approval = new Option<string>("--approve") { Required = true }; apply.Add(file); apply.Add(approval);
        Bind(apply, async (_, parsed, ct) =>
        {
            var scope = QueryScope(parsed);
            await new QueryDataMigration(scope).ApplyAsync(RunJournal.Read<QueryMigrationPreview>(parsed.GetValue(file)!), parsed.GetValue(approval)!, ct);
            return await scope.StatusAsync(ct);
        }); group.Add(apply); root.Add(group);
    }
}
