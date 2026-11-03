using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

public enum StartupStage { Configuration, Covers, Shelf, Sessions }
public sealed record StartupDiagnosticStep(StartupStage Stage, string State, DateTimeOffset StartedAt, long DurationMs, string? ErrorCode, string Advice);
public sealed record StartupDiagnosticReport(DateTimeOffset StartedAt, string Version, string Runtime, string Platform, IReadOnlyList<StartupDiagnosticStep> Steps,
    string Format = "vn-startup-diagnostics", int FormatVersion = 1);

/// <summary>One bounded attempt. Allowlisted facts only; exception messages, stacks, paths, settings and personal data are never stored.</summary>
public sealed class StartupDiagnostics(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly List<StartupDiagnosticStep> _steps = [];
    private DateTimeOffset _started;
    public void Begin() { _steps.Clear(); _started = _time.GetUtcNow(); }
    public StartupDiagnosticReport Snapshot() => new(_started, typeof(StartupDiagnostics).Assembly.GetName().Version?.ToString() ?? "unknown", Environment.Version.ToString(),
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "other", _steps.ToArray());
    public bool Failed => _steps.Any(s => s.State is "failed" or "cancelled");
    public void RecordFailure(StartupStage stage, Exception error)
    {
        if (!Enum.IsDefined(stage) || _steps.Count >= 16) return;
        var code = Category(error);
        _steps.Add(new(stage, error is OperationCanceledException ? "cancelled" : "failed", _time.GetUtcNow(), 0, code, Advice(stage, code)));
    }
    public async Task RunAsync(StartupStage stage, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        if (!Enum.IsDefined(stage) || _steps.Count >= 16) throw new InvalidOperationException("启动诊断阶段无效或超出预算。");
        var started = _time.GetUtcNow(); var ticks = _time.GetTimestamp();
        try
        {
            await action(ct);
            _steps.Add(new(stage, "complete", started, Elapsed(ticks), null, "此阶段已完成。"));
        }
        catch (Exception error)
        {
            var code = Category(error);
            _steps.Add(new(stage, error is OperationCanceledException ? "cancelled" : "failed", started, Elapsed(ticks), code, Advice(stage, code))); throw;
        }
    }
    private long Elapsed(long ticks) => Math.Max(0, (long)_time.GetElapsedTime(ticks).TotalMilliseconds);
    private static string Category(Exception error) => error switch
    {
        OperationCanceledException => "cancelled", System.Text.Json.JsonException => "invalid_json", InvalidDataException => "invalid_data",
        UnauthorizedAccessException or System.Security.Cryptography.CryptographicException => "permission",
        ClientApiException { Status: 401 or 403 } => "authentication", CatalogUnavailableException or OfflineCacheMissException or HttpRequestException or TimeoutException => "unavailable",
        IOException => "io", ArgumentException => "invalid_setting", PlatformNotSupportedException => "runtime_unavailable", _ => "unexpected"
    };
    private static string Advice(StartupStage stage, string code) => code switch
    {
        "cancelled" => "操作已取消；原登记、原稿和回执保留，可稍后重试。",
        "authentication" => "请在连接设置核对Web管理密钥；不要使用NAS系统密码。",
        "permission" => "检查本机数据访问权限或在设置重新提供凭据；不要清空个人库或修改系统权限。",
        "invalid_json" or "invalid_data" => "本机JSON可能损坏、超限或格式不兼容。保留原件，核对版本或从已验证备份恢复；不要重置登记。",
        "unavailable" => "检查Web连接；保留本机缓存。离线启动配置不依赖Web，未缓存的明细需恢复连接后读取。",
        "invalid_setting" => "核对连接与本机设置；修正后重新尝试，不自动改写原配置。",
        "runtime_unavailable" => "核对当前系统与发布包是否匹配，使用完整的独立发布目录。",
        _ when stage == StartupStage.Sessions => "会话恢复未完成；保留活动记录，关闭重复客户端并核对本机数据后重试。",
        _ => "本阶段未完成。保留数据并导出这份脱敏报告排查，不需要删除个人资料或游戏。"
    };
    public Task SaveAsync(string file, CancellationToken ct = default)
    {
        if (!Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择新的JSON报告文件。");
        return JsonSnapshotStore.AtomicWriteAsync(file, Snapshot(), false, ct, maxBytes: 64 * 1024);
    }
    public async Task<bool> SaveLatestAsync(string directory, CancellationToken ct = default)
    {
        try { await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(directory, "startup-diagnostics.json"), Snapshot(), true, ct, maxBytes: 64 * 1024); return true; }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
