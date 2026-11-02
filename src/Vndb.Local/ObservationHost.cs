namespace Vndb.Local;

public interface IObservationWatchFactory
{
    IDisposable Create(string path, Action<string?, bool> changed);
}
public sealed class ObservationWatchFactory : IObservationWatchFactory
{
    public IDisposable Create(string path, Action<string?, bool> changed)
    {
        LocalFileLease.RequireOrdinaryAncestors(path);
        var watcher = new FileSystemWatcher(path) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName, InternalBufferSize = 8192 };
        watcher.Created += (_, e) => changed(e.FullPath, false); watcher.Deleted += (_, e) => changed(e.FullPath, false);
        watcher.Renamed += (_, e) => { changed(e.OldFullPath, false); changed(e.FullPath, false); };
        watcher.Error += (_, _) => changed(null, true);
        try { watcher.EnableRaisingEvents = true; return watcher; } catch { watcher.Dispose(); throw; }
    }
}

/// <summary>One cancellable in-process host. Bounded hints trigger validation, never direct state writes.</summary>
public sealed class ObservationHost(LocalLibraryStore library, string deviceId, Func<CancellationToken, Task<IReadOnlyList<LaunchProfile>>> profiles,
    IObservationFiles? files = null, IObservationWatchFactory? watcherFactory = null, TimeProvider? time = null) : IAsyncDisposable
{
    private readonly LocalObservation _observer = new(library, deviceId, files, time);
    private readonly IObservationWatchFactory _factory = watcherFactory ?? new ObservationWatchFactory();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly HashSet<string> _dirty = new(ObservationSettingsStore.Paths);
    private readonly Dictionary<string, IDisposable> _watchers = new(ObservationSettingsStore.Paths);
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private LocalFileLease? _owner;
    private Task? _loop;
    private ObservationSettings _settings = new();
    private DateTimeOffset _firstHint, _lastHint, _nextSweep;
    private DateTimeOffset _lastFocus;
    private bool _all = true, _watchError, _closed, _budgetExceeded;
    private ObservationResult? _result;
    public event Action<ObservationResult>? Changed;
    public ObservationResult? Result => Volatile.Read(ref _result);
    public string Status { get; private set; } = "本机观测未启用。";
    public int WatcherCount { get { lock (_gate) return _watchers.Count; } }
    public int PendingCount { get { lock (_gate) return _dirty.Count; } }
    public long Hints { get; private set; }
    public long Passes { get; private set; }
    public bool Running => _loop is { IsCompleted: false };
    public void Focus(string path)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_closed || now - _lastFocus < TimeSpan.FromMinutes(1) || Result is { } result && now - result.CheckedAt < TimeSpan.FromMinutes(1)) return;
            _lastFocus = now;
        }
        Hint(path);
    }
    public async Task StartAsync(ObservationSettings settings, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_closed || _loop is not null) throw new InvalidOperationException("观测宿主已启动或关闭。");
        new ObservationSettingsStore(library.DirectoryPath).Validate(settings); _settings = settings;
        if (!settings.Enabled || (settings.Roots?.Count ?? 0) == 0) return;
        _owner = LocalFileLease.TryAcquire(library.DirectoryPath, "local-observation-host");
        if (_owner is null) { Status = "同一数据根已有观测实例，本窗口不重复监听。"; return; }
        try
        {
            foreach (var root in settings.Roots ?? []) TryWatch(root.Path);
            if (_closed) return;
            // Root watchers capture hints while baseline discovers company-level watchers.
            Status = "正在后台建立浅层观测基线。";
            _nextSweep = _time.GetUtcNow();
            _loop = Task.Run(LoopAsync, _stop.Token);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await DisposeAsync(); throw; }
    }
    public void Hint(string? path, bool error = false)
    {
        lock (_gate)
        {
            if (_closed) return;
            Hints++;
            var now = _time.GetUtcNow(); if (_dirty.Count == 0 && (!_all || _firstHint == default || now - _lastHint > TimeSpan.FromSeconds(3))) _firstHint = now;
            _lastHint = now;
            if (error || path is null || _dirty.Count >= 256) { _all = true; _watchError |= error; _dirty.Clear(); }
            else
            {
                var full = Path.GetFullPath(path);
                if ((_settings.Roots ?? []).Any(r => ObservationSettingsStore.Within(r.Path, full))) _dirty.Add(full);
            }
        }
        Signal();
    }
    private void Signal() { try { if (_signal.CurrentCount == 0) _signal.Release(); } catch (SemaphoreFullException) { } }
    private bool TryWatch(string path)
    {
        lock (_gate)
        {
            if (_closed || _watchers.ContainsKey(path)) return false;
            if (_watchers.Count >= _settings.WatcherLimit) { _budgetExceeded = true; return false; }
            try
            {
                var watcher = _factory.Create(path, (p, error) => Hint(p, error));
                if (_closed) { watcher.Dispose(); return false; }
                _watchers.Add(path, watcher); return true;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException) { _watchError = true; return false; }
        }
    }
    public async Task<ObservationResult?> CheckNowAsync(CancellationToken ct = default, string? path = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        await _pass.WaitAsync(linked.Token);
        try { return await CheckCoreAsync(force: true, linked.Token, path); } finally { _pass.Release(); }
    }
    private async Task<ObservationResult?> CheckCoreAsync(bool force, CancellationToken ct, string? onlyPath = null)
    {
        string[]? dirty; bool reset;
        lock (_gate)
        {
            var now = _time.GetUtcNow(); var periodic = now >= _nextSweep;
            if (!force && !periodic && ((!_all && _dirty.Count == 0) || now - _lastHint < TimeSpan.FromSeconds(3) && now - _firstHint < TimeSpan.FromSeconds(10))) return null;
            if (onlyPath is not null) { dirty = [onlyPath]; reset = false; }
            else { dirty = force || periodic || _all ? null : _dirty.ToArray(); reset = _watchError; _watchError = false; _all = false; _dirty.Clear(); }
            if (reset) { foreach (var watcher in _watchers.Values) watcher.Dispose(); _watchers.Clear(); }
        }
        if (reset) foreach (var root in _settings.Roots ?? []) TryWatch(root.Path);
        var result = await _observer.CheckAsync(_settings, await profiles(ct), dirty, discover: onlyPath is null, ct); Passes++;
        ct.ThrowIfCancellationRequested();
        var healthyPaths = result.WatchPaths.ToHashSet(ObservationSettingsStore.Paths);
        if (onlyPath is null && dirty is null && (_settings.Roots ?? []).Any(r => !healthyPaths.Contains(r.Path))) _watchError = true;
        if (dirty is null)
        {
            _budgetExceeded = false;
            lock (_gate)
                foreach (var path in _watchers.Keys.Where(p => !healthyPaths.Contains(p) && !(_settings.Roots ?? []).Any(r => ObservationSettingsStore.Paths.Equals(p, r.Path))).ToArray())
                { _watchers[path].Dispose(); _watchers.Remove(path); }
        }
        var added = false; foreach (var path in result.WatchPaths) added |= TryWatch(path);
        if (added) { lock (_gate) _all = true; Signal(); } // Reconcile the small window before a new company watcher was installed.
        var pending = result.Deferred || result.Snapshot.Installations.Any(i => i.NegativeSince is not null && i.Status != "missing") ||
            result.Snapshot.Packages.Any(i => i.NegativeSince is not null && i.Status != "missing") || result.Snapshot.Candidates.Any(c => c.State == "checking");
        var next = _time.GetUtcNow().Add(pending ? TimeSpan.FromSeconds(10) : result.Degraded || _watchError ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(_settings.PeriodMinutes));
        _nextSweep = onlyPath is not null && _nextSweep < next ? _nextSweep : next;
        var degraded = result.Degraded || _watchError || _budgetExceeded;
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_closed) return null;
            Status = $"{(degraded ? "观测降级，周期复核兜底" : "浅层监控已启用")} · {_watchers.Count} 个监听器 · {result.Snapshot.Candidates.Count(c => c.State is "pending" or "checking")} 个待匹配候选";
            Volatile.Write(ref _result, result with { Degraded = degraded }); Changed?.Invoke(Result!); return Result;
        }
    }
    private async Task LoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _pass.WaitAsync(_stop.Token);
                try { await CheckCoreAsync(false, _stop.Token); }
                catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                { Status = "观测暂停／待核对：本机设置或资源不可用，原件保留。"; _nextSweep = _time.GetUtcNow().AddMinutes(1); }
                finally { _pass.Release(); }
                await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token); // A hot producer cannot turn hint polling into a busy loop.
                await _signal.WaitAsync(TimeSpan.FromSeconds(3), _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        lock (_gate) { if (_closed) return; _closed = true; foreach (var watcher in _watchers.Values) watcher.Dispose(); _watchers.Clear(); _dirty.Clear(); }
        _stop.Cancel(); if (_loop is not null) await _loop.ConfigureAwait(false);
        await _pass.WaitAsync().ConfigureAwait(false); _pass.Release(); _owner?.Dispose(); _owner = null; _stop.Dispose();
        Status = "本机观测已停止。";
    }
}
