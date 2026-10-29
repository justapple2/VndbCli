using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public partial class ImportFeedbackViewModel(Func<CancellationToken, Task<LocalImportStatus>> load,
    Func<Func<CancellationToken, Task>, Task> run) : ObservableObject
{
    [ObservableProperty] private string _summary = "读取服务限额和已有逐项回执；metadata到Web的JSON导入，活动到会话页。";
    [ObservableProperty] private IReadOnlyList<ImportFeedbackRow> _results = [];
    public IReadOnlyList<ImportFeedbackDisplay> DisplayResults => Results.Select(r => new ImportFeedbackDisplay(r)).ToArray();
    partial void OnResultsChanged(IReadOnlyList<ImportFeedbackRow> value) => OnPropertyChanged(nameof(DisplayResults));
    public async Task LoadAsync(CancellationToken ct)
    {
        LocalImportStatus state;
        try { state = await load(ct); }
        catch { Summary = "读取失败；上次逐项结果保留，不能据此确认新的写入。请核对连接、密钥或原回执后重试。"; throw; }
        Results = state.Results;
        Summary = (state.Limits is { } limits ? $"Web每批最多{limits.MaximumBytes:N0}字节／{limits.MaximumItems}项。" : "离线：限额以重连后的Web发现为准。") +
            "新资料可分成较小文件；已发送或待确认批次保留原ID、原件和回执，限额降低时需恢复兼容限额后重试。" +
            (state.Truncated ? "以下展示最近部分原稿与回执，完整记录保留于本机及Web。" : "以下是本机原稿与已有回执快照；Web已接收仍需后续处理，不是实时任务进度。");
    }
    [RelayCommand] private Task RefreshAsync() => run(LoadAsync);
}

public sealed record ImportFeedbackDisplay(ImportFeedbackRow Original)
{
    public string Key => Original.Key;
    public string Kind => Original.Kind switch { "metadata" => "公开资料", "items" or "item" => "书架条目", "plays" or "play" => "游玩经历", "play_sessions" or "session" => "计时会话", "play_edits" or "play_edit" => "个人编辑", _ => "待核对（" + Original.Kind + "）" };
    public string State => Original.Status switch { "waiting_web" => "已保存在本机，等待Web", "unconfirmed" => "结果未确认", "preview" when Original.Kind == "metadata" => "Web已接收，等待处理", "queued" => "等待补全", "imported" => "已收录", "existing" or "duplicate" => "复用原结果", "needs_review" => "候选待核对", "applied" => "已应用", "conflict" => "冲突待处理", "dependency_pending" => "依赖待补全", "error" or "invalid" => "处理失败，原件保留", _ => "待核对（" + Original.Status + "）" };
    public string Message => Original.Message ?? "";
    public string Evidence => $"种类：{Original.Kind}\n状态：{Original.Status}\n原键：{Original.Key}";
}
