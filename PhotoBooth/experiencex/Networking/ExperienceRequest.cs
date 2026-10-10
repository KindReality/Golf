using System.Text.Json;

namespace ExperienceX;

public sealed record ExperienceRequest
{
    public string Experience { get; init; } = "";
    public string Value { get; init; } = "";
    public bool Loop { get; init; }
    public string? Command { get; init; }
    public string? PlaybackRequestId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsStop => string.Equals(Command, "stop", StringComparison.OrdinalIgnoreCase);
    public string? Screen
    {
        get; init;
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ScreenId
    {
        get; init;
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ScreenName
    {
        get; init;
    }
    public string? DisplayName
    {
        get; init;
    }
    public string? AudioDevice
    {
        get; init;
    }
    public string[]? AudioDevices
    {
        get; init;
    }
    public int? Channel
    {
        get; init;
    }
    public bool Mute
    {
        get; init;
    }
    public double? Volume
    {
        get; init;
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public double VolumePercent => Volume ?? 100;
    public string? Source
    {
        get; init;
    }
    public string RequestId { get; init; } = "";

    public static ExperienceRequest Parse(ReadOnlySpan<byte> json)
        => Validate(JsonSerializer.Deserialize<ExperienceRequest>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new JsonException("Request must be an object."));

    internal static ExperienceRequest Parse(JsonElement json)
        => Validate(json.Deserialize<ExperienceRequest>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new JsonException("Request must be an object."));

    private static ExperienceRequest Validate(ExperienceRequest request)
    {
        if (!string.Equals(request.Experience, "video", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Experience, "audio", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only video and audio experiences are supported.");
        if (request.Command is not null && !string.Equals(request.Command, "play", StringComparison.OrdinalIgnoreCase) && !request.IsStop)
            throw new ArgumentException("Video commands support play or stop.");
        if (request.IsStop && (!string.Equals(request.Experience, "video", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(request.PlaybackRequestId) || request.PlaybackRequestId.Length > 128 || request.Loop))
            throw new ArgumentException("Stopping video requires playbackRequestId and cannot enable looping.");
        if (!request.IsStop && request.PlaybackRequestId is not null)
            throw new ArgumentException("playbackRequestId is only used to stop an existing video.");
        if (request.Loop && !string.Equals(request.Experience, "video", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Looping is supported for video only.");
        if (!request.IsStop && string.IsNullOrWhiteSpace(request.Value))
            throw new ArgumentException("A media filename is required.");
        if (request.Channel is <= 0)
            throw new ArgumentException("Channel numbers start at 1.");
        if (request.AudioDevices is not null && (request.AudioDevices.Length is 0 or > 32 ||
            request.AudioDevices.Any(string.IsNullOrWhiteSpace) || !string.IsNullOrWhiteSpace(request.AudioDevice)))
            throw new ArgumentException("Supply either audioDevice or a nonempty audioDevices array of up to 32 names/IDs.");
        if (!double.IsFinite(request.VolumePercent) || request.VolumePercent is < 0 or > 100)
            throw new ArgumentException("Volume must be a percentage from 0 to 100.");
        var id = string.IsNullOrWhiteSpace(request.RequestId) ? Guid.NewGuid().ToString("N") : request.RequestId;
        return request with
        {
            RequestId = id[..Math.Min(id.Length, 128)]
        };
    }
}
