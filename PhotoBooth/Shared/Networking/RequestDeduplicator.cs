using System.Diagnostics;

namespace PhotoBooth.Networking;

// Logical sender identity remains stable across UDP ports and IP address changes.
internal sealed class RequestDeduplicator(int capacity = 4096)
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Sender, string Id), long> _seen = [];
    private readonly Queue<((string Sender, string Id) Key, long At)> _order = [];
    public bool Accept(string sender, string id, double seconds, long? timestamp = null)
    {
        if (seconds <= 0)
            return true;
        var now = timestamp ?? Stopwatch.GetTimestamp();
        var lifetime = (long)(seconds * Stopwatch.Frequency);
        lock (_gate)
        {
            while (_order.TryPeek(out var head) && now - head.At >= lifetime)
            {
                _order.Dequeue();
                _seen.Remove(head.Key);
            }
            var key = (sender, id);
            if (_seen.ContainsKey(key))
                return false;
            while (_seen.Count >= capacity)
            {
                _seen.Remove(_order.Dequeue().Key);
            }
            _seen[key] = now;
            _order.Enqueue((key, now));
            return true;
        }
    }
    internal int Count
    {
        get
        {
            lock (_gate)
                return _seen.Count;
        }
    }
}
