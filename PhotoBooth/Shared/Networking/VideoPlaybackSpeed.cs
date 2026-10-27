using System.Text.Json;

namespace PhotoBooth.Networking;

internal static class VideoPlaybackSpeed
{
    internal const double Minimum = .5, Maximum = 2;
    internal static void Validate(double speed)
    {
        if (!double.IsFinite(speed) || speed < Minimum || speed > Maximum)
            throw new ArgumentException("Video playbackSpeed must be from 0.5 to 2.0; 1.0 is normal speed.");
    }
    internal static double Read(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return 1;
        var field = payload.EnumerateObject().FirstOrDefault(p => p.Name.Equals("playbackSpeed", StringComparison.OrdinalIgnoreCase)).Value;
        if (field.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return 1;
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetDouble(out var speed)) throw new ArgumentException("Video playbackSpeed must be a number.");
        Validate(speed);
        string? Text(string name) => payload.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } node ? node.GetString() : null;
        if (!string.Equals(Text("experience"), "video", StringComparison.OrdinalIgnoreCase) || Text("command")?.ToLowerInvariant() is not (null or "play"))
            throw new ArgumentException("playbackSpeed applies only to video playback commands.");
        return speed;
    }
}
