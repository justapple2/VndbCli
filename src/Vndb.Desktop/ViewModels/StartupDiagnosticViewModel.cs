using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public sealed record StartupStepDisplay(StartupDiagnosticStep Step)
{
    public string Label => (Step.Stage switch { StartupStage.Configuration => "本机配置与身份", StartupStage.Covers => "封面服务组装", StartupStage.Shelf => "个人书架与分类", StartupStage.Sessions => "会话恢复与观测", _ => "未知阶段" }) + " · " +
        (Step.State == "complete" ? "已完成" : Step.State == "cancelled" ? "已取消" : "未完成") + $" · {Step.DurationMs} ms";
    public string Detail => $"{Step.StartedAt.ToLocalTime():g} · {Step.ErrorCode ?? "无错误"}\n{Step.Advice}";
}
public sealed partial class StartupDiagnosticViewModel(Func<Task<string?>> selectFile) : ObservableObject
{
    public StartupDiagnostics Trace { get; } = new();
    [ObservableProperty] private IReadOnlyList<StartupStepDisplay> _steps = [];
    [ObservableProperty] private string _summary = "尚未执行启动检查。报告不包含密钥、异常原文或本机文件结构。";
    [ObservableProperty] private string _exportMessage = "";
    public void Refresh()
    {
        var report = Trace.Snapshot(); Steps = report.Steps.Select(s => new StartupStepDisplay(s)).ToArray();
        Summary = $"最近一次启动 · {report.Version} · {report.Platform} · {Steps.Count} 个阶段" + (Trace.Failed ? " · 有阶段未完成" : "");
    }
    [RelayCommand] private async Task ExportAsync()
    {
        try { if (await selectFile() is { } file) { await Trace.SaveAsync(file); ExportMessage = "已导出脱敏启动报告；未包含凭据、游戏位置或原个人资料。"; } }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { ExportMessage = "无法保存报告，请选择新的可写JSON文件；原文件保留。"; }
    }
}
