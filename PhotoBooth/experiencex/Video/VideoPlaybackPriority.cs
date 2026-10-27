using PhotoBooth.Networking;

namespace ExperienceX;

// Reservations include queued opens. Priority is isolated to one display/screen.
internal sealed class VideoPlaybackPriority
{
    private readonly object _gate = new();
    private readonly HashSet<VideoPriorityLease> _active = [];
    internal CommandPriority? BlockingPriorityFor(CommandPriority requested, string display, string screen)
    {
        lock (_gate) return _active.Where(l => l.Priority > requested && l.Identity.Display.Equals(display, StringComparison.OrdinalIgnoreCase) &&
            l.Identity.Screen.Equals(screen, StringComparison.OrdinalIgnoreCase)).Select(l => (CommandPriority?)l.Priority).Max();
    }
    public VideoPriorityLease? TryBegin(CommandPriority priority, VideoPlaybackIdentity identity) => TryBeginBatch(priority, [identity])[0];
    public VideoPriorityLease?[] TryBeginBatch(CommandPriority priority, VideoPlaybackIdentity[] identities)
        => TryBeginBatch(priority, identities, out _);
    public VideoPriorityLease?[] TryBeginBatch(CommandPriority priority, VideoPlaybackIdentity[] identities, out CommandPriority?[] blockingPriorities)
    {
        blockingPriorities = new CommandPriority?[identities.Length];
        var interrupted = new List<VideoPriorityLease>();
        var result = new VideoPriorityLease?[identities.Length];
        lock (_gate)
        {
            for (int index = 0; index < identities.Length; index++)
            {
                var identity = identities[index];
                var existing = _active.Where(l => l.Identity.SameOutput(identity)).ToArray();
                if (existing.Any(l => l.Priority > priority))
                { blockingPriorities[index] = existing.Max(l => l.Priority); continue; }
                foreach (var previous in existing.Where(l => l.Identity.Path != identity.Path))
                { previous.Interrupt(); _active.Remove(previous); interrupted.Add(previous); }
                result[index] = new(this, priority, identity);
                _active.Add(result[index]!);
            }
        }
        foreach (var lease in interrupted) lease.Wake();
        return result;
    }
    internal void Release(VideoPriorityLease lease) { lock (_gate) _active.Remove(lease); }
}

internal sealed record VideoPlaybackIdentity(string Path, string Display, string Screen)
{
    internal bool SameOutput(VideoPlaybackIdentity other) => Display == other.Display && Screen == other.Screen;
    internal static VideoPlaybackIdentity For(string path, string display, ExperienceRequest request) =>
        new(System.IO.Path.GetFullPath(path).ToUpperInvariant(), display.ToUpperInvariant(), (request.ScreenId ?? "").ToUpperInvariant());
}

internal sealed class VideoPriorityLease(VideoPlaybackPriority owner, CommandPriority priority, VideoPlaybackIdentity identity) : IDisposable
{
    private int _disposed;
    private volatile bool _interrupted;
    private Action? _wake;
    public CommandPriority Priority { get; } = priority;
    internal VideoPlaybackIdentity Identity { get; } = identity;
    public bool IsInterrupted => _interrupted;
    internal void Interrupt() => _interrupted = true;
    internal void Wake() => Volatile.Read(ref _wake)?.Invoke();
    internal void WakeWhenInterrupted(Action wake)
    { Volatile.Write(ref _wake, wake); if (IsInterrupted) Wake(); }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _wake, null); owner.Release(this);
    }
}
