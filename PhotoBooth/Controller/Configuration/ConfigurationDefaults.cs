using System.Text.Json.Nodes;
namespace Controller.Configuration;

internal static class ConfigurationDefaults
{
    public static string Populate(string text, BuildEnvironment build)
    {
        var options = ControllerOptions.Parse(text, build);
        if (options.Scenes.Length != 0) return text;
        var name = "default-ambient";
        for (var index = 2; options.Playlists.Any(p => ControllerOptions.Same(p.Name, name)); ++index) name = "default-ambient" + index;
        var root = JsonNode.Parse(text)!.AsObject();
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
