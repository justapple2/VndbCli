using Vndb.Contracts;
using Vndb.Client;
using Vndb.Local;

namespace Vndb.Desktop.Services;

/// <summary>Two in-process loops: slow network cannot stall process observations.</summary>
public sealed class DesktopActivityHost(DesktopServices services) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private Task? _tracking;
    private Task? _sync;
    private bool _disposed;
    public string Status { get; private set; } = "待同步操作保存在本机。";
    public void Start()
    {
        _tracking = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                try
                {
                    var wasRunning = services.Sessions?.Running == true;
                    if (services.Sessions is { } sessions) await sessions.TickAsync(_stop.Token);
                    if (wasRunning && services.Sessions?.Running != true) SignalSync();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
                { Status = "检查点待处理：" + e.Message; }
            }
        }, _stop.Token);
        _sync = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                await _signal.WaitAsync(TimeSpan.FromSeconds(60), _stop.Token);
                try { await SyncAsync(_stop.Token); }
                catch (Exception e) when (e is WebUnavailableException or ClientApiException or IOException or InvalidOperationException or HttpRequestException or System.Text.Json.JsonException or ArgumentException or UnauthorizedAccessException)
                { Status = e is WebUnavailableException ? "Web不可达，活动JSON等待重连。" : e.Message; }
            }
        }, _stop.Token);
    }
    public void SignalSync() { if (_signal.CurrentCount == 0) { try { _signal.Release(); } catch (SemaphoreFullException) { } } }
    public async Task SyncAsync(CancellationToken ct)
    {
        if (services.Web is null || services.Settings.Server is null) { Status = "未设置Web；可导出活动JSON。"; return; }
        var result = await services.SyncService().SyncAsync(ct);
        Status = result.PersonalPending > 0 ? $"{result.PersonalPending} 条个人编辑待核对，原稿保留；从作品右键重新打开编辑面板处理。" :
            !result.ActivitiesComplete ? "存在待补依赖或个人编辑冲突，请核对逐项回执。" : "活动已同步；原始JSON及回执保留。";
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (services.Sessions is { Running: true } session) await session.StopAsync(closing: true);
        _stop.Cancel();
        if (_tracking is not null) { try { await _tracking; } catch (OperationCanceledException) { } }
        if (_sync is not null) { try { await _sync; } catch (OperationCanceledException) { } }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await SyncAsync(timeout.Token); } catch (Exception e) when (e is OperationCanceledException or WebUnavailableException or ClientApiException or IOException or InvalidOperationException or System.Text.Json.JsonException or ArgumentException or UnauthorizedAccessException) { }
        _stop.Dispose(); _disposed = true;
    }
}
