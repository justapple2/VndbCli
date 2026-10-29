using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public partial class QueryDataFeatureViewModel(Func<LocalQueryScope> scope, Func<Func<CancellationToken, Task>, Task> run,
    Action<string> status) : ObservableObject
{
    private QueryMigrationPreview? _preview;
    private int _epoch;
    [ObservableProperty] private string _summary = "读取诊断可查看实际作用域和旧资料状态；不会自动迁移。";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApplyCommand))] private bool _approved;
    public void Invalidate() { _epoch++; _preview = null; Approved = false; ApplyCommand.NotifyCanExecuteChanged(); }
    [RelayCommand] private Task RefreshAsync() => run(async ct =>
    {
        var s = await scope().StatusAsync(ct);
        Summary = $"服务：{s.Origin ?? "离线作用域"}\n查询根：{s.QueryRoot}\n当前目录：{s.DirectoryPath}\n缓存{s.CachedQueries}项，保留批次{s.RetainedBatches}项；旧文件可证明{s.LegacyProvenFiles}项，待人工核对{s.LegacyReviewFiles}项。";
    });
    [RelayCommand] private Task PreviewAsync() => run(async ct =>
    {
        Invalidate(); var epoch = _epoch; var current = scope(); var preview = await new QueryDataMigration(current).PreviewAsync(ct: ct);
        if (epoch != _epoch) return; _preview = preview;
        Summary = $"将向{current.DirectoryPath}复制{preview.Files.Count}个已证明归属的旧查询文件；{preview.Review.Count}个文件来源不明或无效，保持原处隔离。原件不删除，同名异内容停止。\n" +
            string.Join("\n", preview.Files.Take(12).Select(f => f.RelativeTarget + " · " + f.Proof));
        ApplyCommand.NotifyCanExecuteChanged();
    });
    private bool CanApply() => Approved && _preview is { Files.Count: > 0 };
    [RelayCommand(CanExecute = nameof(CanApply))] private Task ApplyAsync() => run(async ct =>
    {
        var preview = _preview ?? throw new ArgumentException("请先预览旧查询资料。"); if (!Approved) throw new ArgumentException("需要明确批准。");
        Invalidate(); await new QueryDataMigration(scope()).ApplyAsync(preview, LocalArchiveFormat.Hash(preview), ct);
        Summary = "旧查询JSON已按原字节复制，原件与迁移清单保留。未证明来源的文件未加入当前队列。"; status("查询资料迁移已完成，可重新查询／同步。");
    });
}
