using ExperienceX;

internal static class MachineConfigurationChecks
{
    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ExperienceX-MachineConfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var expected = Path.Combine(directory, "Home Technologies", "Bin", "ExperienceX", "appsettings.ALPHA.json");
            Check(MachineConfiguration.Resolve(directory, "ALPHA") == expected, "OneDrive hostname configuration path was not used.");
            Check(MachineConfiguration.Resolve(directory).EndsWith($"appsettings.{Environment.MachineName}.json"), "Configuration filename did not use the computer hostname.");
            try
            {
                MachineConfiguration.Resolve(" ");
                throw new Exception("Missing OneDrive silently fell back.");
            }
            catch (InvalidOperationException) { }

            var app = Path.GetDirectoryName(expected)!;
            Directory.CreateDirectory(app);
            var template = Path.Combine(app, "appsettings.defaults.json");
            const string settings = """
                {"TransitionInSeconds":17,"configurationMode":false,"Custom":"preserved",
                 "DisplayAliases":{},"DisplayBackgrounds":{},"DisplayScreens":{}}
                """;
            File.WriteAllText(template, settings);
            File.WriteAllText(Path.Combine(app, "appsettings.json"), "{\"TransitionInSeconds\":22}");
            File.WriteAllText(Path.Combine(app, "appsettings.json.bak"), "{\"TransitionInSeconds\":23}");
            Check(MachineConfiguration.Initialize(expected, app) == template, "First launch did not use the defaults template exclusively.");
            Check(File.ReadAllText(expected) == settings, "Template copy changed settings or removed unknown fields.");

            // Startup/hot-plug inventory must write only to this computer's hostname file.
            var store = new PointConfigurationStore(expected);
            var detected = ExperienceOptions.Parse(store.EnsureDisplays([@"\\.\DISPLAY1", @"\\.\DISPLAY2"]));
            Check(detected.DisplayScreens.Count == 2 && detected.DisplayScreens.Values.All(screens => screens.Count == 1 && screens[0].Name == "screen1"), "Detected displays did not receive one default screen.");
            Check(detected.DisplayBackgrounds.Count == 2 && detected.DisplayBackgrounds.Values.All(background => !background.IsTransparent && background.BackgroundColor == "#000000"), "Detected displays did not receive opaque black backgrounds.");
            Check(File.ReadAllText(template) == settings, "Hardware detection modified the shipped template.");
            var localSettings = File.ReadAllText(expected);
            File.WriteAllText(template, "{\"TransitionInSeconds\":40}");
            Check(MachineConfiguration.Initialize(expected, app) is null && File.ReadAllText(expected) == localSettings, "A new template overwrote existing computer settings.");

            var otherProfile = MachineConfiguration.Resolve(directory, "LAPTOPMSPIN");
            Check(MachineConfiguration.Initialize(otherProfile, app) == template && ExperienceOptions.Parse(File.ReadAllText(otherProfile)).TransitionInSeconds == 40, "Another machine did not receive the current defaults independently.");
            Check(File.ReadAllText(expected) == localSettings, "Creating another hostname file modified the first machine's configuration.");
            File.Delete(template);
            Check(MachineConfiguration.Initialize(expected, app) is null, "Existing computer configuration required the shipped template.");
            ExpectFailure(() => MachineConfiguration.Initialize(Path.Combine(directory, "missing", "appsettings.json"), app), "Missing template was silently replaced with other settings.");
            File.WriteAllText(template, "{incomplete");
            var invalidPath = Path.Combine(directory, "invalid", "appsettings.json");
            ExpectFailure(() => MachineConfiguration.Initialize(invalidPath, app), "Invalid template was silently accepted.");
            Check(!File.Exists(invalidPath), "Invalid template created a partial hostname configuration.");

            var backupPath = Path.Combine(directory, "backup", "appsettings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.WriteAllText(backupPath + ".bak", settings);
            Check(MachineConfiguration.Initialize(backupPath, app) is null && !File.Exists(backupPath), "Computer backup was replaced with shipped settings.");
            Check(new PointConfigurationStore(backupPath).RecoverAtStartup() && File.ReadAllText(backupPath).TrimEnd() == settings.TrimEnd(), "Local backup could not be recovered.");

            File.WriteAllText(template, settings);
            var concurrentPath = Path.Combine(directory, "concurrent", "appsettings.json");
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => MachineConfiguration.Initialize(concurrentPath, app))));
            Check(results.Count(result => result is not null) == 1 && File.ReadAllText(concurrentPath) == settings, "Concurrent initializations did not preserve one complete local file.");
            Check(!Directory.EnumerateFiles(directory, "*.tmp", SearchOption.AllDirectories).Any(), "Initialization left temporary files.");
            Console.WriteLine("Passed defaults-only seeding, hardware inventory, independent hostname files, preservation, backup recovery and concurrent initialization checks.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void ExpectFailure(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or ArgumentException or System.Text.Json.JsonException) { return; }
        throw new Exception(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
