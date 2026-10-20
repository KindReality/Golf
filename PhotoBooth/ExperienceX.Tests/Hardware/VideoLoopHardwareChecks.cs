using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using PhotoBooth.Networking;
using PhotoBooth.Diagnostics;
using Forms = System.Windows.Forms;

internal static class VideoLoopHardwareChecks
{
    public static async Task Run(bool softwareVideoDecoding = false)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-video-loop-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                try
                {
                    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
                    var video = Environment.GetEnvironmentVariable("EXPERIENCEX_LOOP_TEST_VIDEO") ?? Path.Combine(root, "experiencex", "Media", "Video.mp4");
                    if (!File.Exists(video)) throw new FileNotFoundException("Provide EXPERIENCEX_LOOP_TEST_VIDEO for native looping checks.", video);
                    using var first = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    using var second = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    var commands = ((IPEndPoint)first.Client.LocalEndPoint!).Port; var events = ((IPEndPoint)second.Client.LocalEndPoint!).Port;
                    first.Dispose(); second.Dispose();
                    var display = Forms.Screen.PrimaryScreen!.DeviceName;
                    var options = new ExperienceOptions { DeveloperMode = true, DeviceName = "loop-client", SoftwareVideoDecoding = softwareVideoDecoding, LocalController = new() { Enabled = false },
                        VideoDirectory = Path.GetDirectoryName(video)!, UdpCommandsPort = commands, UdpEventsPort = events,
                        DisplayBackgrounds = Forms.Screen.AllScreens.ToDictionary(s => s.DeviceName, _ => new DisplayBackgroundOptions { IsTransparent = true }),
                        TransitionInSeconds = .5, TransitionOutSeconds = 3, DisplayScreens = new() { [display] = [new() { Id = "a", Name = "screen1" }] } };
                    var configuration = JsonSerializer.SerializeToNode(options)!;
                    configuration["Telemetry"] = JsonSerializer.SerializeToNode(new TelemetryOptions { Directory = Path.Combine(folder, "logs") });
                    var path = Path.Combine(folder, "appsettings.json"); await File.WriteAllTextAsync(path, configuration.ToJsonString());
                    Telemetry.Start("LoopTests", path);
                    await using var host = new ExperienceHost(dispatcher, path); host.Start();
                    var renderer = host.DisplayWindows.Single(w => w.DisplayName == display).Renderer!;
                    async Task<ScreenPlaybackSnapshot> Read() => (await renderer.SnapshotAsync()).Single(t => t.Id == "a");
                    async Task<ScreenPlaybackSnapshot> Until(Func<ScreenPlaybackSnapshot, bool> ready, int seconds = 12)
                    {
                        var clock = Stopwatch.StartNew();
                        while (clock.Elapsed.TotalSeconds < seconds)
                        { var state = await Read(); if (ready(state)) return state; await Task.Delay(25); }
                        throw new TimeoutException("Looping playback did not reach the expected state: " + await Read());
                    }
                    async Task Send(string source, string id, object body)
                    {
                        var sender = new BroadcastOptions { DeviceName = source, UdpCommandsPort = commands, UdpEventsPort = events, UdpSendAttempts = 1 };
                        await UdpBroadcastSender.SendAsync(BroadcastEnvelope.Create(sender, "command", "loop-client", body, id), sender);
                    }
                    object Play() => new { experience = "video", value = Path.GetFileName(video), screen = display + "/screen1", loop = true, mute = true };
                    object Stop(string id) => new { experience = "video", command = "stop", playbackRequestId = id };
                    await Send("controller-a", "original", Play());
                    await Until(s => s.RequestId == "original" && s.Position > .2 && s.Frames > 2 && s.Opacity > .9);
                    await Send("controller-a", "replacement", Play());
                    await Until(s => s.RequestId == "replacement" && s.Position > .2 && s.Opacity > .9);
                    await Send("controller-a", "stop-old", Stop("original"));
                    await Send("other-controller", "stop-wrong-source", Stop("replacement"));
                    var active = await Until(s => s.RequestId == "replacement" && s.Position > .6);
                    if (active.State == RendererState.FadingOut) throw new Exception("An old or foreign stop affected newer playback.");
                    await Send("controller-a", "stop-before-play", Stop("future")); await Task.Delay(150);
                    await Send("controller-a", "future", Play()); await Task.Delay(250);
                    if ((await Read()).RequestId != "replacement") throw new Exception("A reordered canceled play request replaced the active loop.");
                    var repeated = await Until(s => s.Loops >= 1 && s.RequestId == "replacement" && s.Opacity == 1, 80);
                    Console.WriteLine($"Native looping repeated with {repeated.Frames} transferred frames; surface opacity remained 1; software decoding={softwareVideoDecoding}.");
                    await Send("controller-a", "stop-current", Stop("replacement"));
                    var fading = await Until(s => s.State == RendererState.FadingOut && s.Opacity is > .2 and < .9);
                    if (fading.Frames <= repeated.Frames) throw new Exception("Video stopped decoding before becoming transparent.");
                    await Send("controller-a", "stop-current-again", Stop("replacement"));
                    var idle = await Until(s => s.RequestId is null && s.State == RendererState.Idle && s.Opacity == 0, 5);
                    Telemetry.Stop();
                    var reports = Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.AllDirectories)
                        .SelectMany(File.ReadLines).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                        .Where(e => e.GetProperty("Event").GetString() == "DirectXPlaybackPerformance").ToArray();
                    if (reports.Length == 0 || reports.Any(e => e.GetProperty("Data").GetProperty("MaxVideoTimestampGapMilliseconds").GetDouble() > 10000))
                        throw new Exception("Frame telemetry includes a no-frame sentinel timestamp or lacks playback reports.");
                    Console.WriteLine("Passed native frame telemetry across looping, replacement and stop without sentinel timestamps.");
                    Console.WriteLine("Passed real UDP loop/replacement, stale/foreign/reordered stops, decoding through the three-second stop fade and transparent completion.");
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    Telemetry.Stop();
                    foreach (var log in Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.AllDirectories))
                        foreach (var line in File.ReadLines(log).TakeLast(15)) Console.Error.WriteLine(line);
                    completion.TrySetException(ex);
                }
                finally { Telemetry.Stop(); Directory.Delete(folder, true); dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(105));
    }
}
