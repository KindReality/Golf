using System.Diagnostics;
using ExperienceX;
using Forms = System.Windows.Forms;

internal static class RendererEditingSoakChecks
{
    internal static async Task Run(bool softwareVideoDecoding = false)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            try
            {
                using var window = new Forms.Form { ShowInTaskbar = false, ClientSize = new(640, 360) };
                var video = Environment.GetEnvironmentVariable("EXPERIENCEX_DIAGNOSTIC_TEST_VIDEO") ?? Path.GetFullPath("experiencex/Media/Video.mp4");
                if (!File.Exists(video)) throw new FileNotFoundException("Provide EXPERIENCEX_DIAGNOSTIC_TEST_VIDEO.", video);
                var screens = Enumerable.Range(0, 3).Select(i => new ScreenOptions { Id = "video"+i, Name = "screen"+i }).ToList();
                var options = new ExperienceOptions { ConfigurationMode = true, GraphicsDebugLayerEnabled = true, SoftwareVideoDecoding = softwareVideoDecoding,
                    TransitionInSeconds = .05, TransitionOutSeconds = .1, DisplayScreens = new() { ["TEST"] = screens } };
                using var renderer = new VideoRenderer(window.Handle, 640, 360, options, "TEST", () => { });
                ExperienceRequest Request(string id, string token) => new() { ScreenId = id, RequestId = token, Value = Path.GetFileName(video), Loop = true, Mute = true };
                foreach (var screen in screens) renderer.Play(video, Request(screen.Id, "initial-"+screen.Id));
                var clock = Stopwatch.StartNew();
                while (!renderer.Diagnostics.Screens.All(s => s.FramesTransferred > 5) || renderer.Diagnostics.Screens.Length != 3)
                {
                    if (clock.Elapsed.TotalSeconds > 15 || renderer.State == RendererState.Failed) throw new Exception("Three-screen soak startup failed: "+renderer.Diagnostics);
                    Thread.Sleep(25);
                }
                clock.Restart();
                long lastPresents = 0;
                int edits = 0;
                while (clock.Elapsed.TotalSeconds < 60)
                {
                    var health = renderer.Diagnostics;
                    if (renderer.State == RendererState.Failed || health.Status == "Stalled" || health.PresentsSubmitted <= lastPresents)
                        throw new Exception("Rendering stopped during mixed editing: "+health);
                    lastPresents = health.PresentsSubmitted;
                    var index = edits % 3;
                    var id = "video"+index;
                    var inset = .05 + .002*(edits%40);
                    var points = new PointOptions { Enabled = true, TopLeft = new(inset,inset), TopRight = new(1-inset,inset),
                        BottomRight = new(1-inset,1-inset), BottomLeft = new(inset,1-inset) };
                    screens[index] = screens[index] with { Name = "edited-"+edits, Points = points };
                    var configured = screens.ToList();
                    if (edits%4 < 2) configured.Add(new() { Id = "mat", Name = "mat", Type = "mat", Points = points });
                    options = options with { DisplayScreens = new() { ["TEST"] = configured } };
                    renderer.Configure(options);
                    renderer.ShowCalibration(index, id);
                    renderer.PreviewPoints(points with { TopLeft = new(inset+.01,inset) }, id);
                    renderer.PreviewPoints(null, id);
                    renderer.FlashCalibrationPoints(15, edits%2 == 0, id);
                    if (edits%8 == 0) renderer.Play(video, Request(id, "replacement-"+edits) with { Loop = false });
                    if (edits%8 == 1) renderer.Play(video, Request("video"+((index+2)%3), "loop-"+edits));
                    if (edits%12 == 0) renderer.Configure(options with { DisplayScreens = new() { ["TEST"] = configured.Where(s => s.Id != id).ToList() } });
                    if (edits%12 == 0)
                    {
                        // Confirm removal actually executes before re-adding it; Configure otherwise coalesces.
                        var removed = renderer.SnapshotAsync().GetAwaiter().GetResult();
                        if (removed.Any(s => s.Id == id)) throw new Exception("Screen removal did not execute.");
                        renderer.Configure(options); renderer.Play(video, Request(id,"recreated-"+edits));
                    }
                    edits++;
                    Thread.Sleep(250);
                }
                var final = renderer.SnapshotAsync().GetAwaiter().GetResult();
                if (final.Count(s => s.State is RendererState.Playing or RendererState.Opening) != 3 || renderer.Diagnostics.PresentsSubmitted < 600)
                    throw new Exception("Playback did not survive the editing soak.");
                Console.WriteLine($"PASS 60-second three-video soak: {edits} edits, screen removal/recreation, replacement, mat changes, grids and flashes; {renderer.Diagnostics.PresentsSubmitted} presentations; software decoding={softwareVideoDecoding}.");
            }
            catch (Exception ex) { failure = ex; }
            finally { if (failure is null) done.TrySetResult(); else done.TrySetException(failure); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(90));
    }
}
