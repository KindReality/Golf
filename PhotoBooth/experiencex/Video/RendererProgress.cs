using System.Collections.Concurrent;
using System.Diagnostics;
using PhotoBooth.Diagnostics;

namespace ExperienceX;

internal sealed record ScreenRenderHealth(string ScreenId, string ScreenName, string? Media, string? RequestId,
    string State, bool Loop, long FramesTransferred, double PositionSeconds, double FrameAgeSeconds, bool Stalled, bool SoftwareVideoDecoding = false);
internal sealed record RenderHealth(string DisplayName, int ManagedThreadId, uint NativeThreadId, string Status,
    string Operation, string? OperationScreenId, double OperationAgeSeconds, double WorkerAgeSeconds,
    long PresentsSubmitted, double PresentAgeSeconds, int PendingCommands, bool Suspended, ScreenRenderHealth[] Screens, double StallAgeSeconds = 0);

// Only scalar/immutable data crosses threads. The watchdog never calls a decoder, GPU, or renderer mailbox.
internal sealed class RendererProgress : IDisposable
{
    private readonly string _display;
    private readonly Func<long> _now;
    private readonly Func<int> _pending;
    private readonly Action<RenderHealth, string> _capture;
    private readonly Func<double> _stallSeconds;
    private readonly ConcurrentDictionary<string, ScreenProgress> _screens = new();
    private readonly System.Threading.Timer? _timer;
    private string _operation = "Starting";
    private string? _operationScreen;
    private long _operationAt, _workerAt, _presentAt, _presents, _resumedAt;
    private int _managedThread, _nativeThread, _suspended, _stopped, _reported, _checking;
    internal const double StallSeconds = 10;
    internal static double WarningTimeout(double recoverySeconds) => Math.Min(StallSeconds, Math.Max(1, recoverySeconds - 2));
    internal bool IsSuspended => Volatile.Read(ref _suspended) != 0;
    internal string? OperationScreenId => Volatile.Read(ref _operationScreen);

