using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using ExperienceX;
using Forms = System.Windows.Forms;

internal static class RendererDiagnosticsChecks
{
    internal static async Task Run()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                using var blocked = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                using var window = new Forms.Form { ShowInTaskbar = false, Width = 640, Height = 360 };
                VideoRenderer? renderer = null;
                RendererStallRecovery? recovery = null;
                Exception? failure = null;
                int arm = 0;
                try
                {
                    var captured = new TaskCompletionSource<RenderHealth>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var display = Forms.Screen.PrimaryScreen!.DeviceName;
                    var options = new ExperienceOptions { DeveloperMode = true, RendererRecovery = new() { StallTimeoutSeconds = 5 }, DisplayScreens = new() { [display] = [new() { Id = "a", Name = "screen1" }] } };
                    renderer = new(window.Handle, 640, 360, options, display, () => { }, () =>
                    {
                        if (Interlocked.Exchange(ref arm, 0) != 1) return;
                        blocked.Set(); release.Wait(TimeSpan.FromSeconds(22));
                    }, captureDiagnostics: (health, _) => captured.TrySetResult(health));
                    var restart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    recovery = new(() => [renderer.Diagnostics], () => options.RendererRecovery,
                        () => restart.TrySetResult());
                    var video = Environment.GetEnvironmentVariable("EXPERIENCEX_DIAGNOSTIC_TEST_VIDEO") ?? Path.GetFullPath("experiencex/Media/Video.mp4");
                    renderer.Play(video, new() { ScreenId = "a", Value = Path.GetFileName(video), RequestId = "diagnostic-test", Loop = true, Mute = true });
                    await Until(() => renderer.Diagnostics.Screens.Any(s => s.FramesTransferred > 5 && s.State == "Playing"), "Video starts before fault injection");
                    var restartClock = Stopwatch.StartNew();
                    Interlocked.Exchange(ref arm, 1);
                    await Until(() => blocked.IsSet, "Renderer is deliberately blocked");
                    renderer.Configure(options); // Queue work without waiting for the blocked render thread.
                    var health = await captured.Task.WaitAsync(TimeSpan.FromSeconds(8));
                    if (health.Status != "Stalled" || health.Operation != "BeforePresentCallback" || health.NativeThreadId == 0 ||
                        health.Screens.Single().Media != video || health.Screens[0].RequestId != "diagnostic-test" || health.PendingCommands < 1)
                        throw new Exception("Independent watchdog did not capture the blocked worker's operation and playback context.");
                    Console.WriteLine("PASS blocked render worker is diagnosed independently with operation, native thread ID, media, request and pending work.");
                    await restart.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    if (restartClock.Elapsed.TotalSeconds >= 9) throw new Exception("Fast recovery was delayed by the old ten-second detector.");
                    Console.WriteLine($"PASS independent recovery requests restart after {restartClock.Elapsed.TotalSeconds:F2}s while the rendering worker remains blocked.");
                    var frames = health.Screens[0].FramesTransferred;
                    release.Set();
                    await Until(() => renderer.Diagnostics.Screens.Single().FramesTransferred > frames+5, "Original renderer resumes after release");
                    renderer.Progress.Check();
                    if (renderer.Diagnostics.Status != "Active") throw new Exception("Diagnostics did not clear after actual progress resumed.");
                    Console.WriteLine("PASS released renderer resumes normally; test restart callback performs no process changes.");
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    release.Set();
                    recovery?.Dispose();
                    if (renderer is not null) await Task.Run(renderer.Dispose);
                    if (failure is null) done.TrySetResult(); else done.TrySetException(failure);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(35));
    }
    private static async Task Until(Func<bool> ready, string text)
    {
        var deadline = Stopwatch.StartNew();
        while (!ready()) { if (deadline.Elapsed.TotalSeconds > 15) throw new TimeoutException(text); await Task.Delay(25); }
    }
}
