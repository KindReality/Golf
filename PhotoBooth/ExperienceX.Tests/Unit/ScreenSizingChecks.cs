using ExperienceX;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ScreenSizingChecks
{
    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ExperienceX-sizing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string display = @"\\.\DISPLAY1";
            var path = Path.Combine(directory, "appsettings.json");
            var options = new ExperienceOptions
            {
                ConfigurationMode = true,
                DisplayScreens = new() { [display] = [new(), new() { Id = "other", Name = "other" }, new() { Id = "mat", Name = "mat", Type = "mat" }] }
            };
            var root = JsonSerializer.SerializeToNode(options)!.AsObject();
            root["Custom"] = 42;
            root["DisplayScreens"]![display]![0]!.AsObject().Remove("SizingMode");
            await File.WriteAllTextAsync(path, root.ToJsonString());
            var store = new PointConfigurationStore(path);
            var seeded = ExperienceOptions.Parse(store.EnsureDisplays([display]));
            if (seeded.ScreensFor(display).Any(s => s.SizingMode != "fit")) throw new Exception("New/existing screens did not default to Fit.");
            var changed = ExperienceOptions.Parse(await store.ToggleScreenSizingAsync(display, "screen1"));
            if (changed.ScreensFor(display)[0].SizingMode != "stretch" || changed.ScreensFor(display)[1] != seeded.ScreensFor(display)[1] ||
                changed.ScreensFor(display)[0] with { SizingMode = "fit" } != seeded.ScreensFor(display)[0]) throw new Exception("Sizing changed unrelated screen settings.");
            changed = ExperienceOptions.Parse(await store.ToggleScreenSizingAsync(display, "screen1"));
            if (changed.ScreensFor(display)[0].SizingMode != "fit" || JsonNode.Parse(File.ReadAllText(path))!["Custom"]!.GetValue<int>() != 42)
                throw new Exception("Fit/Stretch did not toggle persistently or preserved settings were lost.");
            var before = File.ReadAllText(path);
            foreach (var id in new[] { "mat", "missing" })
            {
                try { await store.ToggleScreenSizingAsync(display, id); throw new Exception("Unavailable video screen accepted."); }
                catch (InvalidOperationException) { }
            }
            if (File.ReadAllText(path) != before) throw new Exception("Rejected sizing toggle changed configuration.");
            await store.ToggleConfigurationModeAsync();
            try { await store.ToggleScreenSizingAsync(display, "screen1"); throw new Exception("Sizing changed outside configuration mode."); }
            catch (InvalidOperationException) { }
            try
            {
                (options with { DisplayScreens = new() { [display] = [new() { SizingMode = "crop" }] } }).Validate();
                throw new Exception("Unsupported sizing mode accepted.");
            }
            catch (ArgumentException) { }
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("Passed persistent per-screen Fit/Stretch defaults, isolation, user-setting preservation and configuration/mat/missing-screen guards.");
    }
}
