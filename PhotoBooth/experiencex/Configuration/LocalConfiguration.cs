using System.IO;
using System.Text;
using System.Text.Json;

namespace ExperienceX;

internal static class LocalConfiguration
{
    public static string Resolve(string? localApplicationData = null)
    {
        var root = localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("The local application data folder is unavailable.");
        return Path.GetFullPath(Path.Combine(root, "Home Technologies", "ExperienceX", "Configuration", "appsettings.json"));
    }

    // The previous OneDrive location is read only for a one-time migration, never watched.
    public static string? PreviousConfigurationPath(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var root = new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }.Select(environment).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(root))
            return null;
        return Path.GetFullPath(Path.Combine(root, "Home Technologies", "Bin", "Configuration", "ExperienceX", "appsettings.json"));
    }
    // Only the first launch seeds the local file. Existing local settings always take precedence.
    public static string? Initialize(string path, string applicationDirectory, string? previousConfigurationPath = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (File.Exists(path) || File.Exists(path + ".bak"))
                    return null;
                string? source = null;
                string? json = null;
                var candidates = new List<string>();
                if (previousConfigurationPath is not null)
                {
                    candidates.Add(previousConfigurationPath);
                    candidates.Add(previousConfigurationPath + ".bak");
                }
                candidates.AddRange(new[] { "appsettings.json", "appsettings.json.bak", "appsettings.defaults.json" }.Select(name => Path.Combine(applicationDirectory, name)));
                foreach (var candidate in candidates)
                {
                    if (!File.Exists(candidate))
                        continue;
                    try
                    {
                        var text = File.ReadAllText(candidate);
                        ExperienceOptions.Parse(text);
                        json = text;
                        source = candidate;
                        break;
                    }
                    catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException) { }
                }
                json ??= JsonSerializer.Serialize(new ExperienceOptions(), new JsonSerializerOptions { WriteIndented = true });
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var bytes = Encoding.UTF8.GetBytes(json);
                        file.Write(bytes);
                        file.Flush(true);
                    }
                    File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return source ?? "Built-in defaults";
            }
            catch (IOException) when (attempt < 9) { Thread.Sleep(35); }
        }
    }
}