    internal RendererProgress(string display, Func<int> pending, Action<RenderHealth, string> capture,
        bool monitor = true, Func<long>? now = null, Func<double>? stallSeconds = null)
    {
        _display = display; _pending = pending; _capture = capture; _now = now ?? Stopwatch.GetTimestamp;
        _stallSeconds = stallSeconds ?? (() => StallSeconds);
        _operationAt = _workerAt = _presentAt = _resumedAt = _now();
        if (monitor) _timer = new(_ => Check(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }
    internal void RegisterThread(int managed, uint native) { _managedThread = managed; _nativeThread = unchecked((int)native); Beat(); }
    internal void Beat() => Interlocked.Exchange(ref _workerAt, _now());
    internal void Suspend(bool value)
    {
        if (value) Volatile.Write(ref _suspended, 1);
        else if (Volatile.Read(ref _suspended) != 0)
        {
            Interlocked.Exchange(ref _resumedAt, _now());
            Volatile.Write(ref _suspended, 0);
        }
    }
    internal OperationScope Begin(string operation, string? screen = null)
    {
        var scope = new OperationScope(this, _operation, _operationScreen, Interlocked.Read(ref _operationAt));
        Volatile.Write(ref _operationScreen, screen);
        Interlocked.Exchange(ref _operationAt, _now());
        Volatile.Write(ref _operation, operation);
        return scope;
    }
    internal readonly struct OperationScope(RendererProgress owner, string previous, string? screen, long at) : IDisposable
    {
        public void Dispose()
        {
            Volatile.Write(ref owner._operationScreen, screen);
            Interlocked.Exchange(ref owner._operationAt, at);
            Volatile.Write(ref owner._operation, previous);
        }
    }
    internal void StartVideo(ScreenOptions screen, string path, ExperienceRequest request, bool softwareVideoDecoding = false) =>
        _screens[screen.Id] = new(screen.Name, path, request.RequestId, request.Loop, _now(), softwareVideoDecoding);
    internal void ContinueVideo(string id, ExperienceRequest request)
    {
        if (_screens.TryGetValue(id, out var screen)) Volatile.Write(ref screen.RequestId, request.RequestId);
    }
    internal void FrameTransferred(string id)
    {
        if (!_screens.TryGetValue(id, out var screen)) return;
        Interlocked.Increment(ref screen.Frames);
        Interlocked.Exchange(ref screen.FrameAt, _now());
    }
    internal void Position(string id, double value)
    {
        if (_screens.TryGetValue(id, out var screen)) Volatile.Write(ref screen.Position, value);
    }
    internal void Playing(string id) { if (_screens.TryGetValue(id, out var screen)) Volatile.Write(ref screen.State, "Playing"); }
    internal void Stopping(string id) { if (_screens.TryGetValue(id, out var screen)) Volatile.Write(ref screen.State, "Stopping"); }
    internal void EndVideo(string id) => _screens.TryRemove(id, out _);
    internal void Presented() { Interlocked.Increment(ref _presents); Interlocked.Exchange(ref _presentAt, _now()); }
    internal RenderHealth Snapshot()
    {
        var now = _now();
        double Age(long at) => Math.Max(0, Stopwatch.GetElapsedTime(at, now).TotalSeconds);
        var suspended = Volatile.Read(ref _suspended) != 0;
        var stopped = Volatile.Read(ref _stopped) != 0;
        var operation = Volatile.Read(ref _operation);
        var workerAge = Age(Interlocked.Read(ref _workerAt));
        var resumeAge = Age(Interlocked.Read(ref _resumedAt));
        var stallSeconds = _stallSeconds();
        var screens = _screens.Select(pair =>
        {
            var s = pair.Value;
            var state = Volatile.Read(ref s.State);
            var age = Age(Interlocked.Read(ref s.FrameAt));
            var stalled = !suspended && !stopped && state != "Stopping" && Math.Min(age, resumeAge) >= (state == "Opening" ? 20 : stallSeconds);
            return new ScreenRenderHealth(pair.Key, s.Name, s.Path, Volatile.Read(ref s.RequestId), state, s.Loop,
                Interlocked.Read(ref s.Frames), Volatile.Read(ref s.Position), age, stalled, s.SoftwareVideoDecoding);
        }).ToArray();
        // First device/composition setup can be slower than steady-state rendering.
        var workerThreshold = Interlocked.Read(ref _presents) == 0 ? 20 : stallSeconds;
        var stalledWorker = !suspended && !stopped && operation != "IdleWait" && Math.Min(workerAge, resumeAge) >= workerThreshold;
        var status = stopped ? "Stopped" : suspended ? "Suspended" : stalledWorker || screens.Any(s => s.Stalled) ? "Stalled" : screens.Length > 0 ? "Active" : "Idle";
        var stallAge = Math.Max(stalledWorker ? Math.Min(workerAge, resumeAge) : 0,
            screens.Where(s => s.Stalled).Select(s => Math.Min(s.FrameAgeSeconds, resumeAge)).DefaultIfEmpty(0).Max());
        return new(_display, _managedThread, unchecked((uint)_nativeThread), status, operation, Volatile.Read(ref _operationScreen),
            Age(Interlocked.Read(ref _operationAt)), workerAge, Interlocked.Read(ref _presents), Age(Interlocked.Read(ref _presentAt)), _pending(), suspended, screens, stallAge);
    }
    internal void Check()
    {
        if (Interlocked.Exchange(ref _checking, 1) != 0) return;
        try
        {
            var health = Snapshot();
            if (health.Status is "Suspended" or "Stopped") return;
            if (health.Status == "Stalled")
            {
                if (Interlocked.Exchange(ref _reported, 1) == 0)
                {
                    Telemetry.Warning("RendererProgressStalled", health);
                    _capture(health, "ProgressStalled");
                }
            }
            else if (Interlocked.Exchange(ref _reported, 0) != 0)
                Telemetry.Info("RendererProgressResumed", health);
        }
        catch (Exception ex) { Telemetry.Error("RendererDiagnosticsFailed", ex, new { DisplayName = _display }); }
        finally { Volatile.Write(ref _checking, 0); }
    }
    internal void ShutdownTimedOut()
    {
        var health = Snapshot();
        Telemetry.Warning("RendererShutdownProgress", health);
    }
    public void Dispose() { Volatile.Write(ref _stopped, 1); _timer?.Dispose(); }

    private sealed class ScreenProgress(string name, string path, string? request, bool loop, long now, bool softwareVideoDecoding)
    {
        internal readonly string Name = name, Path = path;
        internal readonly bool Loop = loop;
        internal readonly bool SoftwareVideoDecoding = softwareVideoDecoding;
        internal string? RequestId = request;
        internal string State = "Opening";
        internal long FrameAt = now, Frames;
        internal double Position;
    }
}
