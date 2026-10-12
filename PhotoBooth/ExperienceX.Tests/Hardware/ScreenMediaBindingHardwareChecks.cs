using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using PhotoBooth.Diagnostics;
using PhotoBooth.Networking;
using Forms = System.Windows.Forms;

internal static class ScreenMediaBindingHardwareChecks
{
    public static async Task Run()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-binding-hardware-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                try
                {
                    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
                    var fixture = Environment.GetEnvironmentVariable("EXPERIENCEX_BOUND_TEST_VIDEO") ?? Path.Combine(root, "experiencex", "Media", "ping.mp4");
                    if (!File.Exists(fixture)) throw new FileNotFoundException("Provide EXPERIENCEX_BOUND_TEST_VIDEO for media binding checks.", fixture);
                    var media = Path.Combine(folder, "Media"); Directory.CreateDirectory(media);
                    File.Copy(fixture, Path.Combine(media, "Eye.mp4")); File.Copy(fixture, Path.Combine(media, "Other.mp4"));
                    void Image(string name, Color color)
                    {
                        using var bitmap = new Bitmap(32, 32);
                        using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.Clear(color);
                        bitmap.Save(Path.Combine(media, name), ImageFormat.Png);
                    }
                    Image("Skull.png", Color.FromArgb(128, 255, 0, 0));
                    using var commandsSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    using var eventsSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    int commands = ((IPEndPoint)commandsSocket.Client.LocalEndPoint!).Port, events = ((IPEndPoint)eventsSocket.Client.LocalEndPoint!).Port;
                    commandsSocket.Dispose(); eventsSocket.Dispose();
                    var display = Forms.Screen.PrimaryScreen!.DeviceName;
                    PointOptions Region(double left, double right) => new() { Enabled = true, TopLeft = new(left, 0), TopRight = new(right, 0), BottomLeft = new(left, 1), BottomRight = new(right, 1) };
                    var options = new ExperienceOptions
                    {
                        DeviceName = "binding-client", DeveloperMode = true, MediaDiscovery = new() { Enabled = false },
                        VideoDirectory = media, UdpCommandsPort = commands, UdpEventsPort = events, UdpSendAttempts = 1,
                        TransitionInSeconds = .05, TransitionOutSeconds = .1,
                        DisplayBackgrounds = Forms.Screen.AllScreens.ToDictionary(s => s.DeviceName, _ => new DisplayBackgroundOptions { IsTransparent = true }),
                        DisplayScreens = Forms.Screen.AllScreens.ToDictionary(s => s.DeviceName, s => s.DeviceName == display ? new List<ScreenOptions>
                        {
                            new() { Id = "video", Name = "Eye", Points = Region(0, .25) },
                            new() { Id = "image", Name = "Skull", Points = Region(.25, .5) },
                            new() { Id = "free", Name = "Manual", Points = Region(.5, .75) },
                            new() { Id = "late", Name = "Late", Points = Region(.75, 1) },
                            new() { Id = "mat", Name = "Other", Type = "mat", Points = Region(0, .02) }
                        } : [])
                    };
                    var path = Path.Combine(folder, "appsettings.json");
                    var node = JsonSerializer.SerializeToNode(options)!;
                    node["Telemetry"] = JsonSerializer.SerializeToNode(new TelemetryOptions { Directory = Path.Combine(folder, "logs") });
                    await File.WriteAllTextAsync(path, node.ToJsonString()); Telemetry.Start("BindingTests", path);
                    await using (var host = new ExperienceHost(dispatcher, path))
                    {
                        host.Start();
                        var renderer = host.DisplayWindows.Single(w => w.DisplayName == display).Renderer!;
                        async Task<ScreenPlaybackSnapshot[]> Until(Func<ScreenPlaybackSnapshot[], bool> ready, int seconds = 18)
                        {
                            var clock = Stopwatch.StartNew(); ScreenPlaybackSnapshot[] last = [];
                            while (clock.Elapsed.TotalSeconds < seconds)
                            {
                                last = await renderer.SnapshotAsync(); if (ready(last)) return last; await Task.Delay(30);
                            }
                            throw new TimeoutException("Media binding state did not arrive: " + JsonSerializer.Serialize(last));
                        }
                        ScreenPlaybackSnapshot Track(ScreenPlaybackSnapshot[] tracks, string id) => tracks.Single(t => t.Id == id);
                        async Task Send(string id, object body, string source = "test-controller")
                        {
                            var sender = new BroadcastOptions { DeviceName = source, UdpCommandsPort = commands, UdpEventsPort = events, UdpSendAttempts = 1 };
                            await UdpBroadcastSender.SendAsync(BroadcastEnvelope.Create(sender, "command", "*", body, id), sender);
                        }
                        var started = await Until(t => Track(t, "video").NameLocked && Track(t, "video").Frames > 2 && Track(t, "video").Opacity > .9 &&
                            Track(t, "image").NameLocked && Track(t, "image").Frames == 1 && Track(t, "image").Opacity == 1);
                        var videoId = Track(started, "video").RequestId; var imageId = Track(started, "image").RequestId;
                        if (Track(started, "mat").NameLocked || Track(started, "mat").RequestId is not null) throw new Exception("A mat was bound to media.");
                        await Send("broadcast", new { experience = "video", value = "Other.mp4", screen = "*", loop = true, mute = true });
                        var broadcast = await Until(t => Track(t, "free").RequestId == "broadcast" && Track(t, "free").Frames > 2);
                        if (Track(broadcast, "video").RequestId != videoId || Track(broadcast, "image").RequestId != imageId) throw new Exception("A broadcast replaced locked media.");
                        await Send("missing-target", new { experience = "video", value = "Missing.mp4", screen = display + "/Eye" });
                        await Send("stop-bound-video", new { experience = "video", command = "stop", playbackRequestId = videoId }, "binding-client");
                        await Send("stop-bound-image", new { experience = "video", command = "stop", playbackRequestId = imageId }, "binding-client");
                        renderer.Play(Path.Combine(media, "Other.mp4"), new() { Experience = "video", Value = "Other.mp4", RequestId = "direct-command", ScreenId = "video", Mute = true });
                        var repeated = await Until(t => Track(t, "video").Loops >= 1 && Track(t, "video").Opacity == 1, 35);
                        if (Track(repeated, "video").RequestId != videoId || Track(repeated, "image").RequestId != imageId) throw new Exception("A targeted play or stop bypassed the binding lock.");
                        Console.WriteLine("Passed startup auto-play, image hold, continuous video looping, wildcard/direct/stop protection and normal-screen commands.");

                        async Task Rename(string name)
                        {
                            var current = ExperienceOptions.Parse(await File.ReadAllTextAsync(path));
                            current.DisplayScreens[display] = current.DisplayScreens[display].Select(s => s.Id == "video" ? s with { Name = name } : s).ToList();
                            var replacement = path + ".test-tmp";
                            await File.WriteAllTextAsync(replacement, JsonSerializer.Serialize(current));
                            File.Move(replacement, path, true);
                        }
                        await Rename("Renamed");
                        await Until(t => Track(t, "video").Name == "Renamed" && !Track(t, "video").NameLocked && Track(t, "video").RequestId is null);
                        await Send("unlocked-command", new { experience = "video", value = "Other.mp4", screen = display + "/Renamed", loop = true, mute = true });
                        await Until(t => Track(t, "video").RequestId == "unlocked-command" && Track(t, "video").Frames > 2);
                        await Rename("Eye");
                        await Until(t => Track(t, "video").NameLocked && Track(t, "video").Frames > 2 && Track(t, "video").RequestId != "unlocked-command");
                        Image("Late.png", Color.Green);
                        await Until(t => Track(t, "late").NameLocked && Track(t, "late").BoundPath == Path.Combine(media, "Late.png") && Track(t, "late").Opacity == 1);
                        Image("Skull.png", Color.Blue);
                        await Until(t => Track(t, "image").NameLocked && Track(t, "image").RequestId != imageId && Track(t, "image").Opacity == 1);
                        Image("Eye.png", Color.Red);
                        await Until(t => Track(t, "video").NameLocked && Track(t, "video").BoundPath is null && Track(t, "video").RequestId is null);
                        await Send("ambiguous-command", new { experience = "video", value = "Other.mp4", screen = display + "/Eye" });
                        await Task.Delay(200);
                        if ((await renderer.SnapshotAsync()).Single(t => t.Id == "video").RequestId is not null) throw new Exception("An ambiguous binding accepted a command.");
                        File.Delete(Path.Combine(media, "Eye.png"));
                        await Until(t => Track(t, "video").NameLocked && Track(t, "video").BoundPath == Path.Combine(media, "Eye.mp4") && Track(t, "video").Frames > 2);
                        Console.WriteLine("Passed live rename/unlock/rebind, new local media, image replacement and ambiguity protection/recovery.");
                        renderer.SetDisplayAvailable(false);
                        await Until(t => t.All(s => s.RequestId is null));
                        await Task.Delay(1200);
                        if ((await renderer.SnapshotAsync()).Any(s => s.RequestId is not null)) throw new Exception("Unavailable display restarted bound media.");
                        renderer.SetDisplayAvailable(true);
                        await Until(t => Track(t, "video").Frames > 2 && Track(t, "video").NameLocked && Track(t, "image").Opacity == 1);
                        Console.WriteLine("Passed display-unavailable pause and automatic bound-media restoration.");
                    }
                    // A static, media-bound screen must survive recovery even though the new worker
                    // can already be Playing before the window's one-second ready check.
                    int inject = 0, presents = 0;
                    var imageOptions = options with
                    {
                        DisplayScreens = new() { [display] = [new() { Id = "recovery", Name = "Skull" }] }
                    };
                    using (var window = new MainWindow(display, imageOptions, () =>
                    {
                        Interlocked.Increment(ref presents);
                        if (Interlocked.Exchange(ref inject, 0) == 1)
                            throw new COMException("Injected bound-image device failure", unchecked((int)0x887A0005));
                    }))
                    {
                        window.UpdateMediaCatalog(ScreenMediaCatalog.Read(media));
                        window.Start();
                        async Task Wait(Func<Task<bool>> ready)
                        {
                            var elapsed = Stopwatch.StartNew();
                            while (elapsed.Elapsed.TotalSeconds < 15)
                            { if (await ready()) return; await Task.Delay(50); }
                            throw new TimeoutException("Bound-image recovery did not finish.");
                        }
                        try
                        {
                            await Wait(async () => (await window.Renderer!.SnapshotAsync()).Single().Opacity == 1);
                            var settled = Volatile.Read(ref presents);
                            await Task.Delay(350);
                            if (Volatile.Read(ref presents) != settled) throw new Exception("A static image continuously rendered after its fade completed.");
                            Volatile.Write(ref inject, 1); window.UpdateOptions(imageOptions);
                            await Wait(() => Task.FromResult(window.State == RendererState.Failed));
                            window.RefreshDisplay();
                            if (window.Visible) throw new Exception("A failed media-bound window remained visible.");
                            await Wait(async () =>
                            {
                                window.RefreshDisplay();
                                if (!window.Visible || window.State == RendererState.Failed) return false;
                                var state = (await window.Renderer!.SnapshotAsync()).Single();
                                return state.NameLocked && state.Opacity == 1 && state.BoundPath == Path.Combine(media, "Skull.png");
                            });
                            Console.WriteLine("Passed idle-image render efficiency and bound-media restoration after graphics-device failure.");
                        }
                        finally { await window.ShutdownWindowAsync(); }
                    }
                    Telemetry.Stop();
                    var eventsLogged = Directory.EnumerateFiles(Path.Combine(folder, "logs"), "*.jsonl", SearchOption.AllDirectories).SelectMany(File.ReadLines)
                        .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
                    if (!eventsLogged.Any(e => e.GetProperty("Event").GetString() == "VideoCommandIgnoredScreenLocked") ||
                        eventsLogged.Any(e => e.GetProperty("Event").GetString() is "ScreenRenderingFailed" or "ScreenMediaPlaybackFailed") ||
                        eventsLogged.Count(e => e.GetProperty("Event").GetString() == "RendererFailed") != 1)
                        throw new Exception("Screen binding telemetry is missing or playback failed.");
                    completion.TrySetResult();
                }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally
                {
                    Telemetry.Stop(); Directory.Delete(folder, true);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(105)); }
        finally
        {
            // Do not let process exit race WPF's dispatcher/native-hook cleanup.
            if (!thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("The screen-binding test dispatcher did not stop.");
        }
    }
}
