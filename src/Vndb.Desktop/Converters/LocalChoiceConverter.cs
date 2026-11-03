using System.Globalization;
using Avalonia.Data.Converters;
using Vndb.Contracts;
using Vndb.Desktop.ViewModels;
using Vndb.Local;
using Vndb.Core;

namespace Vndb.Desktop.Converters;

/// <summary>Display labels only. IDs and codes remain the selected values.</summary>
public sealed class LocalChoiceConverter : IValueConverter
{
    public static string Kind(string code) => LaunchComponentChoice.KindLabel(code);
    public static string Personal(string code) => code switch
    {
        "collection" => "集合", "tag" => "标签", "package" => "安装包", "patch" => "补丁", "save" => "存档",
        "bonus" => "特典", "document" => "说明", "unreviewed" => "未核对", "applicable" => "人工确认适用", "incompatible" => "不适用", _ => code
    };
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        string code => (parameter as string) switch { "kind" => Kind(code), "personal" => Personal(code), _ => BookViewModel.Status(code) },
        PersonalLabel label => label.Kind.Length == 0 ? label.Name : Personal(label.Kind) + " · " + label.Name,
        PersonalPlayView p => $"{BookViewModel.Status(p.Details.Status)} · {p.Details.StartedOn?.ToString("yyyy-MM-dd") ?? "开始未知"} → {p.Details.FinishedOn?.ToString("yyyy-MM-dd") ?? "结束未知"} · 手工 {p.Details.DurationMinutes?.ToString() ?? "未知"} 分钟 · {p.Id}",
        LocalPlayTarget t => $"{BookViewModel.Status(t.Details.Status)} · {(t.ExpectedRevision == 0 ? "新建游玩／重玩" : "已有记录修订 " + t.ExpectedRevision)} · {t.Id}",
        LocalInstallationView i => i.Installation.Details.Title + " · " + (i.Installation.RemovedAt is not null ? "已移除" : i.Installation.Status switch { "present" => "上次核对存在", "missing" => "缺失", _ => "待核对" }),
        LocalInstallationComponent c => (c.Details.Label is { Length: > 0 } label ? label + " · " : "") + Kind(c.Details.Kind) + " · " + c.Details.RelativePath,
        ProcessObservation p => $"进程 {p.Identity.Pid} · 创建标记 {p.Identity.CreatedUtcTicks}",
        _ => ""
    };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
