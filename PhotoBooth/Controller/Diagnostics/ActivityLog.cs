using System.Collections.Concurrent;
using PhotoBooth.Diagnostics;

namespace Controller.Diagnostics;

internal sealed record ActivityEntry(DateTimeOffset At, string Level, string Message, string Kind = "Activity");
internal sealed class ActivityLog
{
    private readonly ConcurrentQueue<ActivityEntry> _entries = new();
    public ActivityEntry[] Snapshot() => _entries.Reverse().ToArray();
    public void Clear() => _entries.Clear();
    public void Info(string name, string message, object? data = null, string? id = null)
    {
        Telemetry.Info(name, data ?? new { Message = message }, id);
        Add("Information", message, name);
    }
    public void Warning(string name, string message, object? data = null, string? id = null)
    {
        Telemetry.Warning(name, data ?? new { Message = message }, id);
        Add("Warning", message, name);
    }
    public void Error(string name, Exception error, object? data = null, string? id = null)
    {
        Telemetry.Error(name, error, data, id);
        Add("Error", $"{name}: {error.Message}", name);
    }
    private void Add(string level, string message, string name)
    {
        var kind = name is "NetworkEventReceived" or "PlaybackPriorityRejected" or "ArduinoEventReceived" or "ManualEventSent" ? "Event" :
            name is "CommandSent" or "ManualCommandSent" or "ManualPlaylistStarted" or "LivePlaybackSent" or "ArduinoNetworkCommandSent" or "CommandObserved" ? "Command" : "Activity";
        _entries.Enqueue(new(DateTimeOffset.Now, level, message, kind));
        while (_entries.Count > 250) _entries.TryDequeue(out _);
    }
}
