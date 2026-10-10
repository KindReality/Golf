using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Controller.Diagnostics;

namespace Controller.Scheduling;

// A high-resolution one-shot wait avoids the normal Windows timer tick without
// changing the machine's timer resolution. A configuration edit wakes it early.
internal sealed class SchedulerWait : IDisposable
{
    private readonly AutoResetEvent _wake;
    private readonly ActivityLog _log;
    private readonly TimerHandle? _timer;
    private readonly WaitHandle[] _handles;
    private bool _warned;

    public SchedulerWait(AutoResetEvent wake, ActivityLog log)
    {
        _wake = wake;
        _log = log;
        var handle = CreateWaitableTimerExW(0, 0, 0x00000002, 0x001F0003);
        if (!handle.IsInvalid) _timer = new(handle);
        else { handle.Dispose(); Warn(); }
        _handles = _timer is null ? [wake] : [wake, _timer];
    }

    public void Until(long timestamp)
    {
        var remaining = timestamp - Stopwatch.GetTimestamp();
        if (remaining <= 0) return;
        if (_timer is not null)
        {
            // Recheck long monotonic deadlines once a second after suspend/resume.
            var dueTime = -Math.Max(1, (long)Math.Min(TimeSpan.TicksPerSecond, remaining * 10000000d / Stopwatch.Frequency));
            if (SetWaitableTimerEx(_timer.SafeWaitHandle, ref dueTime, 0, 0, 0, 0, 0))
            {
                WaitHandle.WaitAny(_handles);
                return;
            }
            Warn();
        }
        var milliseconds = remaining * 1000d / Stopwatch.Frequency;
        if (milliseconds < 1) Thread.Yield();
        else _wake.WaitOne((int)Math.Min(int.MaxValue, Math.Floor(milliseconds)));
    }

    private void Warn()
    {
        if (_warned) return;
        _warned = true;
        _log.Warning("HighResolutionTimerUnavailable", "Scheduler is using the normal timer; send timing may be less precise",
            new { Win32Error = Marshal.GetLastWin32Error() });
    }
    public void Dispose() => _timer?.Dispose();

    private sealed class TimerHandle : WaitHandle
    {
        public TimerHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, ref long dueTime, int period,
        nint completionRoutine, nint argument, nint wakeContext, uint tolerableDelay);
}
