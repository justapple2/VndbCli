using System.Globalization;
using System.Text;
using Vndb.Core;
using Vndb.Contracts;

namespace Vndb.Cli;

internal static class TextOutput
{
    public static string Format(object data)
    {
        var text = new StringBuilder();
        switch (data)
        {
            case MetadataResponse response:
                foreach (var item in response.Items)
                {
                    if (item.Novel is { } vn) AppendNovel(text, vn);
                    else if (item.Producer is { } p) AppendProducer(text, p, details: true);
                    text.AppendLine();
                }
                AppendPage(text, response.Items.Count, response.Page, response.PageSize, response.More);
                text.AppendLine($"来源：{response.Source}；{(response.PendingImport ? "已保存本机待导入 JSON（尚未同步 Web）" : "无待同步资料")}");
                if (response.Partial) text.AppendLine("当前为本地资料子集，不能视为 VNDB 完整检索结果。");
                break;
            case QueryPage<VisualNovelSummary> page:
                foreach (var novel in page.Results)
                {
                    AppendNovel(text, novel);
                    text.AppendLine();
                }
                AppendPage(text, page.Results.Count, page.Page, page.PageSize, page.More);
                break;
            case VisualNovel novel:
                AppendNovel(text, novel);
                break;
            case QueryPage<Producer> page:
                foreach (var producer in page.Results)
                {
                    AppendProducer(text, producer, details: false);
                    text.AppendLine();
                }
                AppendPage(text, page.Results.Count, page.Page, page.PageSize, page.More);
                break;
            case Producer producer:
                AppendProducer(text, producer, details: true);
                break;
            case AuthInfo auth:
                text.AppendLine($"用户：{Safe(auth.Username)} ({Safe(auth.Id)})");
                text.AppendLine($"令牌权限：{(auth.Permissions.Count == 0 ? "无" : Join(auth.Permissions))}");
                break;
            case DatabaseStats stats:
                text.AppendLine($"视觉小说：{stats.Vn}");
                text.AppendLine($"会社/开发者：{stats.Producers}");
                text.AppendLine($"发行版本：{stats.Releases}");
                text.AppendLine($"角色：{stats.Chars}；人员：{stats.Staff}；标签：{stats.Tags}；特征：{stats.Traits}");
                break;
            default:
                return System.Text.Json.JsonSerializer.Serialize(data, data.GetType(), VndbJson.Options);
        }
        return text.ToString().TrimEnd();
    }

    private static void AppendNovel(StringBuilder text, VisualNovelSummary novel)
    {
        text.AppendLine($"{Safe(novel.Id)}  {Safe(novel.Title)}");
        if (!string.IsNullOrWhiteSpace(novel.AltTitle)) text.AppendLine($"原文标题：{Safe(novel.AltTitle)}");
        text.AppendLine($"开发会社：{(novel.Developers.Count == 0 ? "未知" : string.Join(" / ", novel.Developers.Select(ProducerLabel)))}");
        text.AppendLine($"发行日期：{Safe(novel.Released)}；原始语言：{Safe(novel.OriginalLanguage)}");
        text.AppendLine($"链接：https://vndb.org/{Safe(novel.Id)}");
        if (novel is not VisualNovel detail) return;

        foreach (var title in detail.Titles)
        {
            text.AppendLine($"标题 [{Safe(title.Lang)}{(title.Main ? ", 原始" : "")}]{(title.Official ? "" : " [非官方]")}：{Safe(title.Title)}");
            if (!string.IsNullOrWhiteSpace(title.Latin)) text.AppendLine($"  罗马字：{Safe(title.Latin)}");
        }
        if (detail.Aliases.Count > 0) text.AppendLine($"别名：{Join(detail.Aliases)}");
        text.AppendLine($"已收录语言（不含机翻）：{Join(detail.Languages.Select(ReleaseRules.Language).ToArray())}");
        if (detail.ReleaseCatalog is { } catalog)
        {
            text.AppendLine($"发行目录：{catalog.Items.Count} 项，查询于 {catalog.FetchedAt:O}（不代表本机安装版本）");
            foreach (var release in catalog.Items) text.AppendLine(Safe(ReleaseRules.Describe(release, detail.Id)));
        }
        else text.AppendLine("发行目录：尚未查询，可使用 vn releases <id>。");
        text.AppendLine($"平台：{Join(detail.Platforms)}");
        text.AppendLine($"评分：{(detail.Rating is { } rating ? rating.ToString("0.##", CultureInfo.InvariantCulture) + "/100" : "未知")}；票数：{detail.VoteCount?.ToString(CultureInfo.InvariantCulture) ?? "未知"}");
        text.AppendLine($"平均时长：{(detail.LengthMinutes is { } minutes ? minutes + " 分钟" : "未知")}");
        if (novel.Developers.Count > 1) text.AppendLine("此作品有多家开发会社，列表顺序不表示归档优先级。");
        if (!string.IsNullOrWhiteSpace(detail.Description)) text.AppendLine($"简介（VNDB 原始格式）：\n{Safe(detail.Description)}");
    }

    private static void AppendProducer(StringBuilder text, Producer producer, bool details)
    {
        text.AppendLine(ProducerLabel(producer));
        var kind = producer.Type switch { "co" => "会社", "ng" => "同人/业余团体", "in" => "个人", _ => "未知" };
        text.AppendLine($"类型：{kind}；语言：{Safe(producer.Lang)}");
        if (producer.Aliases.Count > 0) text.AppendLine($"别名：{Join(producer.Aliases)}");
        text.AppendLine($"链接：https://vndb.org/{Safe(producer.Id)}");
        if (details && !string.IsNullOrWhiteSpace(producer.Description)) text.AppendLine($"简介（VNDB 原始格式）：\n{Safe(producer.Description)}");
    }

    private static void AppendPage(StringBuilder text, int count, int page, int pageSize, bool more)
    {
        text.AppendLine($"第 {page} 页，本页 {count} 条，每页上限 {pageSize} 条。");
        text.AppendLine(more ? $"还有下一页，使用 --page {(long)page + 1} 继续查询。" : "没有更多结果。");
    }

    private static string ProducerLabel(ProducerSummary p) =>
        $"{Safe(p.Name)}{(string.IsNullOrWhiteSpace(p.Original) || p.Original == p.Name ? "" : $" / {Safe(p.Original)}")} ({Safe(p.Id)})";

    private static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "未知" : string.Join(", ", values.Select(Safe));
    private static string Safe(string? value) => value is null ? "未知" : string.Concat(value.Where(c => !char.IsControl(c) || c is '\n' or '\t'));
}
