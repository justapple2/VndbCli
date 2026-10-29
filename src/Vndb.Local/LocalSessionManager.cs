using System.Diagnostics;
using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

public sealed record LocalSessionState(string TargetId, SessionSample Sample, ProcessIdentity? Process, string ExpectedExecutable,
    long ElapsedTicks, string Stage);

/// <summary>One client-process timer. No network, background service, or process termination.</summary>
public sealed class LocalSessionManager : IDisposable
{
    private readonly LocalLauncher _launcher;
    private readonly ActivityOutbox _outbox;
    private readonly IProcessInventory _inventory;
    private readonly ISessionClock _clock;
    private readonly TimeProvider _time;
    private readonly string _stateFile;
    private readonly FileStream _lease;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SessionAccumulator? _counter;
    private ProcessTracker? _tracker;
    private Process? _root;
    private long _matchStarted;
    private long _lastSaved;
    private int _timeout;
    public LocalSessionState? Current { get; private set; }
    public bool Running => Current?.Sample.State is "active" or "paused";
    public LocalSessionManager(LocalLauncher launcher, ActivityOutbox outbox, string directory,
        IProcessInventory? inventory = null, ISessionClock? clock = null, TimeProvider? time = null)
    {
        _launcher = launcher; _outbox = outbox; _inventory = inventory ?? new WindowsProcessInventory(); _clock = clock ?? new SessionClock(); _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(directory); _stateFile = Path.Combine(directory, "active-session.json");
        _lease = new(Path.Combine(directory, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_stateFile)) return;
        if ((File.GetAttributes(_stateFile) & FileAttributes.ReparsePoint) != 0 || new FileInfo(_stateFile).Length > 8 * 1024 * 1024) throw new InvalidDataException("会话检查点不是普通JSON或过大。");
        Current = JsonSerializer.Deserialize<LocalSessionState>(await File.ReadAllTextAsync(_stateFile, ct), VndbJson.Options) ?? throw new InvalidDataException("会话检查点损坏，原文件保留。");
        if (!Guid.TryParseExact(Current.TargetId, "D", out _) || Current.Sample is null || Current.ElapsedTicks < 0 || Current.Sample.Sequence < 1 || Current.Sample.CumulativeSeconds < 0) throw new InvalidDataException("会话检查点无效，原文件保留。");
        if (!Running) { await _outbox.SaveSessionAsync(Current.TargetId, Current.Sample, ct); return; }
        // The unobserved interval is deliberately absent. A new explicit attach can start from now.
        Current = Current with { Sample = Current.Sample with { Sequence = Current.Sample.Sequence + 1, State = "interrupted", EndedAt = Current.Sample.CheckpointAt, Reason = "client_recovered_unknown_gap_excluded" }, Stage = "recovered" };
        await PersistAsync(ct);
    }
    public async Task StartAsync(string profileId, LocalPlayTarget target, CancellationToken ct = default, LaunchMethod method = LaunchMethod.Profile, LocaleLaunch? globalLocale = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Running) throw new InvalidOperationException("本设备已有自动会话，请先结束当前计时。");
            var profile = (await _launcher.ReadAsync(ct)).Single(p => p.Id == profileId);
            var now = _time.GetUtcNow();
            _counter = new(_clock); _counter.Pause("awaiting_process");
            Current = new(target.Id, new(Guid.NewGuid().ToString("D"), target.Dependencies.DeviceId, target.PlayRef, profile.InstallationId, profile.ComponentId,
                now, now, null, 1, 0, "paused", "awaiting_process"), null, profile.TrackedExecutable ?? profile.Executable, 0, "waiting");
            _matchStarted = _clock.Read().MonotonicTicks; _timeout = profile.MatchTimeoutSeconds;
            await PersistAsync(ct); // Durable intent precedes process launch.
            try
            {
                var launched = await _launcher.LaunchTrackedAsync(profileId, ct, method, globalLocale); _root?.Dispose(); _root = launched.Process;
                if (launched.Identity is null) { await FinishCoreAsync("review", "process_identity_unconfirmed", true, ct); return; }
                _tracker = new(launched.Identity, Current.ExpectedExecutable);
                if (string.Equals(launched.Identity.Executable, Current.ExpectedExecutable, StringComparison.OrdinalIgnoreCase))
                { Current = Current with { Process = launched.Identity, Stage = "tracking", Sample = Current.Sample with { State = "active", Reason = null } }; _counter.Resume(); }
                Current = Current with { Sample = Current.Sample with { Sequence = Current.Sample.Sequence + 1 } }; await PersistAsync(ct);
            }
            catch
            {
                await FinishCoreAsync("review", "launch_failed", true, CancellationToken.None); throw;
            }
        }
        finally { _gate.Release(); }
    }
    public IReadOnlyList<ProcessObservation> Processes() => _inventory.Snapshot();
    public async Task AttachAsync(ProcessIdentity selected, LocalPlayTarget target, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Running) throw new InvalidOperationException("先结束当前自动会话。");
            if (!_inventory.Snapshot().Any(p => WindowsProcessInventory.Same(p.Identity, selected))) throw new IOException("选中的进程已退出或PID被复用，请重新选择。");
            _root?.Dispose(); _root = null; _tracker = null; _counter = new(_clock);
            var now = _time.GetUtcNow();
            Current = new(target.Id, new(Guid.NewGuid().ToString("D"), target.Dependencies.DeviceId, target.PlayRef, null, null, now, now, null, 1, 0, "active", "explicit_attach"), selected, selected.Executable, 0, "tracking");
            await PersistAsync(ct);
        }
        finally { _gate.Release(); }
    }
    public async Task TickAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!Running || Current is null || _counter is null) return;
            IReadOnlyList<ProcessObservation> snapshot;
            try { snapshot = _inventory.Snapshot(); }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
            { _counter.Pause("process_inspection_failed"); await CheckpointCoreAsync(ct); return; }
            if (Current.Stage == "waiting")
            {
                DateTimeOffset? exited = null;
                try { if (_root?.HasExited == true) exited = _root.ExitTime.ToUniversalTime(); } catch (InvalidOperationException) { }
                var matches = _tracker?.Match(snapshot, _time.GetUtcNow(), exited) ?? [];
                if (matches.Count > 1) { await FinishCoreAsync("review", "multiple_matching_descendants", true, ct); return; }
                if (matches.Count == 1)
                { Current = Current with { Process = matches[0], Stage = "tracking", Sample = Current.Sample with { State = "active", Reason = null } }; _counter.Resume(); await CheckpointCoreAsync(ct); return; }
                if (_clock.Read().MonotonicTicks - _matchStarted >= TimeSpan.FromSeconds(_timeout).Ticks)
                    await FinishCoreAsync("review", "process_match_timeout", true, ct);
                return;
            }
            var alive = Current.Process is { } identity && snapshot.Any(p => WindowsProcessInventory.Same(p.Identity, identity));
            _counter.Tick(alive);
            if (!alive) { await FinishCoreAsync("ended", "process_exit", false, ct); return; }
            Current = Current with { ElapsedTicks = _counter.ElapsedTicks, Sample = Current.Sample with { State = _counter.Paused ? "paused" : "active", Reason = _counter.PauseReason } };
            if (_clock.Read().MonotonicTicks - _lastSaved >= TimeSpan.FromSeconds(30).Ticks) await CheckpointCoreAsync(ct);
        }
        finally { _gate.Release(); }
    }
    public async Task PauseAsync(bool resume = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!Running || _counter is null || Current?.Stage != "tracking") throw new InvalidOperationException("当前没有可暂停／继续的已确认进程。");
            if (resume)
            {
                if (!_inventory.Snapshot().Any(p => WindowsProcessInventory.Same(p.Identity, Current.Process!))) throw new IOException("原进程身份已改变。");
                _counter.Resume();
            }
            else _counter.Pause();
            await CheckpointCoreAsync(ct);
        }
        finally { _gate.Release(); }
    }
    public async Task StopAsync(bool closing = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!Running) return;
            _counter?.Tick(Current?.Process is { } identity && _inventory.Snapshot().Any(p => WindowsProcessInventory.Same(p.Identity, identity)));
            await FinishCoreAsync(closing ? "interrupted" : "ended", closing ? "client_closed_unknown_gap_excluded" : "manual_stop", false, ct);
        }
        finally { _gate.Release(); }
    }
    private async Task CheckpointCoreAsync(CancellationToken ct)
    {
        if (Current is null || _counter is null) return;
        Current = Current with { ElapsedTicks = _counter.ElapsedTicks, Sample = Current.Sample with { Sequence = Current.Sample.Sequence + 1, CheckpointAt = _time.GetUtcNow(), CumulativeSeconds = _counter.ElapsedTicks / TimeSpan.TicksPerSecond,
            State = _counter.Paused ? "paused" : "active", Reason = _counter.PauseReason } };
        await PersistAsync(ct);
    }
    private async Task FinishCoreAsync(string state, string reason, bool review, CancellationToken ct)
    {
        if (Current is null) return;
        var now = _time.GetUtcNow(); var ticks = _counter?.ElapsedTicks ?? Current.ElapsedTicks;
        Current = Current with { ElapsedTicks = ticks, Stage = "closed", Sample = Current.Sample with { Sequence = Current.Sample.Sequence + 1, CheckpointAt = now, EndedAt = now,
            CumulativeSeconds = ticks / TimeSpan.TicksPerSecond, State = state, Reason = reason, NeedsReview = review } };
        await PersistAsync(ct); _root?.Dispose(); _root = null;
    }
    private async Task PersistAsync(CancellationToken ct)
    {
        if (Current is null) return;
        await JsonSnapshotStore.AtomicWriteAsync(_stateFile, Current, true, ct);
        await _outbox.SaveSessionAsync(Current.TargetId, Current.Sample, ct); _lastSaved = _clock.Read().MonotonicTicks;
    }
    public void Dispose() { _root?.Dispose(); _lease.Dispose(); }
}
