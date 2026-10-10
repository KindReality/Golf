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
                {"ConfigurationVersion":1,"Custom":42,"DisplayAliases":{"Left":"\\\\.\\DISPLAY1","Projector":"\\\\.\\DISPLAY2"},
                 "DisplayBackgrounds":{"Left":"transparent","Projector":"#123456"},"DisplayKeystones":{}}
                """);
            var store = new PointConfigurationStore(path);
            var options = ExperienceOptions.Parse(store.EnsureDisplays([@"\\.\DISPLAY1", @"\\.\DISPLAY2", @"\\.\DISPLAY3"]));
            if (options.ConfigurationVersion != ConfigurationSchema.CurrentVersion || options.DisplayBackgrounds.Count != 3 ||
                !options.DisplayBackgrounds["Left"].IsTransparent || options.DisplayBackgrounds["Left"].BackgroundColor != "#000000" ||
                options.DisplayBackgrounds["Projector"].IsTransparent || options.DisplayBackgrounds["Projector"].BackgroundColor != "#123456" ||
                options.DisplayBackgrounds[@"\\.\DISPLAY3"] != new DisplayBackgroundOptions())
                throw new Exception("Background migration or detected-display initialization lost settings.");
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            root["DisplayBackgrounds"]!["Projector"]!["CustomBackgroundField"] = "retain";
            File.WriteAllText(path, root.ToJsonString());
            var keystones = root["DisplayScreens"]!.ToJsonString();
            options = ExperienceOptions.Parse(await store.SetBackgroundAsync(@"\\.\DISPLAY2", true));
            var toggled = JsonNode.Parse(File.ReadAllText(path))!;
            if (!options.DisplayBackgrounds["Projector"].IsTransparent || options.DisplayBackgrounds["Projector"].BackgroundColor != "#123456" ||
                toggled["Custom"]!.GetValue<int>() != 42 || toggled["DisplayBackgrounds"]!["Projector"]!["CustomBackgroundField"]!.GetValue<string>() != "retain" ||
                toggled["DisplayScreens"]!.ToJsonString() != keystones || options.DisplayBackgrounds["Left"].IsTransparent != true)
                throw new Exception("Targeted toggle lost color, another display, calibration or unrelated fields.");
            options = ExperienceOptions.Parse(await store.SetBackgroundAsync(@"\\.\DISPLAY2", false, "#000000"));
            if (options.DisplayBackgrounds["Projector"].IsTransparent || options.DisplayBackgrounds["Projector"].BackgroundColor != "#000000")
                throw new Exception("Toggling a transparent background did not produce opaque black.");
            toggled = JsonNode.Parse(File.ReadAllText(path))!;
            if (toggled["DisplayBackgrounds"]!["Projector"]!["CustomBackgroundField"]!.GetValue<string>() != "retain" ||
                toggled["DisplayScreens"]!.ToJsonString() != keystones || !options.DisplayBackgrounds["Left"].IsTransparent)
                throw new Exception("Switching to black changed unrelated display or calibration settings.");
            options = ExperienceOptions.Parse(await store.SetBackgroundAsync(@"\\.\DISPLAY2", true));
            if (!options.DisplayBackgrounds["Projector"].IsTransparent) throw new Exception("Black did not become transparent.");
            options = ExperienceOptions.Parse(await store.SetBackgroundAsync(@"\\.\DISPLAY2", false, "#FFFFFF"));
            toggled = JsonNode.Parse(File.ReadAllText(path))!;
            if (options.DisplayBackgrounds["Projector"].IsTransparent || options.DisplayBackgrounds["Projector"].BackgroundColor != "#FFFFFF" ||
                toggled["DisplayScreens"]!.ToJsonString() != keystones || toggled["DisplayBackgrounds"]!["Projector"]!["CustomBackgroundField"]!.GetValue<string>() != "retain")
                throw new Exception("White choice was not opaque or changed unrelated settings.");
            var before = File.ReadAllText(path);
            store.EnsureDisplays([@"\\.\DISPLAY1", @"\\.\DISPLAY2", @"\\.\DISPLAY3"]);
            if (File.ReadAllText(path) != before)
                throw new Exception("Startup defaults overwrote configured backgrounds.");
            options = ExperienceOptions.Parse(store.EnsureDisplays([@"\\.\DISPLAY4"]));
            if (options.DisplayBackgrounds[@"\\.\DISPLAY4"] != new DisplayBackgroundOptions())
                throw new Exception("Hot-plugged display did not receive opaque black defaults.");
            if (!File.Exists(path + ".bak"))
                throw new Exception("Background changes did not retain a backup.");
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("Passed background migration, startup/hot-plug defaults, transparent/black/white persistence and preservation of calibration/unknown fields.");
    }
}
