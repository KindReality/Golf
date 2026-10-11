using System.IO;
using System.Text;

namespace PhotoBooth.Configuration;

public static class MachineConfigurationFiles
{
    public static string Resolve(string application, string? oneDriveRoot = null, string? hostname = null)
    {
        var root = oneDriveRoot ?? new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("OneDrive is not configured. Set OneDrive, OneDriveConsumer or OneDriveCommercial to its local folder.");
        var host = hostname ?? Environment.MachineName;
        foreach (var name in new[] { application, host })
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Application and computer names must be valid filename components.");
        return Path.GetFullPath(Path.Combine(root, "Home Technologies", "Bin", application, $"appsettings.{host}.json"));
    }

    // Copy once; a new build/template must never reset this computer's working configuration.
    public static string? Initialize(string path, string templateDirectory, Action<string> validate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (File.Exists(path) || File.Exists(path + ".bak")) return null;
                var source = Path.Combine(templateDirectory, "appsettings.defaults.json");
                var json = File.ReadAllText(source);
                validate(json);
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
                return source;
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(35);
            }
        }
    }
}
