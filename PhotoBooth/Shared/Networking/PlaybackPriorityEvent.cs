using System.Text.Json;

namespace PhotoBooth.Networking;

internal static class PlaybackPriorityEvent
{
    internal const string EventName = "playback.priorityRejected";
    internal static byte[] Create(BroadcastOptions options, string commandSource, string commandRequestId, string value,
        string display, string displayLabel, string screen, CommandPriority requestedPriority, CommandPriority activePriority,
        bool deferred, CommandContext? context = null, BroadcastChain? chain = null) =>
        BroadcastEnvelope.Create(options, "event", "*", new
        {
            input = "playback", @event = EventName, experience = "video", reason = "lowerPriority",
            commandSource, commandRequestId, value, display, displayLabel, screen,
            requestedPriority, activePriority, deferred
        }, chain: chain, context: context);

    internal static bool TryDescribe(BroadcastEnvelope message, out string text)
    {
        text = "";
        if (message.Type != "event" || message.Event != EventName) return false;
        string Read(string key) => message.Body.TryGetProperty(key, out var node) && node.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(node.GetString()) ? node.GetString()! : throw new ArgumentException($"Missing priority event {key}.");
        if (Read("experience") != "video" || Read("reason") != "lowerPriority") throw new ArgumentException("Invalid priority event.");
        var requested = message.Body.GetProperty("requestedPriority").Deserialize<CommandPriority>();
        var active = message.Body.GetProperty("activePriority").Deserialize<CommandPriority>();
        if (requested >= active) throw new ArgumentException("Priority rejection requires a higher active priority.");
        var value = Read("value").Replace('\\', '/');
        var file = value[(value.LastIndexOf('/') + 1)..];
        var action = message.Body.TryGetProperty("deferred", out var deferred) && deferred.ValueKind == JsonValueKind.True ? "Deferred" : "Ignored";
        text = $"{action} {file} on {message.Source}/{Read("displayLabel")}/{Read("screen")} ({requested.ToString().ToLowerInvariant()} < {active.ToString().ToLowerInvariant()})";
        return true;
    }
}
