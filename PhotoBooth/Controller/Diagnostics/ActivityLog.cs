using System.Collections.Concurrent;
using PhotoBooth.Diagnostics;

namespace Controller.Diagnostics;

internal sealed record ActivityEntry(DateTimeOffset At, string Level, string Message);
internal sealed class ActivityLog
{
    private readonly ConcurrentQueue<ActivityEntry> _entries = new();
    public ActivityEntry[] Snapshot() => _entries.Reverse().ToArray();
    public void Info(string name, string message, object? data = null, string? id = null)
    {
        Telemetry.Info(name, data ?? new { Message = message }, id);
        Add("Information", message);
    }
    public void Warning(string name, string message, object? data = null, string? id = null)
    {
        Telemetry.Warning(name, data ?? new { Message = message }, id);
        Add("Warning", message);
    }
    public void Error(string name, Exception error, object? data = null, string? id = null)
    {
        Telemetry.Error(name, error, data, id);
        Add("Error", $"{name}: {error.Message}");
    }
    private void Add(string level, string message)
    {
        _entries.Enqueue(new(DateTimeOffset.Now, level, message));
        while (_entries.Count > 250) _entries.TryDequeue(out _);
    }
}
