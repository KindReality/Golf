using System.Diagnostics;

namespace ExperienceX;

internal sealed class GraphicsPresentationTimeoutException(string message) : TimeoutException(message);

// An elapsed deadline across repeated short waits, not a timeout restarted on every iteration.
internal sealed class PresentationDeadline
{
    private long? _since;
    internal void Reset() => _since = null;
    internal void Check(long now, bool available, bool suspended)
    {
        if (available || suspended) { Reset(); return; }
        _since ??= now;
        if (Stopwatch.GetElapsedTime(_since.Value, now) >= TimeSpan.FromSeconds(5))
            throw new GraphicsPresentationTimeoutException("No DXGI presentation buffer became available within five seconds.");
    }
}
