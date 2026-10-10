using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using PhotoBooth.Networking;
using Forms = System.Windows.Forms;

internal static class AllScreenPlaybackChecks
{
    public static async Task Run()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-all-screens-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                try
                {
                    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
                    var video = Environment.GetEnvironmentVariable("EXPERIENCEX_TEST_VIDEO") ?? Path.Combine(root, "experiencex", "Media", "ping.mp4");
                    if (!File.Exists(video)) throw new FileNotFoundException("Provide EXPERIENCEX_TEST_VIDEO for all-screen playback.", video);
                    using var commandsSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    using var eventsSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    int commands = ((IPEndPoint)commandsSocket.Client.LocalEndPoint!).Port, events = ((IPEndPoint)eventsSocket.Client.LocalEndPoint!).Port;
                    commandsSocket.Dispose(); eventsSocket.Dispose();
                    var displays = Forms.Screen.AllScreens.Select(s => s.DeviceName).ToArray();
                    var options = new ExperienceOptions
                    {
                        DeviceName = "all-screens-test", DeveloperMode = true, MediaDiscovery = new() { Enabled = false },
                        VideoDirectory = Path.GetDirectoryName(video)!, UdpCommandsPort = commands, UdpEventsPort = events, TransitionInSeconds = .05,
                        DisplayScreens = displays.ToDictionary(d => d, d => new List<ScreenOptions>
                        {
                            new() { Id = "first", Name = "screen1" }, new() { Id = "second", Name = "screen2" },
                            new() { Id = "mat", Name = "mask", Type = "mat" }
                        })
                    };
                    var path = Path.Combine(folder, "appsettings.json");
                    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(options));
                    await using var host = new ExperienceHost(dispatcher, path);
                    host.Start();
                    var senderOptions = new BroadcastOptions { DeviceName = "wildcard-sender", UdpCommandsPort = commands, UdpEventsPort = events, UdpSendAttempts = 1 };
                    await UdpBroadcastSender.SendAsync(BroadcastEnvelope.Create(senderOptions, "command", "*", new { experience = "video", value = Path.GetFileName(video), screen = "*", mute = true }, "all-video-screens"), senderOptions);
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    while (true)
                    {
                        var snapshots = await Task.WhenAll(host.DisplayWindows.Select(w => w.Renderer!.SnapshotAsync()));
                        if (snapshots.Length == displays.Length && snapshots.All(s => s.Count(t => t.RequestId == "all-video-screens" && t.Frames > 1 && t.Position > .1) == 2))
                        {
                            if (snapshots.Any(s => s.Single(t => t.Id == "mat").RequestId is not null)) throw new Exception("Wildcard video was sent to a mat.");
                            Console.WriteLine($"Passed one real UDP broadcast playing {displays.Length * 2} video screens across {displays.Length} displays; mats remained idle.");
                            break;
                        }
                        if (DateTime.UtcNow > deadline) throw new TimeoutException("Wildcard broadcast did not start all video screens.");
                        await Task.Delay(50);
                    }
                    completion.TrySetResult();
                }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { Directory.Delete(folder, true); dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(35));
    }
}
