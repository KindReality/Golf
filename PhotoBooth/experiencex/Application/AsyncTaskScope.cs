using PhotoBooth.Diagnostics;

namespace ExperienceX;

// Start/stop are called by the owning dispatcher; completion may occur on any thread.
internal sealed class AsyncTaskScope(string owner) : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly HashSet<Task> _tasks = [];
    private readonly object _gate = new();
    private bool _stopping;

    public CancellationToken Token => _cancellation.Token;
    public void Cancel()
    {
        lock (_gate)
        {
            if (_stopping)
                return;
        }
        _cancellation.Cancel();
    }

    public void Run(Func<CancellationToken, Task> operation, string name)
    {
        lock (_gate)
        {
            if (_stopping || _cancellation.IsCancellationRequested)
                return;
        }
        Task task;
        try
        {
            task = operation(Token);
        }
        catch (Exception ex) { Telemetry.Error("BackgroundOperationFailed", ex, new { Owner = owner, Operation = name }); return; }
        lock (_gate)
            _tasks.Add(task);
        _ = ObserveAsync(task, name);
    }

    private async Task ObserveAsync(Task task, string name)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception ex) { Telemetry.Error("BackgroundOperationFailed", ex, new { Owner = owner, Operation = name }); }
        finally { lock (_gate) _tasks.Remove(task); }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (_gate)
        {
            if (_stopping)
                return;
            _stopping = true;
            tasks = _tasks.ToArray();
        }
        _cancellation.Cancel();
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { Telemetry.Warning("BackgroundShutdownTimedOut", new { Owner = owner, TimeoutSeconds = 5 }); }
        catch { /* Each failure is reported by ObserveAsync. */ }
        // Keep the token source alive for operations that exceeded the bounded wait.
        if (tasks.All(task => task.IsCompleted))
            _cancellation.Dispose();
    }
}
