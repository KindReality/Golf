using System.Text.Json;

namespace PhotoBooth.Networking;

internal static class VideoFadeCommand
{
    internal static bool IsFade(string? command) => command?.ToLowerInvariant() is "fadein" or "fadeout";
    internal static double Target(string? command) => string.Equals(command, "fadeIn", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    internal static void Validate(string? experience, double? durationSeconds, bool loop)
    {
        if (!string.Equals(experience, "video", StringComparison.OrdinalIgnoreCase) || loop ||
            durationSeconds is null || !double.IsFinite(durationSeconds.Value) || durationSeconds is < 0 or > 86400)
            throw new ArgumentException("Video fadeIn/fadeOut requires durationSeconds from 0 to 86400 and cannot loop.");
    }
    internal static void Validate(JsonElement body)
    {
        JsonElement Field(string name) => body.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        var duration = Field("durationSeconds");
        var loop = Field("loop");
        if (loop.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("Video fade loop must be a boolean when supplied.");
        Validate(Field("experience").ValueKind == JsonValueKind.String ? Field("experience").GetString() : null,
            duration.ValueKind == JsonValueKind.Number && duration.TryGetDouble(out var seconds) ? seconds : null, Field("loop").ValueKind == JsonValueKind.True);
    }
}
