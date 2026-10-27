using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using NAudio.Wave;
using PhotoBooth.Diagnostics;
using PhotoBooth.Networking;
using Forms = System.Windows.Forms;

internal static class VideoPriorityHardwareChecks
{
    public static async Task Run()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousNetwork = Environment.GetEnvironmentVariable(LocalTestNetwork.Variable);
        Environment.SetEnvironmentVariable(LocalTestNetwork.Variable, "127.0.0.3");
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-priority-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder); var passed = false;
                try
                {
                    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
                    var fixture = Environment.GetEnvironmentVariable("EXPERIENCEX_PRIORITY_TEST_VIDEO") ?? Path.Combine(root, ".tools", "priority-fixture.mp4");
                    if (!File.Exists(fixture)) throw new FileNotFoundException("Provide a short (about two-second) EXPERIENCEX_PRIORITY_TEST_VIDEO for native priority checks.", fixture);
                    File.Copy(fixture, Path.Combine(folder, "Sample.mp4")); File.Copy(fixture, Path.Combine(folder, "Other.mp4"));
                    await File.WriteAllTextAsync(Path.Combine(folder, "Broken.mp4"), "Not a video");
                    using (var writer = new WaveFileWriter(Path.Combine(folder, "Silence.wav"), WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
                        writer.WriteSamples(new float[48000 * 2 * 90], 0, 48000 * 2 * 90);
                    var commands = FreeUdpPort(); var events = FreeUdpPort();
                    var displays = Forms.Screen.AllScreens.Select(screen => screen.DeviceName).ToArray();
                    var first = Forms.Screen.PrimaryScreen!.DeviceName; var second = displays.FirstOrDefault(display => display != first) ?? first;
                    var screens = displays.ToDictionary(display => display, _ => new List<ScreenOptions>());
                    screens[first].Add(new() { Id = "a", Name = "screen1" }); screens[second].Add(new() { Id = "b", Name = "screen2" });
                    var options = new ExperienceOptions
                    {
                        DeveloperMode = true, DeviceName = "priority-client", LocalController = new() { Enabled = false },
                        VideoDirectory = folder, AudioDirectory = folder, UdpCommandsPort = commands, UdpEventsPort = events, InventoryPort = FreeTcpPort(),
                        DisplayBackgrounds = displays.ToDictionary(display => display, _ => new DisplayBackgroundOptions { IsTransparent = true }),
                        DisplayScreens = screens, TransitionInSeconds = .05, TransitionOutSeconds = .1
                    };
                    var config = JsonSerializer.SerializeToNode(options)!;
                    config["Telemetry"] = JsonSerializer.SerializeToNode(new TelemetryOptions { Directory = Path.Combine(folder, "logs") });
                    var configPath = Path.Combine(folder, "appsettings.json"); await File.WriteAllTextAsync(configPath, config.ToJsonString());
                    Telemetry.Start("PriorityTests", configPath);
                    await using (var host = new ExperienceHost(dispatcher, configPath))
                    {
                        host.Start();
                        async Task<ScreenPlaybackSnapshot[]> Read()
                        {
                            var windows = host.DisplayWindows.Where(w => w.DisplayName == first || w.DisplayName == second).ToArray();
                            if (windows.Any(w => w.Renderer!.State == RendererState.Starting)) return [];
                            if (windows.Any(w => w.Renderer!.State == RendererState.Failed)) throw new Exception("A display renderer failed. See " + folder);
                            try { return (await Task.WhenAll(windows.Select(w => w.Renderer!.SnapshotAsync()))).SelectMany(s => s).Where(s => s.Id is "a" or "b").ToArray(); }
                            catch (TimeoutException) { return []; }
                        }
                        async Task<ScreenPlaybackSnapshot[]> Until(Func<ScreenPlaybackSnapshot[], bool> ready, int seconds = 15)
                        {
                            var clock = Stopwatch.StartNew();
                            while (clock.Elapsed.TotalSeconds < seconds) { var items = await Read(); if (items.Length == 2 && ready(items)) return items; await Task.Delay(20); }
                            throw new TimeoutException("Video priority state: " + JsonSerializer.Serialize(await Read()));
                        }
                        async Task Send(string id, object body, CommandPriority priority = CommandPriority.Medium)
                        {
                            var sender = new BroadcastOptions { DeviceName = "priority-controller", UdpCommandsPort = commands, UdpEventsPort = events, UdpSendAttempts = 1 };
                            await UdpBroadcastSender.SendAsync(BroadcastEnvelope.Create(sender, "command", options.DeviceName, body, id, priority: priority), sender);
                        }
                        object Play(string screen, string file = "Sample.mp4", bool loop = false) => new { experience = "video", value = file, screen, loop, mute = true };
                        object Stop(string id) => new { experience = "video", command = "stop", playbackRequestId = id };
                        ScreenPlaybackSnapshot A(ScreenPlaybackSnapshot[] items) => items.Single(i => i.Id == "a");
                        ScreenPlaybackSnapshot B(ScreenPlaybackSnapshot[] items) => items.Single(i => i.Id == "b");
                        var targetA = first + "/screen1"; var targetB = second + "/screen2";
                        var endpoints = AudioManager.ReadInventory();
                        var targetCount = AudioDeviceSelection.Resolve(new() { AudioDevice = "*" }, options, endpoints.Devices, endpoints.DefaultId).Devices.Length;
                        if (targetCount == 0) throw new Exception("Native priority checks require an audio output.");
                        await Send("low-audio", new { experience = "audio", value = "Silence.wav", audioDevice = "*", mute = true }, CommandPriority.Low);
                        await Until(_ => host.AudioPlaybackCount == targetCount);
                        await Send("high-audio", new { experience = "audio", value = "Silence.wav", audioDevice = "*", mute = true }, CommandPriority.High);
                        await Until(_ => host.AudioPlaybackCount == targetCount * 2);

                        await Send("background", Play("*", loop: true));
                        var start = await Until(i => i.All(s => s.RequestId == "background" && s.Frames > 4 && s.Opacity > .9));
                        await Send("repeat", Play("*", loop: true));
                        await Until(i => i.All(s => s.RequestId == "background" && s.Frames > start.Single(p => p.Id == s.Id).Frames && s.Opacity > .9));
                        await Send("foreground", Play(targetA, "Other.mp4"));
                        await Until(i => A(i).RequestId == "foreground" && A(i).Frames > 3 && B(i).RequestId == "background");
                        await Send("blocked", Play(targetA), CommandPriority.Low);
                        await Send("latest-background", Play(targetA, loop: true));
                        await Task.Delay(150);
                        Check(A(await Read()).RequestId == "foreground", "A lower-priority background interrupted the foreground video.");
                        await Until(i => A(i).RequestId == "latest-background" && A(i).Frames > 3 && B(i).RequestId == "background");
                        Console.WriteLine("PASS Foreground finishes; latest background resumes on its screen; another screen continues.");

                        var before = A(await Read());
                        await Send("same-one-shot", Play(targetA));
                        var continued = A(await Until(i => A(i).RequestId == "same-one-shot" && A(i).Frames > before.Frames));
                        Check(continued.Opacity > .9, "Same-video one-shot faded the existing loop.");
                        await Until(i => A(i).RequestId == "latest-background" && A(i).Loops > continued.Loops);
                        Console.WriteLine("PASS Same media is promoted without reopen, keeps looping, and releases priority at a natural wrap.");

                        await Send("high", Play(targetA, "Other.mp4"), CommandPriority.High);
                        await Until(i => A(i).RequestId == "high" && A(i).Frames > 3);
                        await Send("medium-blocked", Play(targetA));
                        await Send("other-screen", Play(targetB, "Other.mp4"));
                        await Until(i => A(i).RequestId == "high" && B(i).RequestId == "other-screen" && B(i).Frames > 2);
                        await Send("stop-background", Stop("latest-background"));
                        await Until(i => A(i).RequestId is null && B(i).RequestId == "background");
                        await Task.Delay(250); Check(A(await Read()).RequestId is null, "Stopped scene background returned after foreground completion.");
                        Console.WriteLine("PASS High blocks Medium only on that screen; scene cleanup prevents obsolete background resumption.");

                        await Send("once", Play(targetA));
                        var once = A(await Until(i => A(i).RequestId == "once" && A(i).Frames > 3));
                        await Send("same-once", Play(targetA));
                        await Until(i => A(i).RequestId == "same-once" && A(i).Frames > once.Frames && A(i).Opacity > .9);
                        await Send("promote-loop", Play(targetA, loop: true));
                        await Until(i => A(i).RequestId == "promote-loop" && A(i).Loops >= 1 && A(i).Frames > once.Frames);
                        await Send("stop-old", Stop("once"));
                        await Task.Delay(150); Check(A(await Read()).RequestId == "promote-loop", "A stale stop interrupted promoted playback.");
                        await Send("stop-new", Stop("promote-loop"));
                        await Until(i => A(i).RequestId is null);
                        await Send("broken", Play(targetA, "Broken.mp4"));
                        await Until(i => A(i).RequestId is null, 25);
                        await Send("after-error", Play(targetA, loop: true));
                        await Until(i => A(i).RequestId == "after-error" && A(i).Frames > 3);
                        Check(host.AudioPlaybackCount == targetCount * 2, "Video priority stopped mixed audio.");
                    }
                    Telemetry.Stop();
                    var lines = Directory.EnumerateFiles(Path.Combine(folder, "logs", "PriorityTests"), "*.jsonl").SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line)).ToArray();
                    try
                    {
                        bool Has(string name) => lines.Any(l => l.RootElement.GetProperty("Event").GetString() == name);
                        Check(Has("VideoRequestSkippedLowerPriority") && Has("VideoBackgroundResumed"), "Missing priority or background telemetry.");
                        Check(!lines.Any(l => l.RootElement.GetProperty("Event").GetString() == "VideoOpening" && l.RootElement.GetProperty("RequestId").GetString() is "same-one-shot" or "same-once" or "promote-loop"), "Continuing the same video reopened a decoder.");
                        Check(!lines.Any(l => l.RootElement.GetProperty("Event").GetString() == "VideoOpening" && l.RootElement.GetProperty("RequestId").GetString() == "repeat") && Has("VideoLoopAlreadyPlaying"),
                            "Repeated unchanged loops preserve their original playback ownership without reopening a decoder.");
                    }
                    finally { foreach (var line in lines) line.Dispose(); }
                    Console.WriteLine("PASS Native UDP per-screen priorities, natural completion/background resumption, same-file continuity, loop promotion, stop cleanup and audio mixing.");
                    passed = true; completion.SetResult();
                }
                catch (Exception ex) { completion.SetException(ex); }
                finally
                {
                    Telemetry.Stop(); if (passed) Directory.Delete(folder, true); else Console.WriteLine("Priority diagnostics: " + folder);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(100)); }
        finally { Environment.SetEnvironmentVariable(LocalTestNetwork.Variable, previousNetwork); }
    }
    private static int FreeUdpPort() { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)socket.Client.LocalEndPoint!).Port; }
    private static int FreeTcpPort() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return ((IPEndPoint)socket.LocalEndpoint).Port; }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
