using System.Text.Json.Nodes;
namespace Controller.Configuration;

internal static class ConfigurationDefaults
{
    public static string Populate(string text, BuildEnvironment build)
    {
        using var document = System.Text.Json.JsonDocument.Parse(text);
        ControllerOptions.RejectDuplicates(document.RootElement);
        var root = JsonNode.Parse(text)!.AsObject();
        var changed = false;
        // Remove the retired scene assignment without deleting its playlist content.
        if (root.FirstOrDefault(entry => ControllerOptions.Same(entry.Key, "Scenes")).Value is JsonArray existingScenes)
            foreach (var scene in existingScenes.OfType<JsonObject>())
                foreach (var key in scene.Select(entry => entry.Key).Where(key => ControllerOptions.Same(key, "DeactivatingPlaylist")).ToArray())
                    changed |= scene.Remove(key);
        var options = ControllerOptions.Parse(changed ? root.ToJsonString() : text, build);
        if (!root.Any(p => ControllerOptions.Same(p.Key, "PeerDiscovery"))) { root["PeerDiscovery"] = System.Text.Json.JsonSerializer.SerializeToNode(options.PeerDiscovery); changed = true; }
        if (!root.Any(p => ControllerOptions.Same(p.Key, "ExperienceSupervisionEnabled"))) { root["ExperienceSupervisionEnabled"] = true; changed = true; }
        var existingPlaylists = root.FirstOrDefault(entry => ControllerOptions.Same(entry.Key, "Playlists")).Value as JsonArray;
        if (existingPlaylists is not null)
            foreach (var playlist in existingPlaylists.OfType<JsonObject>())
                if (playlist.FirstOrDefault(entry => ControllerOptions.Same(entry.Key, "Items")).Value is JsonArray items)
                    foreach (var item in items.OfType<JsonObject>())
                    {
                        if (!item.Any(entry => ControllerOptions.Same(entry.Key, "Roles"))) { item["Roles"] = ""; changed = true; }
                        if (!item.Any(entry => ControllerOptions.Same(entry.Key, "BroadcastMode"))) { item["BroadcastMode"] = "Broadcast"; changed = true; }
                        if (!item.Any(entry => ControllerOptions.Same(entry.Key, "IncludeSubwoofer"))) { item["IncludeSubwoofer"] = false; changed = true; }
                    }
        if (string.IsNullOrWhiteSpace(options.ControllerId)) { root["ControllerId"] = Guid.NewGuid().ToString("N"); changed = true; }
        if (!root.Any(p => ControllerOptions.Same(p.Key, "Perspectives"))) { root["Perspectives"] = new JsonArray(); changed = true; }
        foreach (var key in new[] { "DefaultDisplayName", "DisplayAliases" })
        {
            if (root.Any(entry => ControllerOptions.Same(entry.Key, key))) continue;
            root[key] = key == "DisplayAliases" ? new JsonObject() : JsonValue.Create("");
            changed = true;
        }
        if (options.Scenes.Length != 0)
            return changed ? root.ToJsonString(ControllerOptions.Json) : text;
        var playlistKey = root.Select(p => p.Key).FirstOrDefault(k => ControllerOptions.Same(k, "Playlists")) ?? "Playlists";
        var sceneKey = root.Select(p => p.Key).FirstOrDefault(k => ControllerOptions.Same(k, "Scenes")) ?? "Scenes";
        var playlists = root[playlistKey] as JsonArray ?? new();
        if (root[playlistKey] is null) root[playlistKey] = playlists;
        var names = options.Playlists.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string AddEmptyPlaylist(string prefix, string type)
        {
            var name = prefix;
            for (var suffix = 2; !names.Add(name); suffix++) name = prefix + suffix;
            playlists.Add(new JsonObject { ["Name"] = name, ["Type"] = type, ["Items"] = new JsonArray() });
            return name;
        }
        var scenes = new JsonArray();
        foreach (var name in new[] { "idle", "active" })
            scenes.Add(new JsonObject
            {
                ["Name"] = name,
                ["ActivatingPlaylist"] = AddEmptyPlaylist(name + "-activating", "storyboard"),
                ["AmbientPlaylist"] = AddEmptyPlaylist(name + "-ambient", "ambient")
            });
        root[sceneKey] = scenes;
        for (var zone = 1; zone <= 3; zone++)
        {
            var zoneName = "active-zone-" + zone;
            if (playlists.OfType<JsonObject>().Any(p => ControllerOptions.Same(p["Name"]?.GetValue<string>(), zoneName))) continue;
            playlists.Add(new JsonObject { ["Name"] = zoneName, ["Type"] = "storyboard", ["Items"] = new JsonArray(new JsonObject
            {
                ["Name"] = "Activate", ["Roles"] = "", ["BroadcastMode"] = "Broadcast", ["IncludeSubwoofer"] = false,
                ["Commands"] = new JsonArray(new JsonObject { ["Device"] = "*", ["Payload"] = new JsonObject { ["command"] = "scene", ["value"] = "active" } })
            }) });
        }
        var result = root.ToJsonString(ControllerOptions.Json); ControllerOptions.Parse(result, build); return result;
    }
    public static void Write(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { var bytes = System.Text.Encoding.UTF8.GetBytes(text); file.Write(bytes); file.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak"); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
