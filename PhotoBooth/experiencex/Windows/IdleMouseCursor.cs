using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ExperienceX;

// One owner for all monitor windows: every Hide has a matching Show on the UI thread.
internal sealed class IdleMouseCursor : IDisposable
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _focused;
    private readonly Func<Point> _position;
    private readonly Func<TimeSpan> _now;
    private readonly Action<bool> _setHidden;
    private Point _lastPosition;
    private TimeSpan _lastMovement;
    private bool _wasFocused, _hidden, _disposed;

    public IdleMouseCursor(Dispatcher dispatcher, Func<bool>? focused = null, Func<Point>? position = null,
        Func<TimeSpan>? now = null, Action<bool>? setHidden = null)
    {
        _focused = focused ?? ApplicationHasFocus;
        _position = position ?? (() => Forms.Cursor.Position);
        var started = Stopwatch.GetTimestamp();
        _now = now ?? (() => Stopwatch.GetElapsedTime(started));
        _setHidden = setHidden ?? (hidden => { if (hidden) Forms.Cursor.Hide(); else Forms.Cursor.Show(); });
        _timer = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += Tick;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Poll();
        _timer.Start();
    }

    private void Tick(object? sender, EventArgs args) => Poll();

    internal void Poll()
    {
        if (_disposed) return;
        var focused = _focused();
        var position = _position();
        var now = _now();
        if (!focused || !_wasFocused || position != _lastPosition)
            _lastMovement = now;
        _lastPosition = position;
        _wasFocused = focused;
        SetHidden(focused && now - _lastMovement >= IdleDelay);
    }

    private void SetHidden(bool hidden)
    {
        if (_hidden == hidden) return;
        _setHidden(hidden);
        _hidden = hidden;
    }

    private static bool ApplicationHasFocus()
    {
        var foreground = GetForegroundWindow();
        if (foreground == nint.Zero) return false;
        GetWindowThreadProcessId(foreground, out var process);
        return process == Environment.ProcessId;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Tick;
        SetHidden(false);
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
}
