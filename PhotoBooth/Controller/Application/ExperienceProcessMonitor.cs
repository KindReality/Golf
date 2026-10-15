using System.Diagnostics;
using Controller.Diagnostics;

namespace Controller.Application;

// The restart script owns staging, the single-start mutex and the local runtime copy.
internal sealed class ExperienceProcessMonitor : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly Func<bool> _enabled, _running;
    private readonly Func<CancellationToken, Task> _start;
    private readonly ActivityLog _log;
    private long _retryAt;
    private bool _missing;

    public ExperienceProcessMonitor(string source, Func<bool> enabled, ActivityLog log)
        : this(enabled, IsRunning, token => StartAsync(source, token), log) { }

    internal ExperienceProcessMonitor(Func<bool> enabled, Func<bool> running, Func<CancellationToken, Task> start, ActivityLog log, bool run = true)
    {
        _enabled = enabled; _running = running; _start = start; _log = log;
        _worker = run ? Task.Run(RunAsync) : Task.CompletedTask;
    }

    internal async Task CheckAsync(long now, CancellationToken token)
    {
        if (!_enabled() || token.IsCancellationRequested) return;
        if (_running())
        {
            if (_missing) _log.Info("ExperienceProcessDetected", "Experience is running again");
            _missing = false; _retryAt = 0; return;
        }
        if (!_missing) { _missing = true; _log.Info("ExperienceProcessMissing", "Experience is not running; starting its local copy"); }
        if (now < _retryAt) return;
        // One launch at a time; a failed start never creates a one-second restart storm.
        _retryAt = now + 30000;
        try
        {
            await _start(token).ConfigureAwait(false);
            _log.Info("ExperienceStartRequested", "Experience restart script completed");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { _log.Error("ExperienceStartFailed", ex); }
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                try { await CheckAsync(Environment.TickCount64, _stop.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                { _log.Error("ExperienceProcessCheckFailed", ex); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    internal static bool IsRunning()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("ExperienceX"))
            using (process)
            {
                try { if (process.SessionId == current.SessionId && !process.HasExited) return true; }
                catch (InvalidOperationException) { }
            }
        return false;
    }

    internal static async Task StopExperienceAsync(ActivityLog log)
    {
        // The hidden receiver works even when every presentation surface is transparent.
        var requested = PhotoBooth.Windows.ConfigurationShortcut.RequestClose("Experience");
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("ExperienceX"))
            using (process)
            {
                try
                {
                    if (process.SessionId != current.SessionId || process.HasExited) continue;
                    if (!requested) process.CloseMainWindow();
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
                    catch (TimeoutException)
                    {
                        log.Warning("ExperienceShutdownTimedOut", "Experience did not close within ten seconds; stopping the process", new { process.Id });
                        if (!process.HasExited) process.Kill();
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    }
                    log.Info("ExperienceProcessStopped", "Experience stopped for application shutdown", new { process.Id });
                }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
    }

    private static async Task StartAsync(string source, CancellationToken token)
    {
        var script = Path.Combine(source, "Restart-Experience.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("Experience restart script is missing from the OneDrive build folder.", script);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = source };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-SourceDirectory", source, "-AutomaticUpdate", "-StartIfNotRunning" })
            start.ArgumentList.Add(argument);
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("Could not start Experience's restart script.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
            if (!token.IsCancellationRequested) throw new TimeoutException("Experience startup did not finish within 45 seconds.");
            throw;
        }
        if (process.ExitCode != 0) throw new IOException($"Experience restart script exited with code {process.ExitCode}. See its logs/UpdateRestart.log.");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _worker.ConfigureAwait(false);
        _stop.Dispose();
    }
}
