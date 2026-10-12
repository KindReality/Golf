using ExperienceX;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ScreenBrightnessChecks
{
    public static async Task Run()
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var directory = Path.Combine(Path.GetTempPath(), "ExperienceX-brightness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string display = "TEST";
            var path = Path.Combine(directory, "appsettings.json");
            var options = new ExperienceOptions
            {
                ConfigurationMode = true,
                DisplayScreens = new() { [display] = [new(), new() { Id = "other", Name = "other", Brightness = 35 }, new() { Id = "mat", Name = "mat", Type = "mat" }] }
            };
            var root = JsonSerializer.SerializeToNode(options)!;
            root["Custom"] = 42;
            root["DisplayScreens"]![display]![0]!.AsObject().Remove("Brightness");
            await File.WriteAllTextAsync(path, root.ToJsonString());
            var store = new PointConfigurationStore(path);
            var seeded = ExperienceOptions.Parse(store.EnsureDisplays([display]));
            Check(seeded.ScreensFor(display)[0].Brightness == 100 && JsonNode.Parse(File.ReadAllText(path))!["DisplayScreens"]![display]![0]!["Brightness"]!.GetValue<double>() == 100,
                "Existing/new screens did not populate default brightness.");
            var changed = ExperienceOptions.Parse(await store.AdjustScreenBrightnessAsync(display, "screen1", -5));
            Check(changed.ScreensFor(display)[0].Brightness == 95 && changed.ScreensFor(display)[0] with { Brightness = 100 } == seeded.ScreensFor(display)[0] &&
                changed.ScreensFor(display)[1] == seeded.ScreensFor(display)[1] && changed.DisplayBackgrounds[display] == seeded.DisplayBackgrounds[display],
                "Brightness affected unrelated screen, key, points or background settings.");
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.AdjustScreenBrightnessAsync(display, "screen1", -5)));
            Check(ExperienceOptions.Parse(File.ReadAllText(path)).ScreensFor(display)[0].Brightness == 75, "Concurrent adjustments lost increments.");
            changed = ExperienceOptions.Parse(await store.AdjustScreenBrightnessAsync(display, "screen1", -100));
            Check(changed.ScreensFor(display)[0].Brightness == 0, "Brightness did not clamp at zero.");
            var atZero = File.ReadAllText(path);
            await store.AdjustScreenBrightnessAsync(display, "screen1", -5);
            Check(File.ReadAllText(path) == atZero, "Adjustment at zero rewrote configuration.");
            changed = ExperienceOptions.Parse(await store.AdjustScreenBrightnessAsync(display, "screen1", 150));
            Check(changed.ScreensFor(display)[0].Brightness == 100 && JsonNode.Parse(File.ReadAllText(path))!["Custom"]!.GetValue<int>() == 42,
                "Brightness did not clamp at 100 or preserved settings were lost.");
            await store.AdjustScreenBrightnessAsync(display, "screen1", -22.5);
            Check(ExperienceOptions.Parse(new PointConfigurationStore(path).EnsureDisplays([display])).ScreensFor(display)[0].Brightness == 77.5,
                "Startup overwrote persisted fractional brightness.");
            var before = File.ReadAllText(path);
            foreach (var id in new[] { "mat", "missing" })
            {
                try { await store.AdjustScreenBrightnessAsync(display, id, -5); throw new Exception("Unavailable video screen accepted."); }
                catch (InvalidOperationException) { }
            }
            Check(File.ReadAllText(path) == before, "Rejected adjustment changed configuration.");
            await store.ToggleConfigurationModeAsync();
            try { await store.AdjustScreenBrightnessAsync(display, "screen1", -5); throw new Exception("Brightness changed outside configuration mode."); }
            catch (InvalidOperationException) { }
            foreach (var value in new[] { -1, 101, double.NaN, double.PositiveInfinity })
            {
                try { (options with { DisplayScreens = new() { [display] = [new() { Brightness = value }] } }).Validate(); throw new Exception("Invalid brightness accepted."); }
                catch (ArgumentException) { }
            }
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("Passed per-screen brightness defaults/persistence, concurrent increments, bounds, preserved settings and configuration/mat/missing-screen guards.");
    }
}
