using System.Diagnostics;
using ExperienceX;
using Forms = System.Windows.Forms;

internal static class RendererSafetyHardwareChecks
{
    internal static async Task Run()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var window = new Forms.Form { ShowInTaskbar = false, ClientSize = new(640, 360) };
                using (var graphics = new GraphicsResources(window.Handle, 640, 360, "ownership-test", debugLayer: true))
                {
                    graphics.Initialize();
                    bool rejected = false;
                    try { Task.Run(() => graphics.Context).GetAwaiter().GetResult(); } catch (InvalidOperationException) { rejected = true; }
                    if (!rejected) throw new Exception("Release graphics ownership guard did not reject another thread.");
                    graphics.ReportDebugMessages();
                }
                Console.WriteLine("PASS production graphics ownership guard and optional debug-layer initialization/fallback.");

                var video = Environment.GetEnvironmentVariable("EXPERIENCEX_DIAGNOSTIC_TEST_VIDEO") ?? Path.GetFullPath("experiencex/Media/Video.mp4");
                int fail = 0;
                VideoRenderer? target = null;
                var options = new ExperienceOptions { TransitionInSeconds = 0, DisplayScreens = new() { ["TEST"] = [new() { Id = "a", Name = "screen1" }] } };
                using var renderer = new VideoRenderer(window.Handle, 640, 360, options, "TEST", () => { }, () =>
                {
                    if (Interlocked.CompareExchange(ref fail, 2, 1) == 1) target!.Play(video, Request("failed-replacement") with { Loop = false });
                    else if (Interlocked.CompareExchange(ref fail, 0, 2) == 2) throw new InvalidOperationException("Injected presentation failure during replacement.");
                });
                target = renderer;
                ExperienceRequest Request(string id) => new() { ScreenId = "a", RequestId = id, Value = Path.GetFileName(video), Loop = true, Mute = true };
                ScreenPlaybackSnapshot[] Read()
                {
                    var snapshot = renderer.SnapshotAsync().GetAwaiter().GetResult();
                    if (renderer.State == RendererState.Failed) throw new Exception("Renderer failed: " + renderer.Diagnostics);
                    return snapshot;
                }
                void Until(Func<bool> ready)
                {
                    var clock = Stopwatch.StartNew(); while (!ready()) { if (clock.Elapsed.TotalSeconds > 12) throw new TimeoutException("Renderer safety condition not reached."); Thread.Sleep(20); }
                }
                renderer.Play(video, Request("original")); Until(() => Read().Single().State == RendererState.Playing);
                // Schedule replacement during one frame, then fail its transparent presentation on the next worker iteration.
                Volatile.Write(ref fail, 1);
                Until(() => Read().Single() is { State: RendererState.Idle, RequestId: null });
                renderer.Play(video, Request("after-failure"));
                Until(() => Read().Single() is { State: RendererState.Playing, RequestId: "after-failure", Frames: > 2 });
                Console.WriteLine("PASS failed transparent-frame presentation still closes the decoder, returns to Idle, and permits later playback.");
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { done.TrySetResult(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(35));
    }
}
