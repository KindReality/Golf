using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using PhotoBooth.Diagnostics;
using PhotoBooth.Networking;
using Forms = System.Windows.Forms;

internal static class VideoFadeHardwareChecks
{
    internal static async Task Run()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = Environment.GetEnvironmentVariable(LocalTestNetwork.Variable);
        Environment.SetEnvironmentVariable(LocalTestNetwork.Variable, "127.0.0.3");
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "VideoFade-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
                try
                {
                    var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.tools/priority-fixture.mp4"));
                    File.Copy(fixture, Path.Combine(root, "Sample.mp4"));
                    File.Copy(fixture, Path.Combine(root, "Other.mp4"));
                    var display = Forms.Screen.PrimaryScreen!.DeviceName;
                    var options = new ExperienceOptions
                    {
                        DeveloperMode = true, DeviceName = "fade-client", LocalController = new() { Enabled = false },
                        VideoDirectory = root, UdpCommandsPort = FreeUdp(), UdpEventsPort = FreeUdp(), InventoryPort = FreeTcp(),
                        TransitionInSeconds = .05, TransitionOutSeconds = .1,
                        DisplayBackgrounds = Forms.Screen.AllScreens.ToDictionary(d => d.DeviceName, _ => new DisplayBackgroundOptions { IsTransparent = false, BackgroundColor = "#FFFFFF" }),
                        DisplayScreens = new() { [display] = [
                            new() { Id = "a", Name = "screen1", Roles = "Front", Points = new() { Enabled = true, TopRight = new(.5,0), BottomRight = new(.5,1) } },
                            new() { Id = "b", Name = "Bound", Roles = "Rear", MediaFile = "Sample.mp4", Points = new() { Enabled = true, TopLeft = new(.5,0), BottomLeft = new(.5,1) } },
                            new() { Id = "c", Name = "Empty", Roles = "Empty" }] }
                    };
                    var config = JsonSerializer.SerializeToNode(options)!;
                    config["Telemetry"] = JsonSerializer.SerializeToNode(new TelemetryOptions { Directory = Path.Combine(root, "logs") });
                    var path = Path.Combine(root, "appsettings.json"); await File.WriteAllTextAsync(path, config.ToJsonString());
                    Telemetry.Start("ExperienceX-FadeTest", path);
                    await using (var host = new ExperienceHost(dispatcher, path))
                    {
                        host.Start();
                        var renderer = host.DisplayWindows.Single(w => w.DisplayName == display).Renderer!;
                        async Task<ScreenPlaybackSnapshot[]> Until(Func<ScreenPlaybackSnapshot[], bool> ready)
                        {
                            var clock = Stopwatch.StartNew();
                            while (clock.Elapsed.TotalSeconds < 10)
                            { var states = await renderer.SnapshotAsync(); if (states.Length == 3 && ready(states)) return states; await Task.Delay(20); }
                            throw new TimeoutException("Fade playback state did not arrive.");
                        }
                        async Task Send(string id, object body, CommandPriority priority = CommandPriority.Medium)
                        {
                            var sender = new BroadcastOptions { DeviceName = "fade-controller", UdpCommandsPort = options.UdpCommandsPort, UdpEventsPort = options.UdpEventsPort, UdpSendAttempts = 1 };
                            await UdpBroadcastSender.SendAsync(BroadcastEnvelope.Create(sender, "command", options.DeviceName, body, id, priority: priority), sender);
                        }
                        object Play(bool loop = true, double playbackSpeed = 1) => new { experience = "video", value = "Sample.mp4", screen = display + "/screen1", mute = true, loop, playbackSpeed };
                        object Fade(string command, double duration, string screen = "*") => new { experience = "video", command, durationSeconds = duration, screen };
                        await Send("playing", Play());
                        await Until(s => s[0].Frames > 3 && s[0].Opacity > .95 && s[1].Frames > 3);
                        await Send("out", Fade("fadeOut", .5, display + "/screen1"));
                        var middle = await Until(s => s[0].ScreenOpacity is > .1 and < .9);
                        var frames = middle[0].Frames;
                        await Send("reverse", Fade("fadeIn", .25, display + "/screen1"));
                        var visible = await Until(s => s[0].ScreenOpacity == 1 && !s[0].ScreenFading);
                        Check(visible[0].RequestId == "playing" && visible[0].Frames > frames && visible[1].ScreenOpacity == 1, "UDP fades reverse smoothly without restarting playback or affecting untargeted screens.");
                        await Send("hide-all", Fade("fadeOut", .15));
                        var hidden = await Until(s => s.All(x => x.ScreenOpacity == 0 && !x.ScreenFading));
                        var boundRequest = hidden[1].RequestId;
                        await Task.Delay(150);
                        var advancing = await renderer.SnapshotAsync();
                        Check(advancing[0].Opacity == 0 && advancing[1].Opacity == 0 && advancing[0].Frames > hidden[0].Frames && advancing[1].Frames > hidden[1].Frames && advancing[1].NameLocked,
                            "Wildcard fades hide both commanded and media-bound video while decoding continues.");
                        await Send("new-hidden-play", Play(playbackSpeed: 1.25));
                        await Until(s => s[0].RequestId == "new-hidden-play");
                        Check((await renderer.SnapshotAsync())[0].ScreenOpacity == 0, "Screen transparency persists across later video requests until fadeIn.");
                        await Send("front-only", new { experience = "video", command = "fadeIn", durationSeconds = 0, roles = "Front" });
                        var front = await Until(s => s[0].ScreenOpacity == 1 && s[1].ScreenOpacity == 0);
                        Check(front[1].RequestId == boundRequest && front[2].State == RendererState.Idle && ExperienceOptions.Parse(await File.ReadAllTextAsync(path)).DisplayBackgrounds[display].BackgroundColor == "#FFFFFF",
                            "Role targeting and zero-duration fades preserve bound playback, empty screens and the configured background.");
                        await Send("high-play", new { experience = "video", value = "Other.mp4", screen = display + "/screen1", mute = true }, CommandPriority.High);
                        await Until(s => s[0].RequestId == "high-play" && s[0].Opacity > .9);
                        await Send("blocked-fade", Fade("fadeOut", 0, display + "/screen1"), CommandPriority.Low);
                        await Task.Delay(100);
                        Check((await renderer.SnapshotAsync())[0].ScreenOpacity == 1, "Lower-priority fade commands cannot hide higher-priority playback.");
                        await Send("high-fade", Fade("fadeOut", .1, display + "/screen1"), CommandPriority.High);
                        await Until(s => s[0].ScreenOpacity == 0);
                        Check(true, "An equal-priority fade command is accepted.");
                    }
                    Telemetry.Stop();
                    var logs = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).SelectMany(File.ReadLines).ToArray();
                    Check(logs.Any(line => line.Contains("PlaybackPriorityEventSent") && line.Contains("blocked-fade")) && logs.Any(line => line.Contains("VideoScreenFadeCompleted")),
                        "Fade completion and priority rejection are logged and the conflict event is announced.");
                    done.TrySetResult();
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { Telemetry.Stop(); Directory.Delete(root, true); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
        finally { Environment.SetEnvironmentVariable(LocalTestNetwork.Variable, previous); }
    }
    private static int FreeUdp() { using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)client.Client.LocalEndPoint!).Port; }
    private static int FreeTcp() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
