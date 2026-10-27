using System.Text.Json.Nodes;
namespace Controller.Configuration;

internal static class ConfigurationDefaults
{
    public static string Populate(string text, BuildEnvironment build)
    {
        using var document = System.Text.Json.JsonDocument.Parse(text);
        ControllerOptions.RejectDuplicates(document.RootElement);
        var root = JsonNode.Parse(text)!.AsObject();
        var changed = NormalizePlaylistTiming(root);
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
            var playlist = new JsonObject { ["Name"] = name, ["Type"] = type, ["Items"] = new JsonArray() };
            if (type == "storyboard") { playlist["MinimumDurationMilliseconds"] = 5000; playlist["MaximumDurationMilliseconds"] = null; }
            playlists.Add(playlist);
            return name;
        }
        var scenes = new JsonArray();
        foreach (var name in new[] { "idle", "active" })
        {
            var scene = new JsonObject
            {
                ["Name"] = name,
                ["ActivatingPlaylist"] = AddEmptyPlaylist(name + "-activating", "storyboard")
            };
            if (name == "idle") scene["AmbientPlaylist"] = AddEmptyPlaylist(name + "-ambient", "ambient");
            else scene["Transition"] = System.Text.Json.JsonSerializer.SerializeToNode(new SceneTransitionOptions { Scene = "idle" });
            scenes.Add(scene);
        }
        root[sceneKey] = scenes;
        for (var zone = 1; zone <= 3; zone++)
        {
            var zoneName = "active-zone-" + zone;
            if (playlists.OfType<JsonObject>().Any(p => ControllerOptions.Same(p["Name"]?.GetValue<string>(), zoneName))) continue;
            playlists.Add(new JsonObject { ["Name"] = zoneName, ["Type"] = "storyboard", ["MinimumDurationMilliseconds"] = 5000, ["MaximumDurationMilliseconds"] = null, ["Items"] = new JsonArray(new JsonObject
            {
                ["Name"] = "Activate", ["Roles"] = "", ["BroadcastMode"] = "Broadcast",
                ["Commands"] = new JsonArray(new JsonObject { ["Device"] = "*", ["Payload"] = new JsonObject { ["command"] = "scene", ["value"] = "active" } })
            }) });
        }
        var result = root.ToJsonString(ControllerOptions.Json); ControllerOptions.Parse(result, build); return result;
    }
    private static bool NormalizePlaylistTiming(JsonObject root)
    {
        JsonNode? Value(JsonObject node, string key) => node.FirstOrDefault(p => ControllerOptions.Same(p.Key, key)).Value;
        string? Key(JsonObject node, string key) => node.Select(p => p.Key).FirstOrDefault(k => ControllerOptions.Same(k, key));
        var changed = false;
        var playlists = Value(root, "Playlists") as JsonArray;
        if (playlists is not null)
            foreach (var playlist in playlists.OfType<JsonObject>())
            {
                if (Value(playlist, "Type")?.GetValue<string>() is { } type && type != "storyboard") continue;
                if (Key(playlist, "DurationMilliseconds") is not { } key) continue;
                if (Value(playlist, key) is { } prior && Key(playlist, "MinimumDurationMilliseconds") is null)
                    playlist["MinimumDurationMilliseconds"] = prior.DeepClone();
                playlist.Remove(key); changed = true;
            }
        if (Value(root, "Scenes") is JsonArray scenes)
            foreach (var scene in scenes.OfType<JsonObject>())
            {
                if (Value(scene, "Transition") is not JsonObject transition || Key(transition, "MinimumSceneDurationSeconds") is not { } key) continue;
                var seconds = Value(transition, key)?.GetValue<double>() ?? 5;
                if (!double.IsFinite(seconds) || seconds is < .001 or > 86400) throw new ArgumentException("Invalid prior scene minimum duration.");
                var activation = Value(scene, "ActivatingPlaylist")?.GetValue<string>();
                var playlist = playlists?.OfType<JsonObject>().FirstOrDefault(p => ControllerOptions.Same(Value(p, "Name")?.GetValue<string>(), activation));
                if (playlist is null && activation is null && seconds != 5)
                {
                    playlists ??= new JsonArray();
                    if (Key(root, "Playlists") is null) root["Playlists"] = playlists;
                    var names = playlists.OfType<JsonObject>().Select(p => Value(p, "Name")?.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var prefix = (Value(scene, "Name")?.GetValue<string>() ?? "Scene") + "-activating";
                    var name = prefix;
                    for (var suffix = 2; names.Contains(name); suffix++) name = prefix + suffix;
                    playlist = new JsonObject { ["Name"] = name, ["Type"] = "storyboard", ["Items"] = new JsonArray() };
                    playlists.Add(playlist); scene[Key(scene, "ActivatingPlaylist") ?? "ActivatingPlaylist"] = name;
                }
                if (playlist is not null && Key(playlist, "MinimumDurationMilliseconds") is null)
                    playlist["MinimumDurationMilliseconds"] = (int)Math.Round(seconds * 1000);
                transition.Remove(key); changed = true;
            }
        if (playlists is not null)
            foreach (var playlist in playlists.OfType<JsonObject>())
                if ((Value(playlist, "Type")?.GetValue<string>() ?? "storyboard") == "storyboard" && Key(playlist, "MinimumDurationMilliseconds") is null)
                { playlist["MinimumDurationMilliseconds"] = 5000; changed = true; }
        return changed;
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
