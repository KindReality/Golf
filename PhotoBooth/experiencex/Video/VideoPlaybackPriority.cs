namespace ExperienceX;

// Device-wide video priority, including requests still queued for a display worker.
// Each output owns a lease until playback ends, fails, is replaced, or is discarded.
internal sealed class VideoPlaybackPriority
{
    private readonly object _gate = new();
    private readonly HashSet<VideoPriorityLease> _active = [];

    public VideoPriorityLease? TryBegin(bool ambient, VideoLoopIdentity? looping = null) => TryBeginBatch(ambient, [looping])?[0];

    public VideoPriorityLease[]? TryBeginBatch(bool ambient, VideoLoopIdentity?[] looping)
    {
        if (looping.Length == 0) return [];
        VideoPriorityLease[] interrupted;
        VideoPriorityLease[] leases;
        lock (_gate)
        {
            if (ambient && _active.Any(item => !item.IsAmbient)) return null;
            // The destination's identical loop can be promoted without closing its decoder.
            interrupted = ambient ? [] : _active.Where(item => item.IsAmbient && (item.Looping is null || !looping.Contains(item.Looping))).ToArray();
            foreach (var item in interrupted)
            {
                item.Interrupt();
                _active.Remove(item);
            }
            leases = looping.Select(identity => new VideoPriorityLease(this, ambient, identity)).ToArray();
            foreach (var lease in leases) _active.Add(lease);
        }
        foreach (var item in interrupted) item.Wake();
        return leases;
    }

    internal void Release(VideoPriorityLease lease)
    {
        lock (_gate) _active.Remove(lease);
    }
}

internal sealed record VideoLoopIdentity(string Path, string Display, string Screen, bool Mute)
{
    internal static VideoLoopIdentity? For(string path, string display, ExperienceRequest request) => !request.Loop ? null :
        new(System.IO.Path.GetFullPath(path).ToUpperInvariant(), display.ToUpperInvariant(), (request.ScreenId ?? "").ToUpperInvariant(), request.Mute);
}

internal sealed class VideoPriorityLease(VideoPlaybackPriority owner, bool ambient, VideoLoopIdentity? looping) : IDisposable
{
    private int _disposed;
    private volatile bool _interrupted;
    private Action? _wake;
    public bool IsAmbient { get; } = ambient;
    internal VideoLoopIdentity? Looping { get; } = looping;
    public bool IsInterrupted => _interrupted;
    internal void Interrupt() => _interrupted = true;
    internal void Wake() => Volatile.Read(ref _wake)?.Invoke();
    internal void WakeWhenInterrupted(Action wake)
    {
        Volatile.Write(ref _wake, wake);
        if (IsInterrupted) Wake();
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _wake, null);
        owner.Release(this);
    }
}
