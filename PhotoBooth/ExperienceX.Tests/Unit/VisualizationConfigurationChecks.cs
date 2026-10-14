using System.Text.Json;
using ExperienceX;

internal static class VisualizationConfigurationChecks
{
    internal static async Task Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-visualization-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var video = ScreenOptions.New([]);
            var visual = ScreenOptions.New([video], "visualization");
            Check(visual.Name == "gforce" && visual.DefaultName == "gforce", "A new visualization must default to gforce");
            Check(ScreenOptions.New([video with { Name = "gforce" }], "visualization").Name == "gforce2", "Visualization naming must avoid existing screen names");
            Check(visual.Points == new PointOptions(), "Visualization fills its display even alongside existing media");
            var options = new ExperienceOptions { ConfigurationMode = true, DisplayScreens = new() { ["DISPLAY1"] = [video], ["DISPLAY2"] = [] } };
            var path = Path.Combine(folder, "appsettings.json");
            File.WriteAllText(path, JsonSerializer.Serialize(options));
            var store = new PointConfigurationStore(path);
            var created = await store.AddScreenAsync("DISPLAY1", type: "visualization");
            ExperienceOptions Read() => ExperienceOptions.Parse(File.ReadAllText(path));
            Check(Read().ScreensFor("DISPLAY1")[0].IsVisualization, "Visualization and its inverse mat start beneath other screens");
            foreach (var display in new[] { "DISPLAY1", "DISPLAY2" })
                await Rejected(() => store.AddScreenAsync(display, type: "visualization"), "A competing fullscreen visualization is rejected atomically");
            await Rejected(() => store.ToggleScreenTransparencyAsync("DISPLAY1", created.Id), "Chroma-key toggles exclude visualization");
            await Rejected(() => store.AdjustScreenBrightnessAsync("DISPLAY1", created.Id, -5), "Media brightness excludes visualization");
            await Rejected(() => store.SetPerspectiveAsync("DISPLAY1", created.Id, null), "Source perspectives exclude visualization");
            await store.RenameScreenAsync("DISPLAY1", created.Id, created.Name, "Eye");
            File.WriteAllText(Path.Combine(folder, "Eye.mp4"), "metadata");
            Check(ScreenMediaCatalog.Read(folder).Match(Read().ScreensFor("DISPLAY1").Single(s => s.IsVisualization)) is null, "A visualization named after media never starts media playback");
            Check(ScreenSelection.VideoOutputs(new() { Screen = "*" }, Read(), ["DISPLAY1", "DISPLAY2"], "DISPLAY1").Select(s => s.Screen.Id).SequenceEqual([video.Id]), "Playback broadcasts include only media screens");
            Check(DisplayBackground.For("DISPLAY1", Read()).A == 0 && DisplayBackground.For("DISPLAY2", Read()).A == 1, "Only the visualization's display gets a transparent background");
            var renamed = Read().ScreensFor("DISPLAY1").Single(s => s.IsVisualization);
            await store.DeleteScreenAsync("DISPLAY1", renamed);
            Check(DisplayBackground.For("DISPLAY1", Read()).A == 1, "Deleting a visualization restores the configured background");
            await store.AddScreenAsync("DISPLAY2", type: "visualization");
            Check(Read().ScreensFor("DISPLAY2").Single().IsVisualization, "A deleted visualization can be recreated on another display");
            (options with { DisplayScreens = new() { ["DISPLAY1"] = [visual with { Points = video.Points with { TopLeft = new(.1, .1) } }] } }).Validate();
            Invalid(options with { DisplayScreens = new() { ["DISPLAY1"] = [visual with { Points = video.Points with { TopLeft = new(1.1, .1) } }] } });
            Invalid(options with { DisplayScreens = new() { ["DISPLAY1"] = [visual], ["DISPLAY2"] = [visual] } });
            Console.WriteLine("Passed visualization geometry, persistence, single-instance policy, media exclusions, background restoration and remote validation.");
        }
        finally { Directory.Delete(folder, true); }
    }
    private static void Invalid(ExperienceOptions options) { try { options.Validate(); } catch (ArgumentException) { return; } throw new Exception("Invalid visualization configuration accepted."); }
    private static async Task Rejected(Func<Task> operation, string message) { try { await operation(); } catch (InvalidOperationException) { return; } throw new Exception(message); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
