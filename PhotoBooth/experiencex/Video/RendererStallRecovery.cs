using PhotoBooth.Diagnostics;

namespace ExperienceX;

// Lives outside the UI/render workers. The existing external restart script owns process termination.
internal sealed class RendererStallRecovery : IDisposable
{
    private readonly Func<RenderHealth[]> _health;
    private readonly Func<RendererRecoveryOptions> _options;
    private readonly Action _restart;
    private readonly Func<DateTimeOffset> _now;
    private readonly System.Threading.Timer? _timer;
    private int _checking, _requested, _failed, _disposed;
    private DateTimeOffset _retryAt;
    private string? _lastError;
    internal RendererStallRecovery(Func<RenderHealth[]> health, Func<RendererRecoveryOptions> options, Action restart,
        bool monitor = true, Func<DateTimeOffset>? now = null)
    {
        _health = health; _options = options; _restart = restart; _now = now ?? (() => DateTimeOffset.UtcNow);
        if (monitor) _timer = new(_ => Check(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }
    internal void RestartFailed()
    {
        if (Volatile.Read(ref _requested) != 0) Interlocked.Exchange(ref _failed, 1);
    }
    internal void Check()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _checking, 1) != 0) return;
        try
        {
            var options = _options(); var now = _now();
            if (Interlocked.Exchange(ref _failed, 0) != 0) { Volatile.Write(ref _requested, 0); _retryAt = now.AddSeconds(60); }
            if (!options.Enabled || Volatile.Read(ref _requested) != 0 || now < _retryAt) return;
            var stalled = _health().Where(h => h.Status == "Stalled" && !h.Suspended && h.StallAgeSeconds >= options.StallTimeoutSeconds).ToArray();
            if (stalled.Length == 0) return;
            if (Volatile.Read(ref _disposed) != 0) return;
            Volatile.Write(ref _requested, 1);
            Telemetry.Warning("RendererStallRestartRequested", new { options.StallTimeoutSeconds, Displays = stalled });
            _restart();
            _lastError = null;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _requested, 0); _retryAt = _now().AddSeconds(60);
            if (_lastError != ex.Message) { _lastError = ex.Message; Telemetry.Error("RendererStallRecoveryFailed", ex); }
        }
        finally { Volatile.Write(ref _checking, 0); }
    }
    public void Dispose() { Volatile.Write(ref _disposed, 1); _timer?.Dispose(); }
}
