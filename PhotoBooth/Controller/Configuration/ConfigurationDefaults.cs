using System.Text.Json.Nodes;
namespace Controller.Configuration;

internal static class ConfigurationDefaults
{
    public static string Populate(string text, BuildEnvironment build)
    {
        var options = ControllerOptions.Parse(text, build);
        var root = JsonNode.Parse(text)!.AsObject();
        var changed = false;
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
        var name = "default-ambient";
        for (var index = 2; options.Playlists.Any(p => ControllerOptions.Same(p.Name, name)); ++index) name = "default-ambient" + index;
        var playlistKey = root.Select(p => p.Key).FirstOrDefault(k => ControllerOptions.Same(k, "Playlists")) ?? "Playlists";
        var sceneKey = root.Select(p => p.Key).FirstOrDefault(k => ControllerOptions.Same(k, "Scenes")) ?? "Scenes";
        var playlists = root[playlistKey] as JsonArray ?? new();
        if (root[playlistKey] is null) root[playlistKey] = playlists;
        playlists.Add(new JsonObject { ["Name"] = name, ["Type"] = "ambient", ["Items"] = new JsonArray() });
        root[sceneKey] = new JsonArray(new JsonObject { ["Name"] = "idle", ["AmbientPlaylist"] = name });
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
