using System.Text.Json;
using Controller.Application;
using Controller.Configuration;

internal static class ShowEditorChecks
{
    internal static ControllerOptions Fixture() => new()
    {
        DeviceName = "show-editor-controller", ConfigurationMode = true, ExperienceSupervisionEnabled = false,
        ArduinoDiscovery = new() { Enabled = false },
        Scenes = [new() { Name = "idle", AmbientPlaylist = "ambient", ActivatingPlaylist = "cue" }],
        Playlists = [new() { Name = "ambient", Type = "ambient" }, new() { Name = "cue", MinimumDurationMilliseconds = 5000, Items = [new() { Name = "First", DelayMilliseconds = 10000, Commands = [ShowEditSession.DefaultCommand()] }] },
            new() { Name = "parent", MinimumDurationMilliseconds = 5000, Items = [new() { Name = "Nested", Commands = [new() { Playlist = "cue" }] }] }],
        Triggers = [new() { Name = "motion", SourceDevice = "sensor", Input = "motion", Event = "motion.detected", Playlist = "cue" }]
    };
    internal static async Task Run()
    {
        void Check(bool yes, string text) { if (!yes) throw new Exception(text); Console.WriteLine("PASS " + text); }
        var options = Fixture();
        var session = new ShowEditSession(options);
        session.SetPlaylist(1, options.Playlists[1] with { Name = "renamed cue" });
        Check(session.Current.Scenes[0].ActivatingPlaylist == "renamed cue" && session.Current.Triggers[0].Playlist == "renamed cue" && session.Current.Playlists[2].Items[0].Commands[0].Playlist == "renamed cue", "Playlist renames update scene, trigger and nested-playlist references together");
        Check(options.Playlists[1].Name == "cue" && options.Triggers[0].Playlist == "cue", "Draft editing never mutates running configuration snapshots");
        var before = session.Current.Signature;
        try { session.DeletePlaylist(1); throw new Exception("Referenced playlist was deleted."); } catch (ArgumentException) { }
        Check(session.Current.Signature == before, "Referenced playlists cannot be deleted");
        session.Undo(); Check(!session.Dirty && session.Current.Signature == session.Baseline.Signature, "Undo restores a graph including all renamed references");
        try { session.DeleteScene(0); throw new Exception("Last scene was deleted."); } catch (ArgumentException) { }
        session.Change(session.Current with { Scenes = [.. session.Current.Scenes, new() { Name = "second", AmbientPlaylist = "ambient" }] });
        session.MakeStartup(1); Check(session.Current.Scenes[0].Name == "second", "Startup scene is represented by the first scene");
        session.Current.Validate(options);
        var cycle = session.Current with { Playlists = session.Current.Playlists.Select(p => p.Name == "cue" ? p with { Items = [new() { Name = "cycle", Commands = [new() { Playlist = "parent" }] }] } : p).ToArray() };
        try { cycle.Validate(options); throw new Exception("Nested cycle was accepted."); } catch (ArgumentException) { }
        Check(true, "Save validation rejects nested cycles");
        var ambient = ShowEditSession.NewItem("Random", true) with { MinIntervalMilliseconds = 1000, MaxIntervalMilliseconds = 2000, CooldownMilliseconds = 3000 };
        var invalid = session.Current with { Playlists = session.Current.Playlists.Select(p => p.Type == "ambient" ? p with { Items = [ambient] } : p).ToArray() };
        try { invalid.Validate(options); throw new Exception("Invalid cooldown was accepted."); } catch (ArgumentException) { }
        Check(true, "Ambient cooldown must fit inside the configured interval");
        var folder = Path.Combine(Path.GetTempPath(), "Controller-show-edit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "appsettings.json"); File.WriteAllText(path, JsonSerializer.Serialize(options, ControllerOptions.Json));
            await using var service = new ConfigurationService(path, new("Development", 1000), new(), _ => Task.CompletedTask);
            options = service.Initial; var expected = ShowConfiguration.From(options);
            var next = expected with { Scenes = [.. expected.Scenes, new() { Name = "second", AmbientPlaylist = "ambient" }] };
            var unrelated = options with { DefaultDisplayName = "Projector", DisplayAliases = new() { ["Projector"] = @"\\.\DISPLAY2" } };
            File.WriteAllText(path, JsonSerializer.Serialize(unrelated, ControllerOptions.Json));
            await service.SaveShowAsync(expected, next);
            var saved = ControllerOptions.Parse(File.ReadAllText(path), options.Build);
            Check(saved.DefaultDisplayName == "Projector" && saved.DisplayAliases.Count == 1 && saved.ControllerId == options.ControllerId && saved.Scenes.Length == 2, "Atomic show save merges with unrelated machine settings instead of replacing them");
            var bytes = File.ReadAllBytes(path);
            try { await service.SaveShowAsync(expected, expected); throw new Exception("Stale editor overwrote remote changes."); } catch (InvalidOperationException) { }
            Check(File.ReadAllBytes(path).SequenceEqual(bytes), "Stale show saves preserve the newer configuration");
            try { await service.SaveShowAsync(next, cycle); throw new Exception("Invalid graph reached disk."); } catch (ArgumentException) { }
            Check(File.ReadAllBytes(path).SequenceEqual(bytes), "Failed validation leaves the saved file intact");
            File.WriteAllText(path, JsonSerializer.Serialize(saved with { ConfigurationMode = false }, ControllerOptions.Json));
            try { await service.SaveShowAsync(next, next); throw new Exception("Save succeeded outside configuration mode."); } catch (InvalidOperationException) { }
            Check(true, "Configuration mode is required at the actual save boundary");
        }
        finally { Directory.Delete(folder, true); }
    }
}
