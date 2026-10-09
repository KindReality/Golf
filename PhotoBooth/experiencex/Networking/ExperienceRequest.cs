using System.Text.Json;

namespace ExperienceX;

public sealed record ExperienceRequest
{
    public string Experience { get; init; } = "";
    public string Value { get; init; } = "";
    public string? MonitorName
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
        if (string.IsNullOrWhiteSpace(request.Value))
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
