using ExperienceX;

internal static class LocalConfigurationChecks
{
    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ExperienceX-LocalConfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var expected = Path.Combine(directory, "Home Technologies", "ExperienceX", "Configuration", "appsettings.json");
            Check(LocalConfiguration.Resolve(directory) == expected, "Local application data root was not used.");
            Check(LocalConfiguration.Resolve() == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Home Technologies", "ExperienceX", "Configuration", "appsettings.json"), "Default path did not use the local application data folder.");
            try
            {
                LocalConfiguration.Resolve(" ");
                throw new Exception("Missing local application data silently fell back.");
            }
            catch (InvalidOperationException) { }
            var previousPath = Path.Combine(directory, "Home Technologies", "Bin", "Configuration", "ExperienceX", "appsettings.json");
            var values = new Dictionary<string, string?> { ["OneDrive"] = directory, ["OneDriveConsumer"] = directory + "-consumer", ["OneDriveCommercial"] = directory + "-commercial" };
            string? EnvironmentValue(string name) => values.GetValueOrDefault(name);
            Check(LocalConfiguration.PreviousConfigurationPath(EnvironmentValue) == previousPath, "Previous OneDrive location was not resolved for migration.");
            values["OneDrive"] = " ";
            Check(LocalConfiguration.PreviousConfigurationPath(EnvironmentValue)!.StartsWith(directory + "-consumer"), "Consumer migration fallback failed.");
            values["OneDriveConsumer"] = null;
            Check(LocalConfiguration.PreviousConfigurationPath(EnvironmentValue)!.StartsWith(directory + "-commercial"), "Commercial migration fallback failed.");
            values["OneDriveCommercial"] = null;
            Check(LocalConfiguration.PreviousConfigurationPath(EnvironmentValue) is null, "Missing OneDrive should not prevent local configuration startup.");

            var app = Path.Combine(directory, "application");
            Directory.CreateDirectory(app);
            var local = Path.Combine(app, "appsettings.json");
            const string settings = "{\"MaxVideoPlaybackLength\":17,\"configurationMode\":false,\"Custom\":\"preserved\"}";
            File.WriteAllText(local, settings);
            Check(LocalConfiguration.Initialize(expected, app) == local, "First launch did not migrate live settings.");
            Check(File.ReadAllText(expected) == settings, "Migration changed settings or removed unknown fields.");
            File.WriteAllText(local, "{\"MaxVideoPlaybackLength\":22}");
            Check(LocalConfiguration.Initialize(expected, app) is null && File.ReadAllText(expected) == settings, "Existing local settings were overwritten.");

            Directory.CreateDirectory(Path.GetDirectoryName(previousPath)!);
            File.WriteAllText(previousPath, settings);
            var migratedPath = Path.Combine(directory, "from-onedrive", "appsettings.json");
            Check(LocalConfiguration.Initialize(migratedPath, app, previousPath) == previousPath && File.ReadAllText(migratedPath) == settings, "Current OneDrive settings were not migrated before executable-local settings.");
            File.WriteAllText(previousPath, "{\"MaxVideoPlaybackLength\":40}");
            Check(LocalConfiguration.Initialize(migratedPath, app, previousPath) is null && File.ReadAllText(migratedPath) == settings, "Later OneDrive changes overwrote local settings.");
            File.WriteAllText(previousPath, "{incomplete");
            File.WriteAllText(previousPath + ".bak", settings);
            var previousBackupPath = Path.Combine(directory, "from-onedrive-backup", "appsettings.json");
            Check(LocalConfiguration.Initialize(previousBackupPath, app, previousPath) == previousPath + ".bak", "A valid previous OneDrive backup was not migrated.");
            var missingPreviousPath = Path.Combine(directory, "without-onedrive", "appsettings.json");
            Check(LocalConfiguration.Initialize(missingPreviousPath, app, Path.Combine(directory, "unavailable", "appsettings.json")) == local, "Unavailable OneDrive migration source prevented local startup.");

            var backupPath = Path.Combine(directory, "backup", "appsettings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.WriteAllText(backupPath + ".bak", settings);
            Check(LocalConfiguration.Initialize(backupPath, app) is null && !File.Exists(backupPath), "Local backup was ignored during initialization.");
            Check(new KeystoneConfigurationStore(backupPath).RecoverAtStartup() && File.ReadAllText(backupPath).TrimEnd() == settings, "Local backup could not be recovered.");

            File.WriteAllText(local, "{incomplete");
            File.WriteAllText(local + ".bak", settings);
            var migrationPath = Path.Combine(directory, "migration", "appsettings.json");
            Check(LocalConfiguration.Initialize(migrationPath, app) == local + ".bak" && File.ReadAllText(migrationPath) == settings, "Valid local backup did not take precedence over defaults.");
            File.WriteAllText(local + ".bak", "{\"MaxVideoPlaybackLength\":-1}");
            var defaultsPath = Path.Combine(app, "appsettings.defaults.json");
            File.WriteAllText(defaultsPath, settings);
            var templatePath = Path.Combine(directory, "template", "appsettings.json");
            Check(LocalConfiguration.Initialize(templatePath, app) == defaultsPath, "Invalid live and backup settings did not fall back to shipped defaults.");

            var emptyApp = Path.Combine(directory, "empty");
            Directory.CreateDirectory(emptyApp);
            var builtInPath = Path.Combine(directory, "built-in", "appsettings.json");
            Check(LocalConfiguration.Initialize(builtInPath, emptyApp) == "Built-in defaults", "Built-in defaults were not created.");
            ExperienceOptions.Parse(File.ReadAllText(builtInPath));

            var concurrentPath = Path.Combine(directory, "concurrent", "appsettings.json");
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => LocalConfiguration.Initialize(concurrentPath, app))));
            Check(results.Count(result => result is not null) == 1 && File.ReadAllText(concurrentPath) == settings, "Concurrent initializations did not preserve one complete local file.");
            Check(!Directory.EnumerateFiles(directory, "*.tmp", SearchOption.AllDirectories).Any(), "Initialization left temporary files.");
            Console.WriteLine("Passed local configuration path, one-time OneDrive migration, local preservation, backup/default recovery, and concurrent initialization checks.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
