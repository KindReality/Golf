using System.Text.Json;

namespace Experience;

public sealed record ExperienceRequest
{
    public string Experience { get; init; } = "";
    public string Value { get; init; } = "";
    public string? MonitorName { get; init; }
    public string? AudioDevice { get; init; }
    public int? Channel { get; init; }
    public bool Mute { get; init; }
    public string? Source { get; init; }
    public string RequestId { get; init; } = "";

    public static ExperienceRequest Parse(ReadOnlySpan<byte> json)
    {
        var request = JsonSerializer.Deserialize<ExperienceRequest>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new JsonException("Request must be an object.");
        if (!string.Equals(request.Experience, "video", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Experience, "audio", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only video and audio experiences are supported.");
        if (string.IsNullOrWhiteSpace(request.Value)) throw new ArgumentException("A media filename is required.");
        if (request.Channel is <= 0) throw new ArgumentException("Channel numbers start at 1.");
        var id = string.IsNullOrWhiteSpace(request.RequestId) ? Guid.NewGuid().ToString("N") : request.RequestId;
        return request with { RequestId = id[..Math.Min(id.Length, 128)] };
    }
}
