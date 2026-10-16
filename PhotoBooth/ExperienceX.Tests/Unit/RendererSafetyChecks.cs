using System.Diagnostics;
using ExperienceX;

internal static class RendererSafetyChecks
{
    internal static void Run()
    {
        var deadline = new PresentationDeadline();
        long time = 1;
        deadline.Check(time, false, false);
        time += 4*Stopwatch.Frequency; deadline.Check(time, false, false);
        time += Stopwatch.Frequency;
        bool expired = false;
        try { deadline.Check(time, false, false); } catch (GraphicsPresentationTimeoutException) { expired = true; }
        Check(expired, "Repeated short presentation waits have a five-second overall deadline");
        deadline.Check(time, false, true); time += 100*Stopwatch.Frequency; deadline.Check(time, false, true);
        deadline.Check(time, false, false); time += 4*Stopwatch.Frequency; deadline.Check(time, true, false);
        time += 20*Stopwatch.Frequency; deadline.Check(time, false, false);
        Check(true, "Suspension and a received presentation permit reset the deadline");

        var now = DateTimeOffset.UtcNow;
        var options = new RendererRecoveryOptions();
        var health = new RenderHealth("display", 12, 123, "Stalled", "DrawVideoTriangles", "video", 4, 4, 3, 4, 5, false, [], 4);
        int restarts = 0;
        RendererStallRecovery Monitor() => new(() => [health], () => options, () => restarts++, monitor: false, now: () => now);
        using (var monitor = Monitor())
        {
            monitor.Check(); Check(restarts == 0, "Recovery waits for the configured continuous-stall threshold");
            health = health with { Status = "Active", StallAgeSeconds = 99 }; monitor.Check();
            health = health with { Status = "Suspended", Suspended = true }; monitor.Check();
            Check(restarts == 0, "Recovered or suspended output is not restarted");
            health = health with { Status = "Stalled", Suspended = false, StallAgeSeconds = 5 };
            options = options with { Enabled = false }; monitor.Check(); Check(restarts == 0, "Live configuration can disable automatic recovery");
            options = options with { Enabled = true }; monitor.Check(); monitor.Check();
            Check(restarts == 1, "Persistent stall requests exactly one in-flight restart per process");
        }
        // Existing hostname files may still contain obsolete cap fields. They must not cap recovery.
        options = ExperienceOptions.Parse("""{"RendererRecovery":{"Enabled":true,"StallTimeoutSeconds":30,"MaxRestarts":3,"RestartWindowSeconds":300}}""").RendererRecovery;
        health = health with { StallAgeSeconds = 30 };
        for (int i=0;i<20;i++) { using var monitor = Monitor(); monitor.Check(); monitor.Check(); }
        Check(restarts == 21, "Repeated restarted instances have no retry limit, including with old cap settings in configuration");
        using (var monitor = Monitor())
        {
            monitor.Check(); var initial = restarts;
            for (int i=0;i<20;i++)
            {
                monitor.RestartFailed(); monitor.Check();
                now = now.AddSeconds(59); monitor.Check();
                Check(restarts == initial+i, "Failed restart retains its sixty-second backoff");
                now = now.AddSeconds(1); monitor.Check(); monitor.Check();
            }
            Check(restarts == initial+20, "Failed script launches can keep retrying without an attempt cap");
        }
        int throws=0;
        using (var monitor = new RendererStallRecovery(()=>[health],()=>options,()=>{throws++;throw new IOException("Injected launch failure");},monitor:false,now:()=>now))
        {
            for(int i=0;i<20;i++) { monitor.Check(); monitor.Check(); now=now.AddSeconds(60); }
            Check(throws==20,"Thrown launch failures also retry without a cap and without duplicate requests");
        }
        bool rejected = false;
        try { (options with { StallTimeoutSeconds = double.NaN }).Validate(); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Recovery configuration rejects invalid timeouts");
    }
    private static void Check(bool good, string text) { if (!good) throw new Exception(text); Console.WriteLine("PASS "+text); }
}
