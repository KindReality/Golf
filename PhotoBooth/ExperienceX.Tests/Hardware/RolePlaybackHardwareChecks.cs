using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using ExperienceX;
using NAudio.Wave;
using PhotoBooth.Networking;
using Forms = System.Windows.Forms;

internal static class RolePlaybackHardwareChecks
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
                var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-role-playback-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                var passed = false;
                try
                {
                    var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
                    var fixture = Environment.GetEnvironmentVariable("EXPERIENCEX_PRIORITY_TEST_VIDEO") ?? Path.Combine(workspace, ".tools", "screen-media-fixture.mp4");
                    File.Copy(fixture, Path.Combine(folder, "Sample.mp4"));
                    using (var writer = new WaveFileWriter(Path.Combine(folder, "Sound.wav"), WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
                        writer.WriteSamples(new float[48000 * 2 * 60], 0, 48000 * 2 * 60);
                    var devices = AudioManager.ReadInventory().Devices;
                    if (devices.Length < 2) throw new Exception("Role playback checks require two audio outputs.");
                    var display = Forms.Screen.PrimaryScreen!.DeviceName;
                    var displays = Forms.Screen.AllScreens.Select(screen => screen.DeviceName).ToArray();
                    var commands = FreeUdpPort(); var events = FreeUdpPort();
                    ExperienceOptions Options(string device, bool first) => new()
                    {
                        DeviceName = device, DeveloperMode = true, LocalController = new() { Enabled = false },
                        VideoDirectory = folder, AudioDirectory = folder, UdpCommandsPort = commands, UdpEventsPort = events, UdpSendAttempts = 1, InventoryPort = FreeTcpPort(),
                        TransitionInSeconds = .05, TransitionOutSeconds = .1,
                        DisplayBackgrounds = displays.ToDictionary(name => name, _ => new DisplayBackgroundOptions { IsTransparent = true }),
                        DisplayScreens = displays.ToDictionary(name => name, name => name == display ? new List<ScreenOptions>
                        {
                            new() { Id = "a", Name = "screen1", Roles = "Scary, Entry" },
                            new() { Id = "b", Name = "screen2", Roles = first ? "Other" : "" },
                            new() { Id = "locked", Name = "Sample", Roles = "Scary" },
                            new() { Id = "mat", Name = "mask", Type = "mat", Roles = "Scary", Points = new() { Enabled = true,
                                TopLeft = new(.9,.9), TopRight = new(1,.9), BottomLeft = new(.9,1), BottomRight = new(1,1) } }
                        } : []),
                        VirtualAudioDevices = first ? new() { ["Room"] = devices.Take(2).Select(output => output.Id).ToArray() } : new(),
                        AudioDevices = first ? new()
                        {
                            ["Room"] = new() { Roles = "Scary" },
                            [devices[0].Id] = new() { Name = devices[0].Name, Roles = "Scary, Entry" }
                        } : new() { [devices[^1].Id] = new() { Name = devices[^1].Name, Roles = "Scary" } }
                    };
                    var firstOptions = Options("role-a", true); var secondOptions = Options("role-b", false);
                    var firstPath = Path.Combine(folder, "first.json"); var secondPath = Path.Combine(folder, "second.json");
                    await File.WriteAllTextAsync(firstPath, JsonSerializer.Serialize(firstOptions));
                    await File.WriteAllTextAsync(secondPath, JsonSerializer.Serialize(secondOptions));
                    await using (var first = new ExperienceHost(dispatcher, firstPath))
                    await using (var second = new ExperienceHost(dispatcher, secondPath))
                    {
                        first.Start(); second.Start();
                        var rendererA = first.DisplayWindows.Single(window => window.DisplayName == display).Renderer!;
                        var rendererB = second.DisplayWindows.Single(window => window.DisplayName == display).Renderer!;
                        async Task<(ScreenPlaybackSnapshot[] First, ScreenPlaybackSnapshot[] Second)> Read()
                        {
                            if (rendererA.State == RendererState.Starting || rendererB.State == RendererState.Starting) return ([], []);
                            try { return (await rendererA.SnapshotAsync(), await rendererB.SnapshotAsync()); }
                            catch (TimeoutException) { return ([], []); }
                        }
                        async Task Until(Func<ScreenPlaybackSnapshot[], ScreenPlaybackSnapshot[], bool> ready)
                        {
                            var clock = Stopwatch.StartNew();
                            while (clock.Elapsed.TotalSeconds < 20)
                            {
                                var snapshot = await Read();
                                if (snapshot.First.Length == 4 && snapshot.Second.Length == 4 && ready(snapshot.First, snapshot.Second)) return;
                                await Task.Delay(25);
                            }
                            throw new TimeoutException("Role playback state not reached: " + JsonSerializer.Serialize(await Read()));
                        }
                        async Task Send(string id, string experience, string roles, string device = "*")
                        {
                            var sender = new BroadcastOptions { DeviceName = "role-test-controller", UdpCommandsPort = commands, UdpEventsPort = events, UdpSendAttempts = 1 };
                            var body = new { experience, value = experience == "video" ? "Sample.mp4" : "Sound.wav", roles, loop = experience == "video", mute = true, volume = 35 };
                            await UdpBroadcastSender.SendAsync(BroadcastEnvelope.Create(sender, "command", device, body, id), sender);
                        }
                        await Until((a, b) => a.Single(item => item.Id == "locked").Frames > 2 && b.Single(item => item.Id == "locked").Frames > 2);
                        await Until((_, _) => ExperienceOptions.Parse(File.ReadAllText(firstPath)).AudioDevices.Keys.Contains(devices[1].Id));
                        await Send("role-video", "video", "scary, entry");
                        await Until((a, b) => a.Single(item => item.Id == "a").RequestId == "role-video" && b.Single(item => item.Id == "a").RequestId == "role-video" && a.Single(item => item.Id == "a").Frames > 2 && b.Single(item => item.Id == "a").Frames > 2);
                        var before = await Read();
                        Check(before.First.Concat(before.Second).Where(item => item.Id is "b" or "mat").All(item => item.RequestId is null), "Unmatched screens or mats received role video.");
                        Check(before.First.Concat(before.Second).Where(item => item.Id == "locked").All(item => item.NameLocked && item.RequestId != "role-video"), "Role video replaced name-bound media.");
                        await Send("role-audio", "audio", "SCARY, Entry");
                        await Until((_, _) => first.AudioPlaybackCount == 2 && second.AudioPlaybackCount == 1);
                        await Send("role-audio-scoped", "audio", "Scary", "role-a");
                        await Until((_, _) => first.AudioPlaybackCount == 4 && second.AudioPlaybackCount == 1);

                        var root = JsonNode.Parse(await File.ReadAllTextAsync(firstPath))!;
                        root["DisplayScreens"]![display]![1]!["Roles"] = "Scary";
                        await File.WriteAllTextAsync(firstPath, root.ToJsonString());
                        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
                        await Until((_, _) => first.DisplayWindows.Single(window => window.DisplayName == display).ScreenDefinition("b")?.Roles == "Scary");
                        var inventory = JsonSerializer.Deserialize<ExperienceInventory>(await client.GetStringAsync($"http://127.0.0.1:{firstOptions.InventoryPort}/inventory"), ExperienceInventory.Json)!;
                        Check(inventory.Screens.Single(screen => screen.Id == "b").Roles == "Scary" && RoleList.Matches(inventory.Speakers.Single(output => output.Id == devices[1].Id).Roles, "Scary"), "Updated screen and virtual audio roles were not advertised.");
                        await Send("role-video-updated", "video", "Scary");
                        await Until((a, b) => a.Where(item => item.Id is "a" or "b").All(item => item.RequestId == "role-video-updated" && item.Frames > 2) && b.Single(item => item.Id == "a").RequestId == "role-video-updated");
                        Check(first.AudioPlaybackCount == 4 && second.AudioPlaybackCount == 1, "Role video interrupted audio.");
                        await Send("role-video-scoped", "video", "Scary", "role-a");
                        await Until((a, b) => a.Where(item => item.Id is "a" or "b").All(item => item.RequestId == "role-video-scoped") && b.Single(item => item.Id == "a").RequestId == "role-video-updated");
                        await Send("role-absent", "video", "MissingRole");
                        await Task.Delay(200);
                        var after = await Read();
                        Check(after.First.Single(item => item.Id == "a").RequestId == "role-video-scoped" && after.Second.Single(item => item.Id == "a").RequestId == "role-video-updated", "An unmatched role fell back to default playback.");
                    }
                    passed = true;
                    Console.WriteLine("Passed real UDP role playback to two Experience instances, video/audio capability filtering, once-per-output matching, scoped devices, live edits/discovery, locked-screen protection and uninterrupted audio.");
                    completion.TrySetResult();
                }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally
                {
                    if (passed) Directory.Delete(folder, true);
                    else Console.WriteLine("Role playback diagnostics retained: " + folder);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(100));
    }
    private static int FreeUdpPort() { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)socket.Client.LocalEndPoint!).Port; }
    private static int FreeTcpPort() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return ((IPEndPoint)socket.LocalEndpoint).Port; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
