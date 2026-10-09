using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExperienceX;

internal static class ConfigurationSchema
{
    public const int CurrentVersion = 2;
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static JsonObject Read(string json)
    {
        var root = JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject
            ?? throw new JsonException("Configuration must be an object.");
        Upgrade(root);
        return root;
    }

    // Each migration is additive and preserves unknown fields. Persisted by the atomic store.
    public static bool Upgrade(JsonObject root)
    {
        int version = 0;
        if (root.TryGetPropertyValue("ConfigurationVersion", out var value))
        {
            if (value is not JsonValue number || !number.TryGetValue<int>(out version) || version < 0)
                throw new ArgumentException("ConfigurationVersion must be a nonnegative integer.");
        }
        if (version > CurrentVersion)
            throw new NotSupportedException($"Configuration version {version} is newer than supported version {CurrentVersion}.");
        bool changed = version != CurrentVersion;
        if (version < 1)
        {
            if (!root.ContainsKey("DefaultMonitorName") && root.TryGetPropertyValue("MonitorName", out var monitor))
                root["DefaultMonitorName"] = monitor?.DeepClone();
            if (!root.ContainsKey("MaxVideoPlaybackLength") && root.TryGetPropertyValue("maxPlaybackLength", out var maximum))
                root["MaxVideoPlaybackLength"] = maximum?.DeepClone();
        }
        // Continue accepting legacy background edits while persisting the new object representation.
        if (root["MonitorBackgrounds"] is JsonObject backgrounds)
            foreach (var entry in backgrounds.ToArray())
                if (entry.Value is JsonValue valueNode && valueNode.TryGetValue<string>(out var color))
                {
                    bool transparent = string.Equals(color.Trim(), "transparent", StringComparison.OrdinalIgnoreCase);
                    backgrounds[entry.Key] = new JsonObject
                    {
                        ["BackgroundColor"] = transparent ? "#000000" : color.Trim(),
                        ["IsTransparent"] = transparent
                    };
                    changed = true;
                }
        if (changed)
            root["ConfigurationVersion"] = CurrentVersion;
        return changed;
    }
}
