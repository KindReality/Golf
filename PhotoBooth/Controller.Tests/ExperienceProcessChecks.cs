using Controller.Application;
using Controller.Diagnostics;

internal static class ExperienceProcessChecks
{
    internal static async Task Run()
    {
        bool enabled = true, running = true, fail = false;
        int starts = 0;
        var log = new ActivityLog();
        await using var monitor = new ExperienceProcessMonitor(() => enabled, () => running, token =>
        {
            token.ThrowIfCancellationRequested(); starts++;
            return fail ? Task.FromException(new IOException("fixture startup failed")) : Task.CompletedTask;
        }, log, run: false);
        await monitor.CheckAsync(1000, CancellationToken.None);
        Check(starts == 0, "An existing Experience instance is left running");
        running = false;
        await monitor.CheckAsync(2000, CancellationToken.None);
        await monitor.CheckAsync(3000, CancellationToken.None);
        Check(starts == 1, "Missing Experience starts once, with retry backoff");
        running = true; await monitor.CheckAsync(4000, CancellationToken.None);
        running = false; fail = true; await monitor.CheckAsync(5000, CancellationToken.None);
        Check(starts == 2 && log.Snapshot().Any(e => e.Message.Contains("ExperienceStartFailed")), "A later exit is detected and startup failure is logged");
        await monitor.CheckAsync(34000, CancellationToken.None);
        Check(starts == 2, "Failure backoff prevents repeated one-second launches");
        fail = false; await monitor.CheckAsync(35000, CancellationToken.None);
        Check(starts == 3, "Startup is retried after thirty seconds");
        enabled = false; await monitor.CheckAsync(70000, CancellationToken.None);
        Check(starts == 3, "Disabling supervision prevents relaunch");
        enabled = true;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await monitor.CheckAsync(80000, cancelled.Token);
        Check(starts == 3, "Shutdown cancellation prevents a new launch");
    }
    private static void Check(bool condition, string description)
    { if (!condition) throw new Exception(description); Console.WriteLine("PASS " + description); }
}
