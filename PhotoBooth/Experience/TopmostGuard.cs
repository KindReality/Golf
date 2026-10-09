using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PhotoBooth.Diagnostics;

namespace Experience;

internal sealed class TopmostGuard : IDisposable
{
    private readonly Window _window;
    private readonly Func<bool> _isActive;
    private readonly Func<string?> _requestId;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly WinEventCallback _callback;
    private readonly IntPtr _handle;
    private IntPtr _foregroundHook, _orderHook;
    private int _queued;
    private bool _disposed;
    private long _lastEventQueued;
    private long _lastRaiseLog;
    private int _suppressedRaiseLogs;

    public TopmostGuard(Window window, Func<bool> isActive, Func<string?> requestId)
    {
        _window = window;
        _isActive = isActive;
        _requestId = requestId;
        _handle = new WindowInteropHelper(window).Handle;
        _callback = OnWindowEvent; // Keep the native callback alive until the hooks are removed.
        // Out-of-context callbacks; skip this process to avoid reacting to our own raises.
        _foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, _callback, 0, 0, 2);
        _orderHook = SetWinEventHook(0x8004, 0x8004, IntPtr.Zero, _callback, 0, 0, 2);
        if (_foregroundHook == IntPtr.Zero || _orderHook == IntPtr.Zero)
            Telemetry.Warning("TopmostHookUnavailable", new { ForegroundHook = _foregroundHook != IntPtr.Zero,
                OrderHook = _orderHook != IntPtr.Zero, NativeError = Marshal.GetLastWin32Error(), WatchdogSeconds = 1 });
        _timer.Tick += (_, _) => EnsureTopmost("Watchdog");
        _timer.Start();
    }

    private void OnWindowEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || window == _handle) return;
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastEventQueued) < 100 || Interlocked.Exchange(ref _queued, 1) != 0) return;
        Interlocked.Exchange(ref _lastEventQueued, now);
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _queued, 0);
            EnsureTopmost("WindowOrderChanged");
        }));
    }

    public void EnsureTopmost(string reason, bool force = false)
    {
        if (_disposed || !_isActive() || !IsWindowVisible(_handle)) return;
        if (!force && (GetWindowLongPtr(_handle, -20).ToInt64() & 8) != 0 && !HasCoveringWindow()) return;
        // Raise within the topmost band without activating, moving, or resizing the window.
        if (SetWindowPos(_handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013))
        {
            var now = Environment.TickCount64;
            if (force || now - _lastRaiseLog >= 5000)
            {
                Telemetry.Info("TopmostReasserted", new { Reason = reason, SuppressedRepeatLogs = _suppressedRaiseLogs }, _requestId());
                _lastRaiseLog = now;
                _suppressedRaiseLogs = 0;
            }
            else _suppressedRaiseLogs++;
        }
        else
            Telemetry.Warning("TopmostReassertFailed", new { Reason = reason, NativeError = Marshal.GetLastWin32Error() }, _requestId());
    }

    private bool HasCoveringWindow()
    {
        if (!GetWindowRect(_handle, out var own)) return true;
        var above = GetWindow(_handle, 3); // GW_HWNDPREV: walk toward the top of the Z order.
        for (var count = 0; above != IntPtr.Zero && count < 4096; count++, above = GetWindow(above, 3))
        {
            GetWindowThreadProcessId(above, out var process);
            if (process == Environment.ProcessId || !IsWindowVisible(above) || IsIconic(above)) continue;
            // Cloaked windows retain WS_VISIBLE but are not drawn on this desktop.
            if (DwmGetWindowAttribute(above, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (GetWindowRect(above, out var other) && other.Left < own.Right && other.Right > own.Left &&
                other.Top < own.Bottom && other.Bottom > own.Top) return true;
        }
        return false;
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        if (_orderHook != IntPtr.Zero) UnhookWinEvent(_orderHook);
        _foregroundHook = _orderHook = IntPtr.Zero;
        GC.KeepAlive(_callback);
    }

    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);
}
