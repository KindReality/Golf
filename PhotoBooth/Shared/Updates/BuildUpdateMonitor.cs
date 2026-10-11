using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using PhotoBooth.Diagnostics;

namespace PhotoBooth.Updates;

// Polling also handles OneDrive replacing files rather than raising normal write events.
public sealed class BuildUpdateMonitor : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly string _application, _source, _runtime, _running;
    private readonly UpdateStability _stability = new(TimeSpan.FromSeconds(5));
    private string? _lastWarning;

    public BuildUpdateMonitor(string application, string sourceDirectory, string? runtimeDirectory = null)
    {
        _application = application;
        _source = Path.GetFullPath(sourceDirectory);
        _runtime = Path.GetFullPath(runtimeDirectory ?? AppContext.BaseDirectory);
        _running = BuildUpdatePackage.BinaryFingerprint(_runtime, application);
        if (Path.TrimEndingDirectorySeparator(_source).Equals(Path.TrimEndingDirectorySeparator(_runtime), StringComparison.OrdinalIgnoreCase))
        {
            Telemetry.Warning("BuildUpdateMonitoringDisabled", new { Reason = "Run the restart CMD to use a local application copy.", Source = _source });
            _worker = Task.CompletedTask;
            return;
        }
        Telemetry.Info("BuildUpdateMonitoringStarted", new { Source = _source, PollSeconds = 1, SettleSeconds = 5 });
        _worker = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        var clock = Stopwatch.StartNew();
        var retryAt = TimeSpan.Zero;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token).ConfigureAwait(false);
                if (clock.Elapsed < retryAt) continue;
                var candidate = BuildUpdatePackage.BinaryFingerprint(_source, _application);
                if (candidate == _running) { _stability.Reset(); _lastWarning = null; continue; }
                // Include the manifest in settling so a second build resets the wait.
                var manifest = File.ReadAllText(Path.Combine(_source, BuildUpdatePackage.ManifestName));
                if (!_stability.IsReady(candidate + manifest, clock.Elapsed)) continue;
                BuildUpdatePackage.Verify(_source, _application, manifest);
                var scriptName = _application == "Controller" ? "Restart-Controller.ps1" : "Restart-Experience.ps1";
                var script = Path.Combine(_runtime, scriptName);
                if (!File.Exists(script)) throw new IOException("Local restart script is missing. Run the restart CMD manually.");
                var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _runtime
                };
                foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
                    "-SourceDirectory", _source, "-AutomaticUpdate" }) start.ArgumentList.Add(argument);
                Telemetry.Info("BuildUpdateRestartRequested", new { Source = _source, Application = _application, BinaryFingerprint = candidate });
                using var process = Process.Start(start) ?? throw new IOException("Could not start the restart script.");
                // Disposal cancels this wait when the script asks the current app to close.
                // The external restart process continues through copy and relaunch.
                await process.WaitForExitAsync(_stop.Token).ConfigureAwait(false);
                Telemetry.Warning("BuildUpdateRestartFailed", new { process.ExitCode, Log = Path.Combine(_runtime, "logs", "UpdateRestart.log") });
                retryAt = clock.Elapsed + TimeSpan.FromSeconds(60);
                _stability.Reset();
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                _stability.Reset();
                if (_lastWarning != ex.Message)
                {
                    _lastWarning = ex.Message;
                    Telemetry.Warning("BuildUpdateDeferred", new { Source = _source, Reason = ex.Message });
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _worker.ConfigureAwait(false);
        _stop.Dispose();
    }
}

public sealed class UpdateStability(TimeSpan settle)
{
    private string? _candidate;
    private TimeSpan _since;
    public bool IsReady(string candidate, TimeSpan now)
    {
        if (_candidate != candidate) { _candidate = candidate; _since = now; return false; }
        return now - _since >= settle;
    }
    public void Reset() => _candidate = null;
}

public static class BuildUpdatePackage
{
    public const string ManifestName = "build-manifest.sha256";
    public static string BinaryFingerprint(string directory, string application) =>
        Hash(Path.Combine(directory, application + ".exe")) + Hash(Path.Combine(directory, application + ".dll"));

    public static string Hash(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    public static void Verify(string directory, string application, string manifest)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = line.TrimEnd('\r').Split('|');
            if (entry.Length != 2 || entry[0].Length != 64 || entry[0].Any(c => !Uri.IsHexDigit(c)) ||
                string.IsNullOrWhiteSpace(entry[1]) || Path.IsPathRooted(entry[1]) || entry[1].Contains(':'))
                throw new IOException("Build manifest is incomplete or invalid.");
            var path = Path.GetFullPath(Path.Combine(root, entry[1]));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !names.Add(entry[1]))
                throw new IOException("Invalid or duplicate build manifest path.");
            if (!Hash(path).Equals(entry[0], StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Waiting for build file to sync: {entry[1]}");
        }
        var script = application == "Controller" ? "Restart-Controller.ps1" : "Restart-Experience.ps1";
        foreach (var required in new[] { application + ".exe", application + ".dll", application + ".deps.json",
            application + ".runtimeconfig.json", "appsettings.defaults.json", script, "Stage-ApplicationUpdate.ps1" })
            if (!names.Contains(required)) throw new IOException($"Build manifest is missing {required}.");
        if (application == "Controller" && !names.Contains("appsettings.environment.json"))
            throw new IOException("Build manifest is missing the Controller environment.");
        if (File.ReadAllText(Path.Combine(root, ManifestName)) != manifest)
            throw new IOException("Build changed during verification; waiting for sync.");
    }
}
