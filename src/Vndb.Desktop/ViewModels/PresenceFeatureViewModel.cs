using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Contracts;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public partial class PresenceFeatureViewModel(Func<PresenceSync> service, Func<ISyncApi?> remote,
    Func<string?> origin, Func<string?> device, Func<Func<CancellationToken, Task>, Task> run, Action<string> status) : ObservableObject
{
    private PresenceRecheckPreview? _preview;
    private int _epoch;
    [ObservableProperty] private string _stateLabel = "读取状态后可按原批次重试；明确冲突时再重新核对。";
    [ObservableProperty] private string _previewLabel = "未生成重新核对预览。";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ConfirmCommand))] private bool _approved;
    public void Invalidate() { _epoch++; _preview = null; Approved = false; PreviewLabel = "条件变化，请重新读取状态／预览。"; ConfirmCommand.NotifyCanExecuteChanged(); }
    private string Origin => origin() ?? throw new ArgumentException("请先配置Web地址。");
    private string Device => device() ?? throw new InvalidOperationException("请先加载本机身份。");
    private ISyncApi Remote => remote() ?? throw new InvalidOperationException("请先配置Web地址。");
    public async Task ReadAsync(CancellationToken ct) => StateLabel = (await service().StateAsync(Origin, Device, ct)).Message;
    [RelayCommand] private Task RefreshAsync() => run(ReadAsync);
    [RelayCommand] private Task RetryAsync() => run(async ct =>
    { try { await service().SubmitAsync(Remote, Origin, Device, ct); status("存在标记已核对，原稿和回执保留。"); } finally { await ReadAsync(ct); } });
    [RelayCommand] private Task PreviewAsync() => run(async ct =>
    {
        Invalidate(); var epoch = _epoch; var result = await service().PreviewRecheckAsync(Remote, Origin, Device, ct);
        if (epoch != _epoch) return; _preview = result;
        PreviewLabel = $"将封存已拒绝原件；排除{result.ExcludedItems}个已移除／未收录条目。\n" +
            (result.Replacement is null ? "当前状态无需再提交。" : $"按Web修订{result.HeadRevision}建立{result.Replacement.Items.Count}项替代批次。") + "\n确认后保存替代意图，再点击按原批次重试；不会移动游戏。";
        ConfirmCommand.NotifyCanExecuteChanged(); await ReadAsync(ct);
    });
    private bool CanConfirm() => Approved && _preview is not null;
    [RelayCommand(CanExecute = nameof(CanConfirm))] private Task ConfirmAsync() => run(async ct =>
    {
        var preview = _preview ?? throw new ArgumentException("请先生成预览。"); if (!Approved) throw new ArgumentException("请明确确认重新核对。");
        Invalidate(); await service().ApplyRecheckAsync(Remote, preview, LocalArchiveFormat.Hash(preview), ct); await ReadAsync(ct);
        status("拒绝原件已封存，替代意图已保存；可按原批次重试。");
    });
}
