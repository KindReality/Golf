using ExperienceX;
using System.Text.Json.Nodes;

internal static class ScreenMediaNameCycleChecks
{
    public static async Task Run()
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        string[] media = ["Eye", "Ghost", "Skull"];
        string Next(string current, int direction) => ScreenMediaNameCycle.Next("screen1", current, media, direction);
        Check(Next("screen1", 1) == "Eye" && Next("Eye", 1) == "Ghost", "Right did not advance from zero through media.");
        Check(Next("Ghost", -1) == "Eye" && Next("Eye", -1) == "screen1", "Left did not return through media to zero.");
        Check(Next("screen1", -1) == "Skull" && Next("Skull", 1) == "screen1", "Browsing did not wrap in both directions.");
        Check(Next("Custom", 1) == "Eye" && Next("Custom", -1) == "Skull" && Next("eYe", 1) == "Ghost",
            "Custom names or case differences did not resolve to the right browsing position.");
        Check(ScreenMediaNameCycle.Next("screen1", "Other", [], -1) == "screen1" &&
            ScreenMediaNameCycle.Next("screen1", "screen1", ["screen1", "Eye"], 1) == "Eye", "Empty media or duplicate zero entry broke browsing.");
        var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-name-cycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var name in new[] { "zebra.mp4", "alpha.png", "Eye.mp4", "Eye.png", "Eye.mp4.png", "Audio.mp3" })
                File.WriteAllText(Path.Combine(folder, name), "metadata fixture");
            var catalog = ScreenMediaCatalog.Read(folder);
            Check(catalog.Names().SequenceEqual(["alpha", "Eye.mp4", "Eye.mp4.png", "Eye.png", "zebra"]),
                "Media were not alphabetical, unambiguous, and limited to videos/images.");
            Check(catalog.BrowseName(new() { Name = "zebra.mp4" }) == "zebra" &&
                catalog.BrowseName(new() { Name = "Eye.mp4" }) == "Eye.mp4", "Explicit filenames lost their canonical browsing position.");
            foreach (var name in catalog.Names()) Check(catalog.Match(new() { Name = name })?.Path is not null, "Browsing exposed an ambiguous name.");
            var path = Path.Combine(folder, "appsettings.json");
            File.WriteAllText(path, """
                {"configurationMode":true,"Custom":17,"DisplayScreens":{"TEST":[
                  {"Id":"screen1","Name":"Eye"},{"Id":"second","Name":"screen2"},{"Id":"custom","Name":"Stage"}]}}
                """);
            var store = new PointConfigurationStore(path);
            var options = ExperienceOptions.Parse(store.EnsureDisplays(["TEST"]));
            Check(options.ScreensFor("TEST").Select(s => s.DefaultName).SequenceEqual(["screen1", "screen2", "screen3"]),
                "Startup did not populate unique default names while preserving renamed screens.");
            await store.RenameScreenAsync("TEST", "screen1", "Eye", "zebra");
            var restarted = ExperienceOptions.Parse(new PointConfigurationStore(path).EnsureDisplays(["TEST"]));
            Check(restarted.ScreensFor("TEST")[0].DefaultName == "screen1" && restarted.ScreensFor("TEST")[0].Name == "zebra",
                "Restart lost the saved zero position or current media name.");
            var added = await store.AddScreenAsync("TEST");
            Check(added.Name == "screen4" && added.DefaultName == "screen4", "New screens reused another screen's default name.");
            try
            {
                await store.RenameScreenAsync("TEST", added.Id, "screen4", "screen1");
                throw new Exception("Another screen's reserved zero-position name was accepted.");
            }
            catch (ArgumentException) { }
            Check(JsonNode.Parse(File.ReadAllText(path))!["Custom"]!.GetValue<int>() == 17, "Default naming erased unrelated configuration.");
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("Passed alphabetical media name browsing, both wraps, explicit/ambiguous filenames, persisted zero positions and reserved default names.");
    }
}
