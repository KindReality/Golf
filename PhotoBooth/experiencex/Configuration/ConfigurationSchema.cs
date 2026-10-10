using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExperienceX;

internal static class ConfigurationSchema
{
    public const int CurrentVersion = 4;
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
        // Rename persisted fields once. New names take precedence when both are present.
        foreach (var (oldName, newName) in new[] { ("MonitorName", "DefaultDisplayName"), ("DefaultMonitorName", "DefaultDisplayName"),
            ("MonitorAliases", "DisplayAliases"), ("MonitorBackgrounds", "DisplayBackgrounds"), ("Keystone", "Points"), ("KeystoneHitDiameterPercent", "PointHitDiameterPercent") })
            if (root.TryGetPropertyValue(oldName, out var oldValue))
            {
                if (!root.ContainsKey(newName))
                    root[newName] = oldValue?.DeepClone();
                root.Remove(oldName);
                changed = true;
            }
        if (root.ContainsKey("UdpPort"))
        {
            root.Remove("UdpPort");
            changed = true;
        }
        foreach (var (name, port) in new[] { ("UdpEventsPort", 54242), ("UdpCommandsPort", 54243) })
            if (!root.ContainsKey(name))
            {
                root[name] = port;
                changed = true;
            }
        if (root["DisplayKeystones"] is JsonObject legacy)
        {
            var screens = root["DisplayScreens"] as JsonObject ?? new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });
            root["DisplayScreens"] ??= screens;
            foreach (var entry in legacy)
                if (!screens.ContainsKey(entry.Key))
                    screens[entry.Key] = new JsonArray(new JsonObject { ["Id"] = "screen1", ["Name"] = "screen1", ["Type"] = "video", ["Keystone"] = entry.Value?.DeepClone() });
            root.Remove("DisplayKeystones");
            changed = true;
        }
        if (version < 1)
        {
            if (!root.ContainsKey("DefaultDisplayName") && root.TryGetPropertyValue("DisplayName", out var display))
                root["DefaultDisplayName"] = display?.DeepClone();
        }
        if (root["Points"] is JsonObject defaultPoints)
            changed |= UpgradePoints(defaultPoints);
        if (root["DisplayScreens"] is JsonObject displayScreens)
            foreach (var screens in displayScreens.Select(p => p.Value).OfType<JsonArray>())
                foreach (var screen in screens.OfType<JsonObject>())
                {
                    if (screen.TryGetPropertyValue("Keystone", out var keystone))
                    {
                        if (!screen.ContainsKey("Points")) screen["Points"] = keystone?.DeepClone();
                        screen.Remove("Keystone"); changed = true;
                    }
                    if (screen["Points"] is JsonObject points) changed |= UpgradePoints(points);
                }
        // Continue accepting legacy background edits while persisting the new object representation.
        if (root["DisplayBackgrounds"] is JsonObject backgrounds)
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

    private static bool UpgradePoints(JsonObject points)
    {
        bool changed = false;
        if (!points.ContainsKey("Vertices"))
        {
            var vertices = new JsonArray();
            foreach (var (name, x, y) in new[] { ("TopLeft", 0, 0), ("TopRight", 1, 0), ("BottomRight", 1, 1), ("BottomLeft", 0, 1) })
            {
                var vertex = points[name]?.DeepClone() as JsonObject ?? new JsonObject();
                if (!vertex.ContainsKey("X")) vertex["X"] = x;
                if (!vertex.ContainsKey("Y")) vertex["Y"] = y;
                vertices.Add(vertex);
            }
            points["Vertices"] = vertices; changed = true;
        }
        foreach (var name in new[] { "TopLeft", "TopRight", "BottomLeft", "BottomRight" })
            changed |= points.Remove(name);
        return changed;
    }
}
