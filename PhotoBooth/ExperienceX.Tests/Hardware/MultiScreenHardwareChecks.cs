using System.Diagnostics;
using ExperienceX;
using Forms = System.Windows.Forms;

internal static class MultiScreenHardwareChecks
{
    public static async Task Run()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var video = Environment.GetEnvironmentVariable("EXPERIENCEX_TEST_VIDEO") ?? Path.Combine(root, "experiencex", "Media", "ping.mp4");
        if (!File.Exists(video))
            throw new FileNotFoundException("Provide EXPERIENCEX_TEST_VIDEO for the multi-screen playback check; Media is deliberately excluded from Git.", video);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var window = new Forms.Form { ClientSize = new(640, 360), ShowInTaskbar = false };
                ScreenOptions[] screens = [
                    new() { Id = "a", Name = "screen1", Points = new() { Enabled = true, TopRight = new(.5, 0), BottomRight = new(.5, 1) } },
                    new() { Id = "b", Name = "screen2", Points = new() { Enabled = true, TopLeft = new(.5, 0), BottomLeft = new(.5, 1) } }];
                var options = new ExperienceOptions { TransitionInSeconds = .05, DisplayScreens = new() { ["TEST"] = screens.ToList() } };
                using var renderer = new VideoRenderer(window.Handle, 640, 360, options, "TEST", () => { });
                ScreenPlaybackSnapshot[] Read() => renderer.SnapshotAsync().GetAwaiter().GetResult();
                ScreenPlaybackSnapshot[] Until(Func<ScreenPlaybackSnapshot[], bool> ready)
                {
                    var clock = Stopwatch.StartNew();
                    while (clock.Elapsed.TotalSeconds < 12)
                    {
                        var snapshot = Read();
                        if (ready(snapshot))
                            return snapshot;
                        if (renderer.State == RendererState.Failed)
                            throw new Exception("The display renderer failed during multi-screen playback.");
                        Thread.Sleep(20);
                    }
                    throw new TimeoutException("Multi-screen playback did not reach the expected state.");
                }
                renderer.Play(video, new()
                {
                    Experience = "video",
                    Value = "ping.mp4",
                    ScreenId = "a",
                    RequestId = "first-a",
                    Mute = true
                });
                renderer.Play(video, new()
                {
                    Experience = "video",
                    Value = "ping.mp4",
                    ScreenId = "b",
                    RequestId = "first-b",
                    Mute = true
                });
                var initial = Until(s => s.Length == 2 && s.All(t => t.Frames > 2 && t.Position > .2 && t.Opacity > .9));
                var b = initial.Single(t => t.Id == "b");
                renderer.Play(video, new()
                {
                    Experience = "video",
                    Value = "ping.mp4",
                    ScreenId = "a",
                    RequestId = "replacement-a",
                    Mute = true
                });
                var replaced = Until(s => s.Single(t => t.Id == "a").RequestId == "replacement-a" && s.Single(t => t.Id == "a").Position > .2);
                var kept = replaced.Single(t => t.Id == "b");
                if (kept.RequestId != "first-b" || kept.Position <= b.Position || kept.Frames <= b.Frames)
                    throw new Exception("Replacing one video restarted another screen.");
                options = options with
                {
                    DisplayScreens = new()
                    {
                        ["TEST"] = [screens[0] with { Name = "Renamed" }, screens[1]]
                    }
                };
                renderer.Configure(options);
                var renamed = Until(s => s[0].Name == "Renamed" && s[1].Position > kept.Position + .1);
                if (renamed[0].RequestId != "replacement-a" || renamed[1].RequestId != "first-b")
                    throw new Exception("Renaming a screen interrupted playback.");
                options = options with
                {
                    DisplayScreens = new()
                    {
                        ["TEST"] = [screens[1]]
                    }
                };
                renderer.Configure(options);
                var removed = Until(s => s.Length == 1 && s[0].Position > renamed[1].Position);
                if (removed[0].Id != "b" || removed[0].RequestId != "first-b")
                    throw new Exception("Removing a screen stopped another video.");
                renderer.Configure(options with
                {
                    DisplayScreens = new()
                    {
                        ["TEST"] = []
                    }
                });
                Until(s => s.Length == 0);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
        Console.WriteLine("Passed hardware-decoded simultaneous videos on one display, independent replacement, rename continuity, single-screen removal and zero screens.");
    }
}
