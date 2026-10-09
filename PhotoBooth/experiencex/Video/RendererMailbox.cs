namespace ExperienceX;

// A fixed set of command categories, each retaining only its newest pending value.
internal sealed class RendererMailbox
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (Action Run, Action? Replaced)> _pending = [];
    private readonly Queue<string> _order = [];
    private bool _closed;
    internal const int Capacity = 16;
    public void Post(string key, Action run, Action? replaced = null)
    {
        Action? discard = null;
        lock (_gate)
        {
            if (_closed)
                discard = replaced;
            else if (_pending.TryGetValue(key, out var old))
            {
                _pending[key] = (run, replaced);
                discard = old.Replaced;
            }
            else if (_pending.Count < Capacity)
            {
                _pending.Add(key, (run, replaced));
                _order.Enqueue(key);
            }
            else
                throw new InvalidOperationException("Renderer command category limit exceeded.");
        }
        discard?.Invoke();
    }
    public bool TryTake(out Action action)
    {
        lock (_gate)
        {
            if (_order.TryDequeue(out var key))
            {
                action = _pending[key].Run;
                _pending.Remove(key);
                return true;
            }
        }
        action = null!;
        return false;
    }
    public int Count
    {
        get
        {
            lock (_gate)
                return _pending.Count;
        }
    }
    public void Close()
    {
        Action?[] callbacks;
        lock (_gate)
        {
            _closed = true;
            callbacks = _pending.Values.Select(p => p.Replaced).ToArray();
            _pending.Clear();
            _order.Clear();
        }
        foreach (var callback in callbacks)
            callback?.Invoke();
    }
}
