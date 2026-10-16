using System.Diagnostics;
using ExperienceX;

internal static class RendererProgressChecks
{
    internal static void Run()
    {
        long now = 1;
        int captures = 0;
        using var progress = new RendererProgress("test-display", () => 3, (_, _) => captures++, monitor: false, now: () => now);
        void Advance(int seconds) => now += seconds*Stopwatch.Frequency;
        var screen = new ScreenOptions { Id = "video", Name = "screen1" };
        progress.RegisterThread(42, 123);
        using (progress.Begin("IdleWait")) { Advance(60); progress.Check(); Check(progress.Snapshot().Status == "Idle" && captures == 0, "Idle waits do not report stalls"); }
        progress.Beat();
        progress.StartVideo(screen, "Eye.mp4", new() { RequestId = "request-a", Loop = true });
        progress.FrameTransferred(screen.Id); progress.Playing(screen.Id); progress.Position(screen.Id, 3.25); progress.Presented();
        Advance(9); progress.Beat(); progress.Check();
        Check(captures == 0 && progress.Snapshot().Status == "Active", "Healthy video respects the ten-second threshold");
        Advance(1); progress.Beat(); progress.Check(); progress.Check();
        var health = progress.Snapshot();
        Check(captures == 1 && health.Status == "Stalled" && health.Screens.Single().Stalled && health.Screens[0].PositionSeconds == 3.25,
            "No-frame stall is detected even while the worker is responsive, with one capture per incident");
        progress.ContinueVideo(screen.Id, new() { RequestId = "request-b", Loop = true }); progress.Check();
        Check(captures == 1 && progress.Snapshot().Screens.Single().FrameAgeSeconds >= 10, "Repeated loop commands do not reset the progress timer");
        progress.StartVideo(new() { Id = "healthy", Name = "screen2" }, "Other.mp4", new() { RequestId = "other" });
        progress.FrameTransferred("healthy"); progress.Playing("healthy");
        Check(progress.Snapshot().Screens.Single(s => s.ScreenId == "video").Stalled, "Another healthy screen does not mask a stalled video");
        progress.Suspend(true); progress.Check(); Advance(40); progress.Check();
        Check(progress.Snapshot().Status == "Suspended" && captures == 1, "Minimized or disconnected displays are excluded");
        progress.Suspend(false); progress.Check();
        Check(captures == 1 && progress.Snapshot().Status == "Active", "Restoring a display grants time for frames to resume without inventing a frame transfer");
        progress.FrameTransferred("video"); progress.FrameTransferred("healthy"); progress.Beat(); progress.Check();
        Check(progress.Snapshot().Status == "Active", "Progress resumption clears the incident");
        using (progress.Begin("MediaShutdown", "video"))
        {
            Advance(11); progress.Check();
            health = progress.Snapshot();
            Check(captures == 2 && health.Operation == "MediaShutdown" && health.OperationScreenId == "video" && health.NativeThreadId == 123 && health.PendingCommands == 3,
                "Blocked operation is diagnosed with thread IDs and queue depth without querying the worker");
        }
        progress.EndVideo("video"); progress.EndVideo("healthy"); progress.Beat(); progress.Check();
        progress.StartVideo(screen, "Eye.mp4", new()); Advance(19); progress.Beat(); progress.Check();
        Check(captures == 2, "Opening has a separate grace period");
        Advance(1); progress.Beat(); progress.Check(); Check(captures == 3, "A stuck opening is reported after its grace period");
        progress.Dispose(); Check(progress.Snapshot().Status == "Stopped", "Stopped diagnostics no longer report a playback stall");
        Check(RendererDiagnosticCapture.RunHelper([RendererDiagnosticCapture.HelperArgument, "1", "1", "../escape.dmp"]) == 2,
            "Diagnostic helper rejects paths outside its local capture folder");
        foreach(var timeout in new[]{3d,5d})
        {
            long ticks=1; int fastCaptures=0, restarts=0;
            using var fast = new RendererProgress("fast",()=>0,(_,_)=>fastCaptures++,monitor:false,now:()=>ticks,stallSeconds:()=>RendererProgress.WarningTimeout(timeout));
            using var recovery = new RendererStallRecovery(()=>[fast.Snapshot()],()=>new(){StallTimeoutSeconds=timeout},()=>restarts++,monitor:false);
            fast.RegisterThread(1,1);fast.StartVideo(screen,"Eye.mp4",new(){Loop=true});fast.Playing(screen.Id);fast.FrameTransferred(screen.Id);
            var warning = RendererProgress.WarningTimeout(timeout);
            ticks+=(long)(warning*Stopwatch.Frequency);fast.Beat();fast.Check();recovery.Check();
            Check(fastCaptures==1 && restarts==0,"Diagnostics start before the shorter recovery deadline");
            ticks+=(long)((timeout-warning)*Stopwatch.Frequency);fast.Beat();recovery.Check();
            Check(restarts==1,$"Responsive worker with frozen video recovers at {timeout} seconds without waiting ten seconds");
        }
        long startupTime=1; int startupCaptures=0;
        using var startup = new RendererProgress("startup",()=>0,(_,_)=>startupCaptures++,monitor:false,now:()=>startupTime,stallSeconds:()=>3);
        startup.RegisterThread(1,1);
        startupTime+=8*Stopwatch.Frequency;startup.Check();
        Check(startupCaptures==0,"Cold graphics initialization is not mistaken for a five-second playback freeze");
        startupTime+=12*Stopwatch.Frequency;startup.Check();
        Check(startupCaptures==1,"An indefinitely blocked initialization is still diagnosed after its separate grace period");
    }
    private static void Check(bool good, string text) { if (!good) throw new Exception(text); Console.WriteLine("PASS "+text); }
}
