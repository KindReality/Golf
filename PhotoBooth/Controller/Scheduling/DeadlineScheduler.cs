using System.Diagnostics;
using Controller.Diagnostics;
namespace Controller.Scheduling;

internal sealed class DeadlineScheduler : IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly SchedulerWait _wait;
    private readonly PriorityQueue<Work, (long At, long Order)> _queue = new();
    private readonly Thread _thread;
    private readonly ActivityLog _log;
    private long _sequence;
    private bool _stopping;
    public DeadlineScheduler(ActivityLog log)
    {
        _log = log; _wait = new(_wake, log);
        _thread = new Thread(Loop) { IsBackground = true, Name = "Controller deadline scheduler" }; _thread.Start();
    }
    public void Post(Action action) => Enqueue(new(action, null), Stopwatch.GetTimestamp());
    public Task UntilAsync(long timestamp, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var completion = new TaskCompletionSource();
        Enqueue(new(null, completion), timestamp); return AwaitAsync(completion, token);
    }
    private async Task AwaitAsync(TaskCompletionSource completion, CancellationToken token)
    {
        using var registration = token.Register(() => { completion.TrySetCanceled(token); _wake.Set(); });
        await completion.Task.ConfigureAwait(false);
    }
    private void Enqueue(Work work, long at)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_queue.Count > 4096) { var live = _queue.UnorderedItems.Where(w => w.Element.Completion?.Task.IsCompleted != true).ToArray(); _queue.Clear(); foreach (var item in live) _queue.Enqueue(item.Element, item.Priority); }
            _queue.Enqueue(work, (at, ++_sequence)); _wake.Set();
        }
    }
    private void Loop()
    {
        while (true)
        {
            Work? due = null; long? next = null;
            lock (_gate)
            {
                if (_stopping) return;
                while (_queue.TryPeek(out var stale, out _) && stale.Completion?.Task.IsCompleted == true) _queue.Dequeue();
                if (_queue.TryPeek(out _, out var priority)) { if (priority.At <= Stopwatch.GetTimestamp()) due = _queue.Dequeue(); else next = priority.At; }
            }
            if (due is null) { if (next is { } timestamp) _wait.Until(timestamp); else _wake.WaitOne(); continue; }
            try { due.Action?.Invoke(); due.Completion?.TrySetResult(); } catch (Exception ex) { _log.Error("ScheduledActionFailed", ex); }
        }
    }
    internal static long After(int milliseconds) => Stopwatch.GetTimestamp() + Ticks(milliseconds);
    internal static long Ticks(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000);
    public void Dispose()
    {
        Work[] pending;
        lock (_gate) { _stopping = true; pending = _queue.UnorderedItems.Select(w => w.Element).ToArray(); _queue.Clear(); _wake.Set(); }
        _thread.Join(); foreach (var work in pending) work.Completion?.TrySetCanceled(); _wait.Dispose(); _wake.Dispose();
    }
    private sealed record Work(Action? Action, TaskCompletionSource? Completion);
}
