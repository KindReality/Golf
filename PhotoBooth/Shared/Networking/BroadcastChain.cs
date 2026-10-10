using System.Text.Json;

namespace PhotoBooth.Networking;

// Local actions carry their event ancestry; each individual send has a new request ID.
internal sealed record BroadcastChain(string CorrelationId, string OriginSource, string CausationId,
    string[] ActionHistory, int HopCount)
{
    public static BroadcastChain From(BroadcastEnvelope message)
    {
        var body = message.Body;
        string Read(string name, string fallback) => body.EnumerateObject()
            .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } value
                ? value.GetString()!.Trim() : fallback;
        var history = body.EnumerateObject().FirstOrDefault(p => p.Name.Equals("actionHistory", StringComparison.OrdinalIgnoreCase)).Value;
        return new(Read("correlationId", message.RequestId), Read("originSource", message.Source), message.RequestId,
            history.ValueKind == JsonValueKind.Array ? history.EnumerateArray().Select(p => p.GetString()!).ToArray() : [], message.HopCount);
    }

    public static BroadcastChain Root(string source) => new(Guid.NewGuid().ToString("N"), source, "", [], 0);

    public BroadcastChain Enter(string action)
    {
        if (HopCount >= BroadcastEnvelope.MaximumHops || ActionHistory.Contains(action, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The event chain has reached this action already or reached its hop limit.");
        return this with { ActionHistory = [.. ActionHistory, action], HopCount = HopCount + 1 };
    }

    public static void Validate(JsonElement body)
    {
        foreach (var property in body.EnumerateObject())
        {
            if (new[] { "correlationId", "originSource", "causationId" }.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw new ArgumentException($"{property.Name} must be a string.");
                BroadcastOptions.ValidateName(property.Value.GetString(), property.Name, false);
            }
            if (property.Name.Equals("actionHistory", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() > BroadcastEnvelope.MaximumHops)
                    throw new ArgumentException("actionHistory must be an array with at most eight entries.");
                foreach (var entry in property.Value.EnumerateArray())
                    if (entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString()) ||
                        entry.GetString()!.Length > 266 || entry.GetString()!.Any(char.IsControl))
                        throw new ArgumentException("actionHistory entries must be device/action identities.");
            }
        }
    }
}
