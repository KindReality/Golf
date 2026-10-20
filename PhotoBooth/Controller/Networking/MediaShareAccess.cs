using System.Diagnostics;
using PhotoBooth.Networking;

namespace Controller.Networking;

internal static class MediaShareAccess
{
    // Credentials remain in the existing installer-style script and Windows Credential Manager.
    public static async Task PrepareAsync(string hostname, CancellationToken token)
    {
        ControllerDiscovery.MediaShare(hostname);
        var script = Path.Combine(AppContext.BaseDirectory, "MediaCredentials.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("Media credential setup script is missing.", script);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Hostname", hostname }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not initialize media credentials.");
        try { await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        if (process.ExitCode != 0) throw new IOException("Controller media credential setup failed.");
    }
}
