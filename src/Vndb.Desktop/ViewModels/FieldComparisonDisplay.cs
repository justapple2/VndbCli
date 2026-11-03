using Vndb.Core;

namespace Vndb.Desktop.ViewModels;

/// <summary>Presentation only; full values remain folded for long personal text. Never a diagnostic export model.</summary>
public sealed record FieldComparisonDisplay(FieldDifference Difference)
{
    public string Label => Difference.Key switch
    {
        "title" => "记录标题", "novel_id" => "作品关联", "item_id" => "个人条目归属", "item_novel_id" => "书架作品关联", "status" => "游玩状态", "started_on" => "开始日期", "finished_on" => "结束日期",
        "duration_minutes" => "手工时长", "score" => "个人评分", "review" => "个人评价", "notes" => "备注一", "extra_notes" => "备注二", "language" => "游玩语言",
        var key when key.StartsWith("label:", StringComparison.Ordinal) => "分类 " + key.Split(':')[1] + (key.EndsWith(":name", StringComparison.Ordinal) ? " · 名称" : " · 类型"),
        var key when key.StartsWith("item:", StringComparison.Ordinal) => "条目 " + key.Split(':')[1] + (key.EndsWith(":favorite", StringComparison.Ordinal) ? " · 收藏" : " · 分类"), _ => Difference.Key
    };
    public string State => !Difference.Original.Known ? "基准未知，请人工核对" : Difference.Conflict ? "双方修改不同，需人工核对" : Difference.RemoteChanged ? "Web已改变" : "本机修改";
    public string Summary => Label + " · " + State;
    private string Format(ComparedValue value)
    {
        if (!value.Known) return "未知（未保存或尚未读取）";
        if (!value.Exists) return "此记录／分类不存在";
        if (value.Value is null) return "未记录／空值";
        if (value.Value.Length == 0) return "空字符串";
        if (Difference.Key == "status") return BookViewModel.Status(value.Value);
        if (Difference.Key == "duration_minutes") return value.Value + " 分钟";
        if (Difference.Key.EndsWith(":favorite", StringComparison.Ordinal)) return value.Value == "True" ? "已收藏" : "未收藏";
        if (Difference.Key.EndsWith(":kind", StringComparison.Ordinal)) return Converters.LocalChoiceConverter.Personal(value.Value);
        return value.Value;
    }
    public string Original => Format(Difference.Original);
    public string Current => Format(Difference.Current);
    public string Incoming => Format(Difference.Incoming);
    public bool Expanded => new[] { Original, Current, Incoming }.All(s => s.Length <= 160 && s.Count(c => c == '\n') < 3);
    public static IReadOnlyList<FieldComparisonDisplay> Rows(IEnumerable<FieldDifference> rows) => rows.Select(r => new FieldComparisonDisplay(r)).ToArray();
}
