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
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-priority-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                var passed = false;
                try
                {
                    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
                    var fixture = Environment.GetEnvironmentVariable("EXPERIENCEX_PRIORITY_TEST_VIDEO") ?? Path.Combine(root, ".tools", "screen-media-fixture.mp4");
                    if (!File.Exists(fixture)) throw new FileNotFoundException("Provide EXPERIENCEX_PRIORITY_TEST_VIDEO for native priority checks.", fixture);
                    var video = Path.Combine(folder, "Sample.mp4");
                    File.Copy(fixture, video);
                    await File.WriteAllTextAsync(Path.Combine(folder, "Broken.mp4"), "Not a video");
                    var audio = Path.Combine(folder, "Silence.wav");
                    using (var writer = new WaveFileWriter(audio, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
                        writer.WriteSamples(new float[48000 * 2 * 90], 0, 48000 * 2 * 90);
                    var commands = FreeUdpPort();
                    var events = FreeUdpPort();
                    var displays = Forms.Screen.AllScreens.Select(screen => screen.DeviceName).ToArray();
                    var first = Forms.Screen.PrimaryScreen!.DeviceName;
                    var second = displays.FirstOrDefault(display => display != first) ?? first;
                    var screens = displays.ToDictionary(display => display, _ => new List<ScreenOptions>());
                    screens[first].Add(new() { Id = "a", Name = "screen1" });
                    screens[second].Add(new() { Id = "b", Name = "screen2" });
                    var options = new ExperienceOptions
                    {
                        DeveloperMode = true, DeviceName = "priority-client", MediaDiscovery = new() { Enabled = false },
                        VideoDirectory = Path.GetDirectoryName(video)!, AudioDirectory = folder,
                        UdpCommandsPort = commands, UdpEventsPort = events, InventoryPort = FreeTcpPort(),
                        DisplayBackgrounds = displays.ToDictionary(display => display, _ => new DisplayBackgroundOptions { IsTransparent = true }),
                        DisplayScreens = screens, TransitionInSeconds = .05, TransitionOutSeconds = .1
                    };
                    var config = JsonSerializer.SerializeToNode(options)!;
                    config["Telemetry"] = JsonSerializer.SerializeToNode(new TelemetryOptions { Directory = Path.Combine(folder, "logs") });
                    var configPath = Path.Combine(folder, "appsettings.json");
                    await File.WriteAllTextAsync(configPath, config.ToJsonString());
                    Telemetry.Start("PriorityTests", configPath);
                    var targetCount = 0;
                    await using (var host = new ExperienceHost(dispatcher, configPath))
                    {
                        host.Start();
                        async Task<ScreenPlaybackSnapshot[]> Read()
                        {
                            var windows = host.DisplayWindows.Where(window => window.DisplayName == first || window.DisplayName == second).ToArray();
                            if (windows.Any(window => window.Renderer!.State == RendererState.Starting)) return [];
                            if (windows.Any(window => window.Renderer!.State == RendererState.Failed)) throw new Exception("A test display renderer failed. See " + folder);
                            ScreenPlaybackSnapshot[][] snapshots;
                            try { snapshots = await Task.WhenAll(windows.Select(window => window.Renderer!.SnapshotAsync())); }
                            catch (TimeoutException) { return []; } // Native SetSource/Load can temporarily occupy the worker.
                            return snapshots.SelectMany(items => items).Where(item => item.Id is "a" or "b").ToArray();
                        }
                        async Task<ScreenPlaybackSnapshot[]> Until(Func<ScreenPlaybackSnapshot[], bool> ready, int seconds = 15)
                        {
                            var clock = Stopwatch.StartNew();
                            while (clock.Elapsed.TotalSeconds < seconds)
                            {
                                var items = await Read();
                                if (items.Length == 2 && ready(items)) return items;
                                await Task.Delay(25);
                            }
                            throw new TimeoutException("Video priority did not reach expected state: " + JsonSerializer.Serialize(await Read()));
                        }
                        async Task Send(string id, object body, bool ambient)
                        {
                            var sender = new BroadcastOptions { DeviceName = "priority-controller", UdpCommandsPort = commands, UdpEventsPort = events, UdpSendAttempts = 1 };
                            var context = new CommandContext { Scene = "test", Playlist = ambient ? "ambient" : "normal", Item = id, IsAmbient = ambient };
                            await UdpBroadcastSender.SendAsync(BroadcastEnvelope.Create(sender, "command", options.DeviceName, body, id, context: context), sender);
                        }
                        object Play(string screen, bool loop = true) => new { experience = "video", value = Path.GetFileName(video), screen, loop, mute = true };
                        object Stop(string id) => new { experience = "video", command = "stop", playbackRequestId = id };
                        var endpoints = AudioManager.ReadInventory();
                        targetCount = AudioDeviceSelection.Resolve(new() { AudioDevice = "*" }, options, endpoints.Devices, endpoints.DefaultId).Devices.Length;
                        if (targetCount == 0) throw new Exception("Native priority checks require at least one audio output.");
                        await Send("ambient-audio", new { experience = "audio", value = "Silence.wav", audioDevice = "*", mute = true }, true);
                        await Until(_ => host.AudioPlaybackCount == targetCount);
                        await Send("normal-audio", new { experience = "audio", value = "Silence.wav", audioDevice = "*", mute = true }, false);
                        await Until(_ => host.AudioPlaybackCount == targetCount * 2);
                        await Send("ambient-initial", Play("*"), true);
                        var initial = await Until(items => items.All(item => item.RequestId == "ambient-initial" && item.Frames > 2 && item.Opacity > .9));
                        await Send("ambient-repeat", Play("*"), true);
                        await Until(items => items.All(item => item.RequestId == "ambient-repeat" && item.Frames > initial.Single(previous => previous.Id == item.Id).Frames && item.Opacity > .9));

                        await Send("normal-a", Play(first + "/screen1"), false);
                        await Until(items => items.Single(item => item.Id == "a").RequestId == "normal-a" && items.Single(item => item.Id == "a").Frames > 2 && items.Single(item => item.Id == "b").RequestId is null);
                        var promoted = (await Read()).Single(item => item.Id == "a");
                        Check(promoted.Frames > initial.Single(item => item.Id == "a").Frames && promoted.Opacity > .9, "Promoting an identical ambient loop restarted it or faded it.");
                        await Send("normal-a-repeat", Play(first + "/screen1"), false);
                        await Until(items => items.Single(item => item.Id == "a").RequestId == "normal-a-repeat" && items.Single(item => item.Id == "a").Frames > promoted.Frames && items.Single(item => item.Id == "a").Opacity > .9);
                        await Send("stop-old-a", Stop("normal-a"), false);
                        await Send("ambient-blocked", Play(second + "/screen2"), true);
                        await Task.Delay(250);
                        var protectedState = await Read();
                        Check(protectedState.Single(item => item.Id == "a").RequestId == "normal-a-repeat" && protectedState.Single(item => item.Id == "b").RequestId is null, "Ambient video or a superseded stop interrupted normal playback, or ambient started on another display.");
                        await Send("normal-b", Play(second + "/screen2"), false);
                        await Until(items => items.Single(item => item.Id == "a").RequestId == "normal-a-repeat" && items.Single(item => item.Id == "b").RequestId == "normal-b" && items.All(item => item.Frames > 2));
                        await Send("stop-a", Stop("normal-a-repeat"), false);
                        await Until(items => items.Single(item => item.Id == "a").RequestId is null);
                        await Send("ambient-still-blocked", Play(first + "/screen1"), true);
                        await Task.Delay(250);
                        Check((await Read()).Single(item => item.Id == "a").RequestId is null, "Ambient resumed before the last normal video ended.");
                        await Send("stop-b", Stop("normal-b"), false);
                        await Until(items => items.All(item => item.RequestId is null));
                        await Send("ambient-after-stop", Play("*"), true);
                        await Until(items => items.All(item => item.RequestId == "ambient-after-stop" && item.Frames > 2));

                        await Send("normal-once", Play(first + "/screen1", false), false);
                        await Until(items => items.Single(item => item.Id == "a").RequestId == "normal-once" && items.Single(item => item.Id == "a").Frames > 2);
                        await Until(items => items.All(item => item.RequestId is null), 60);
                        await Send("ambient-after-eof", Play("*"), true);
                        await Until(items => items.All(item => item.RequestId == "ambient-after-eof" && item.Frames > 2));
                        await Send("normal-invalid", new { experience = "video", value = "Broken.mp4", screen = first + "/screen1", mute = true }, false);
                        await Until(items => items.All(item => item.RequestId is null), 25);
                        await Send("ambient-after-error", Play("*"), true);
                        await Until(items => items.All(item => item.RequestId == "ambient-after-error" && item.Frames > 2));
                        Check(host.AudioPlaybackCount == targetCount * 2, "Video priority stopped ambient or normal audio.");
                    }
                    Telemetry.Stop();
                    var logs = Directory.EnumerateFiles(Path.Combine(folder, "logs", "PriorityTests"), "*.jsonl").SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line)).ToArray();
                    try
                    {
                        Check(logs.Any(log => log.RootElement.GetProperty("Event").GetString() == "AmbientVideoRequestSkipped"), "Missing ambient suppression telemetry.");
                        Check(logs.Any(log => log.RootElement.GetProperty("Event").GetString() == "PlaybackStopped" && log.RootElement.GetProperty("Data").GetProperty("Reason").GetString() == "NormalVideoPriority"), "Missing ambient interruption telemetry.");
                        Check(!logs.Any(log => log.RootElement.GetProperty("Event").GetString() == "VideoOpening" && log.RootElement.GetProperty("RequestId").GetString() is "ambient-repeat" or "normal-a" or "normal-a-repeat"), "An identical looping request reopened the decoder.");
                    }
                    finally { foreach (var log in logs) log.Dispose(); }
                    Console.WriteLine($"Passed native UDP video priority across {displays.Length} displays, repeated-loop continuity/promotion, overlapping normal videos, stop/EOF recovery, and uninterrupted ambient/normal audio on {targetCount} outputs.");
                    passed = true;
                    completion.SetResult();
                }
                catch (Exception ex) { completion.SetException(ex); }
                finally
                {
                    Telemetry.Stop();
                    if (passed) Directory.Delete(folder, true);
                    else Console.WriteLine("Priority test diagnostics retained: " + folder);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(100));
    }
    private static int FreeUdpPort() { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)socket.Client.LocalEndPoint!).Port; }
    private static int FreeTcpPort() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return ((IPEndPoint)socket.LocalEndpoint).Port; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
