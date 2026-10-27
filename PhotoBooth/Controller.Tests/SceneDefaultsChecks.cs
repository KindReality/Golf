using System.Text.Json;
using Controller.Configuration;

internal static class SceneDefaultsChecks
{
    internal static void Run()
    {
        var template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.defaults.json"));
        foreach (var build in new[] { new BuildEnvironment("Development", 1000), new BuildEnvironment("Production", 10000) })
        {
            CheckScenePlaylists(ControllerOptions.Parse(template, build), build.Environment + " template", activationVideos: true);
            var generated = ConfigurationDefaults.Populate("{}", build);
            CheckScenePlaylists(ControllerOptions.Parse(generated, build), build.Environment + " startup");
            Check(ConfigurationDefaults.Populate(generated, build) == generated, "Startup population is idempotent in " + build.Environment);
        }
        var environment = new BuildEnvironment("Development", 1000);
        var collision = ConfigurationDefaults.Populate("""
            {"Playlists":[{"Name":"IDLE-AMBIENT","Type":"storyboard","Items":[]},
                          {"Name":"active-activating","Type":"ambient","Items":[]}]}
            """, environment);
        var options = ControllerOptions.Parse(collision, environment);
        CheckScenePlaylists(options, "Startup with existing playlist names");
        Check(options.Playlist("IDLE-AMBIENT").Type == "storyboard" && options.Playlist("active-activating").Type == "ambient",
            "Default generation chooses unique names and preserves preexisting playlists");
        var existing = ShowEditorChecks.Fixture();
        var populated = ControllerOptions.Parse(ConfigurationDefaults.Populate(JsonSerializer.Serialize(existing, ControllerOptions.Json), environment), environment);
        Check(ShowConfiguration.From(populated).Signature == ShowConfiguration.From(existing).Signature,
            "Existing scenes and playlist content are not changed by new defaults");
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(existing, ControllerOptions.Json))!;
        legacy["Scenes"]![0]!["DeactivatingPlaylist"] = "old-exit";
        legacy["Playlists"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse("""{"Name":"old-exit","Items":[]}"""));
        var cleaned = ConfigurationDefaults.Populate(legacy.ToJsonString(), environment);
        Check(!cleaned.Contains("DeactivatingPlaylist") && ControllerOptions.Parse(cleaned, environment).Playlist("old-exit").Items.Length == 0,
            "Startup removes retired scene assignments and preserves standalone playlists");
        Check(ConfigurationDefaults.Populate(cleaned, environment) == cleaned, "Retired-assignment cleanup is idempotent");
    }

    internal static void CheckScenePlaylists(ControllerOptions options, string source, bool activationVideos = false)
    {
        Check(options.Scenes.Select(s => s.Name).SequenceEqual(["idle", "active"]), source + " starts with Idle, followed by Active");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scene in options.Scenes)
        {
            Check(scene.ActivatingPlaylist is not null,
                source + ": " + scene.Name + " has an activation playlist");
            var assigned = new List<(string Name, string Type)> { (scene.ActivatingPlaylist!, "storyboard") };
            if (scene.Name == "idle")
            {
                Check(scene.AmbientPlaylist is not null && scene.Transition is null, source + ": Idle runs its ambient playlist after activation");
                assigned.Add((scene.AmbientPlaylist!, "ambient"));
            }
            else
                Check(scene.AmbientPlaylist is null && scene.Transition is { Scene: "idle", Device: "*" } && options.Playlist(scene.ActivatingPlaylist!).EffectiveMinimumDurationMilliseconds == 5000,
                    source + ": Active broadcasts a return to Idle using automatic timing and a five-second minimum");
            foreach (var (name, type) in assigned)
            {
                var playlist = options.Playlist(name, type);
                var sample = activationVideos && name == scene.ActivatingPlaylist;
                Check(names.Add(name) && playlist.Items.Length == (sample ? 1 : 0),
                    source + ": " + name + (sample ? " contains one activation video" : " is a separate, empty " + type + " playlist"));
                if (sample)
                {
                    var command = playlist.Items[0].Commands.Single();
                    var loop = scene.Name == "idle";
                    var file = loop ? "Zombies/TerrifyingZombieHorde.mp4" : "Skulls/SkullStrobe.mp4";
                    Check(command.Device == "*" && ControllerOptions.Text(command.Payload, "screen") == "*" &&
                        ControllerOptions.Text(command.Payload, "value") == file && command.Payload.GetProperty("loop").GetBoolean() == loop && command.Payload.GetProperty("mute").GetBoolean(),
                        source + ": " + scene.Name + " plays " + file + (loop ? " looping" : " once") + ", muted, on all video screens");
                }
            }
        }
    }
    private static void Check(bool condition, string text)
    { if (!condition) throw new InvalidOperationException(text); Console.WriteLine("PASS " + text); }
}
