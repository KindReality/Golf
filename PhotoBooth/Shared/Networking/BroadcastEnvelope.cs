using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhotoBooth.Networking;

// Media/action fields belong to the receiving application; this envelope is shared by all peers.
internal sealed record BroadcastEnvelope(JsonElement Body, string Type, string Device, string Source,
    string RequestId, string? Event, DateTimeOffset? ExpiresAt, int HopCount,
    double ProcessingTimeoutSeconds, long ReceivedTimestamp)
{
    public const int MaximumDatagramBytes = 1200;
    public const int MaximumHops = 8;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record Fields
    {
        public string Type { get; init; } = "command";
        public string? Device { get; init; }
        public string? Source { get; init; }
        public string? RequestId { get; init; }
        public string? Event { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public int HopCount { get; init; }
        public double ProcessingTimeoutSeconds { get; init; } = 10;
    }

    public static BroadcastEnvelope Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumDatagramBytes)
            throw new ArgumentException($"UDP messages must not exceed {MaximumDatagramBytes} UTF-8 bytes.");
        var body = JsonSerializer.Deserialize<JsonElement>(bytes, Json);
        if (body.ValueKind != JsonValueKind.Object)
            throw new JsonException("A message must be an object.");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (body.EnumerateObject().Any(property => !keys.Add(property.Name)))
            throw new ArgumentException("Duplicate message fields are not allowed.");
        var fields = body.Deserialize<Fields>(Json)!;
        var type = fields.Type?.Trim().ToLowerInvariant();
        if (type is not ("command" or "event"))
            throw new ArgumentException("Message type must be command or event.");
        BroadcastOptions.ValidateName(fields.Device, "device", true);
        BroadcastOptions.ValidateName(fields.Source, "source", false);
        BroadcastOptions.ValidateName(fields.RequestId, "requestId", false);
        if (type == "event")
            BroadcastOptions.ValidateName(fields.Event, "event", false);
        if (fields.HopCount is < 0 or > MaximumHops)
            throw new ArgumentException($"hopCount must be between 0 and {MaximumHops}.");
        if (!double.IsFinite(fields.ProcessingTimeoutSeconds) || fields.ProcessingTimeoutSeconds is <= 0 or > 300)
            throw new ArgumentException("processingTimeoutSeconds must be greater than zero and at most 300.");
        return new(body, type, fields.Device!.Trim(), fields.Source!.Trim(), fields.RequestId!.Trim(),
            fields.Event?.Trim(), fields.ExpiresAt, fields.HopCount, fields.ProcessingTimeoutSeconds, Stopwatch.GetTimestamp());
    }

    public bool Targets(string name) => Device == "*" || string.Equals(Device, name, StringComparison.OrdinalIgnoreCase);
    public bool IsExpired(DateTimeOffset now, long? timestamp = null) =>
        ExpiresAt is { } deadline && deadline <= now ||
        Stopwatch.GetElapsedTime(ReceivedTimestamp, timestamp ?? Stopwatch.GetTimestamp()).TotalSeconds >= ProcessingTimeoutSeconds;

    public static byte[] Create(BroadcastOptions options, string type, string device, object payload, string? requestId = null)
    {
        options.ValidateNetwork();
        var body = JsonSerializer.SerializeToNode(payload, Json) as JsonObject ?? throw new ArgumentException("Payload must be an object.");
        // Reserve envelope fields so callers cannot accidentally override routing or retry identity.
        foreach (var key in body.Select(property => property.Key).ToArray())
            if (new[] { "type", "device", "source", "requestId", "expiresAt", "hopCount", "processingTimeoutSeconds" }.Contains(key, StringComparer.OrdinalIgnoreCase))
                body.Remove(key);
        body["type"] = type;
        body["device"] = device;
        body["source"] = options.EffectiveDeviceName;
        body["requestId"] = requestId ?? Guid.NewGuid().ToString("N");
        // Local monotonic time avoids requiring clock synchronization on an offline LAN.
        body["processingTimeoutSeconds"] = options.RequestLifetimeSeconds;
        body["hopCount"] = 0;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        Parse(bytes);
        return bytes;
    }
}
