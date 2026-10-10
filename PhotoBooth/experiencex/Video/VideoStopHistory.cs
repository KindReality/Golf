using System.Diagnostics;

namespace ExperienceX;

// Bounded tombstones suppress a stopped play request that arrives after its stop over UDP.
internal sealed class VideoStopHistory
{
    private readonly Dictionary<(string Source, string Id), long> _stops = [];
    private readonly PriorityQueue<(string Source, string Id), long> _order = new();
    private static (string, string) Key(string? source, string id) => ((source ?? "").Trim().ToUpperInvariant(), id);
    public void Record(string? source, string id, double seconds, long? now = null)
    {
        var at = now ?? Stopwatch.GetTimestamp();
        Prune(at);
        var key = Key(source, id);
        if (_stops.ContainsKey(key)) return;
        while (_stops.Count >= 4096) _stops.Remove(_order.Dequeue());
        var expires = at + (long)(seconds * Stopwatch.Frequency);
        _stops.Add(key, expires);
        _order.Enqueue(key, expires);
    }
    public bool Contains(string? source, string id, long? now = null)
    {
        var at = now ?? Stopwatch.GetTimestamp();
        Prune(at);
        return _stops.TryGetValue(Key(source, id), out var expires) && expires > at;
    }
    private void Prune(long now)
    {
        // Retention settings can change live, so expire by deadline rather than insertion order.
        while (_order.TryPeek(out var key, out var expires) && expires <= now)
        { _order.Dequeue(); _stops.Remove(key); }
    }
}
