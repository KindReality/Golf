using ExperienceX;
using System.Text.Json.Nodes;

internal static class ScreenTransparencyChecks
{
    public static async Task Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-screen-transparency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            const string display = @"\\.\DISPLAY1";
            var path = Path.Combine(folder, "appsettings.json");
            var initial = new ExperienceOptions
            {
                ConfigurationMode = true,
                BlackKey = new() { Threshold = .02, Softness = .04 },
                DisplayScreens = new() { [display] = [new(), new() { Id = "second", Name = "screen2" }, new() { Id = "mat", Name = "mask", Type = "mat" }] },
                DisplayBackgrounds = new() { [display] = new() { BackgroundColor = "#123456", IsTransparent = false } }
            };
            var root = System.Text.Json.JsonSerializer.SerializeToNode(initial)!.AsObject();
            root["Custom"] = "preserve";
            File.WriteAllText(path, root.ToJsonString());
            var store = new PointConfigurationStore(path);
            var points = root["DisplayScreens"]![display]![0]!["Points"]!.ToJsonString();
            var backgrounds = root["DisplayBackgrounds"]!.ToJsonString();
            var options = ExperienceOptions.Parse(await store.ToggleScreenTransparencyAsync(display, "screen1"));
            var screen = options.ScreensFor(display)[0];
            if (screen.BlackKey != initial.BlackKey with { Enabled = false } || !options.ScreensFor(display)[1].EffectiveBlackKey(options).Enabled || options.BlackKey != initial.BlackKey)
                throw new Exception("Screen chroma key toggle changed another screen, the defaults, or tuning values.");
            root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            root["DisplayScreens"]![display]![0]!["BlackKey"]!["CustomKey"] = 42;
            File.WriteAllText(path, root.ToJsonString());
            options = ExperienceOptions.Parse(await store.ToggleScreenTransparencyAsync(display, "screen1"));
            root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if (!options.ScreensFor(display)[0].BlackKey!.Enabled || root["DisplayBackgrounds"]!.ToJsonString() != backgrounds ||
                root["DisplayScreens"]![display]![0]!["Points"]!.ToJsonString() != points || root["Custom"]!.GetValue<string>() != "preserve" ||
                root["DisplayScreens"]![display]![0]!["BlackKey"]!["CustomKey"]!.GetValue<int>() != 42)
                throw new Exception("Screen transparency persistence lost background, points, or custom settings.");
            var before = File.ReadAllText(path);
            await Reject(() => store.ToggleScreenTransparencyAsync(display, "mat"));
            await Reject(() => store.ToggleScreenTransparencyAsync(display, "missing"));
            if (File.ReadAllText(path) != before) throw new Exception("Rejected transparency toggles changed configuration.");
            options = ExperienceOptions.Parse(await store.SetBackgroundAsync(display, true));
            if (!options.DisplayBackgrounds[display].IsTransparent || !options.ScreensFor(display)[0].BlackKey!.Enabled)
                throw new Exception("Background transparency changed screen chroma key.");
            await store.ToggleConfigurationModeAsync();
            await Reject(() => store.ToggleScreenTransparencyAsync(display, "screen1"));
            try
            {
                (options with { DisplayScreens = new() { [display] = [new() { BlackKey = new() { Threshold = -.1 } }] } }).Validate();
                throw new Exception("Invalid screen chroma key was accepted.");
            }
            catch (ArgumentException) { }
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("Passed independent persistent screen chroma key/background toggles, defaults, tuning preservation, mat/missing-screen rejection and validation.");
    }
    private static async Task Reject(Func<Task<string>> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("An unavailable screen chroma key toggle was accepted.");
    }
}
