using ExperienceX;
using System.Text.Json.Nodes;

internal static class BackgroundPersistenceChecks
{
    public static async Task Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-backgrounds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "appsettings.json");
            File.WriteAllText(path, """
                {"ConfigurationVersion":1,"Custom":42,"MonitorAliases":{"Left":"\\\\.\\DISPLAY1","Projector":"\\\\.\\DISPLAY2"},
                 "MonitorBackgrounds":{"Left":"transparent","Projector":"#123456"},"DisplayKeystones":{}}
                """);
            var store = new KeystoneConfigurationStore(path);
            var options = ExperienceOptions.Parse(store.EnsureDisplays([@"\\.\DISPLAY1", @"\\.\DISPLAY2", @"\\.\DISPLAY3"]));
            if (options.ConfigurationVersion != 2 || options.MonitorBackgrounds.Count != 3 ||
                !options.MonitorBackgrounds["Left"].IsTransparent || options.MonitorBackgrounds["Left"].BackgroundColor != "#000000" ||
                options.MonitorBackgrounds["Projector"].IsTransparent || options.MonitorBackgrounds["Projector"].BackgroundColor != "#123456" ||
                options.MonitorBackgrounds[@"\\.\DISPLAY3"] != new MonitorBackgroundOptions())
                throw new Exception("Background migration or detected-display initialization lost settings.");
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            root["MonitorBackgrounds"]!["Projector"]!["CustomBackgroundField"] = "retain";
            File.WriteAllText(path, root.ToJsonString());
            var keystones = root["DisplayKeystones"]!.ToJsonString();
            options = ExperienceOptions.Parse(await store.ToggleBackgroundTransparencyAsync(@"\\.\DISPLAY2"));
            var toggled = JsonNode.Parse(File.ReadAllText(path))!;
            if (!options.MonitorBackgrounds["Projector"].IsTransparent || options.MonitorBackgrounds["Projector"].BackgroundColor != "#123456" ||
                toggled["Custom"]!.GetValue<int>() != 42 || toggled["MonitorBackgrounds"]!["Projector"]!["CustomBackgroundField"]!.GetValue<string>() != "retain" ||
                toggled["DisplayKeystones"]!.ToJsonString() != keystones || options.MonitorBackgrounds["Left"].IsTransparent != true)
                throw new Exception("Targeted toggle lost color, another monitor, calibration or unrelated fields.");
            options = ExperienceOptions.Parse(await store.ToggleBackgroundTransparencyAsync(@"\\.\DISPLAY2"));
            if (options.MonitorBackgrounds["Projector"].IsTransparent || options.MonitorBackgrounds["Projector"].BackgroundColor != "#123456")
                throw new Exception("Second toggle did not restore the configured solid color.");
            var before = File.ReadAllText(path);
            store.EnsureDisplays([@"\\.\DISPLAY1", @"\\.\DISPLAY2", @"\\.\DISPLAY3"]);
            if (File.ReadAllText(path) != before)
                throw new Exception("Startup defaults overwrote configured backgrounds.");
            options = ExperienceOptions.Parse(store.EnsureDisplays([@"\\.\DISPLAY4"]));
            if (options.MonitorBackgrounds[@"\\.\DISPLAY4"] != new MonitorBackgroundOptions())
                throw new Exception("Hot-plugged display did not receive opaque black defaults.");
            if (!File.Exists(path + ".bak"))
                throw new Exception("Background changes did not retain a backup.");
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("Passed background migration, startup/hot-plug defaults, per-monitor persistent toggles, color independence and preservation of calibration/unknown fields.");
    }
}
