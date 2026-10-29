using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Vndb.Local;

public sealed record SessionClockReading(long MonotonicTicks, long AwakeTicks);
public interface ISessionClock { SessionClockReading Read(); }
public sealed partial class SessionClock : ISessionClock
{
    [LibraryImport("kernel32.dll")] private static partial int QueryUnbiasedInterruptTime(out ulong time);
    public SessionClockReading Read()
    {
        var ticks = Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()).Ticks;
        if (!OperatingSystem.IsWindows()) return new(ticks, ticks);
        if (QueryUnbiasedInterruptTime(out var awake) == 0) throw new IOException("无法读取排除睡眠的系统时钟，停止自动计时。");
        return new(ticks, checked((long)awake));
    }
}

/// <summary>No wall-clock arithmetic, idle heuristics, or CPU time. A polling gap pauses conservatively.</summary>
public sealed class SessionAccumulator(ISessionClock clock, long initialTicks = 0)
{
    private SessionClockReading _last = clock.Read();
    public long ElapsedTicks { get; private set; } = initialTicks;
    public bool Paused { get; private set; }
    public string? PauseReason { get; private set; }
    public void Tick(bool confirmedAlive)
    {
        var current = clock.Read(); var delta = current.MonotonicTicks - _last.MonotonicTicks; var awake = current.AwakeTicks - _last.AwakeTicks; _last = current;
        if (delta < 0 || awake < 0 || delta > TimeSpan.FromSeconds(5).Ticks || Math.Abs(delta - awake) > TimeSpan.FromMilliseconds(100).Ticks)
        { Paused = true; PauseReason = "sleep_or_unobserved_gap"; return; }
        if (!Paused && confirmedAlive) ElapsedTicks = checked(ElapsedTicks + delta);
    }
    public void Pause(string reason = "manual") { Tick(true); Paused = true; PauseReason = reason; }
    public void Resume() { _last = clock.Read(); Paused = false; PauseReason = null; }
}
