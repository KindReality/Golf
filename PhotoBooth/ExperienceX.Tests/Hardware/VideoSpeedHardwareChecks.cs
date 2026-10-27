using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using PhotoBooth.Networking;
using Forms = System.Windows.Forms;

internal static class VideoSpeedHardwareChecks
{
    internal static async Task Run(bool softwareVideoDecoding = false)
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
                var root = Path.Combine(Path.GetTempPath(), "VideoSpeed-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
                try
                {
                    File.Copy(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.tools/priority-fixture.mp4")), Path.Combine(root, "Sample.mp4"));
                    var display = Forms.Screen.PrimaryScreen!.DeviceName;
                    var options = new ExperienceOptions { DeveloperMode = true, DeviceName = "speed-client", LocalController = new() { Enabled = false }, VideoDirectory = root, SoftwareVideoDecoding = softwareVideoDecoding,
                        UdpCommandsPort = FreeUdp(), UdpEventsPort = FreeUdp(), InventoryPort = FreeTcp(), TransitionInSeconds = .05, TransitionOutSeconds = .1,
                        DisplayScreens = new() { [display] = [new() { Id = "slow", Name = "slow" }, new() { Id = "fast", Name = "fast" }] } };
                    var path = Path.Combine(root, "appsettings.json"); await File.WriteAllTextAsync(path, JsonSerializer.Serialize(options));
                    await using (var host = new ExperienceHost(dispatcher, path))
                    {
                        host.Start(); var renderer = host.DisplayWindows.Single(w => w.DisplayName == display).Renderer!;
                        async Task<ScreenPlaybackSnapshot[]> Until(Func<ScreenPlaybackSnapshot[], bool> ready)
                        {
                            var start = Stopwatch.StartNew();
                            while (start.Elapsed.TotalSeconds < 10) { var states = await renderer.SnapshotAsync(); if (states.Length == 2 && ready(states)) return states; await Task.Delay(15); }
                            throw new TimeoutException("Playback-speed state did not arrive.");
                        }
                        async Task Send(string id, string screen, double speed)
                        {
                            var sender = new BroadcastOptions { DeviceName = "speed-controller", UdpCommandsPort = options.UdpCommandsPort, UdpEventsPort = options.UdpEventsPort, UdpSendAttempts = 1 };
                            await UdpBroadcastSender.SendAsync(BroadcastEnvelope.Create(sender, "command", options.DeviceName,
                                new { experience = "video", value = "Sample.mp4", screen = display + "/" + screen, playbackSpeed = speed, loop = true, mute = true }, id), sender);
                        }
                        await Send("slow-start", "slow", .5); await Send("fast-start", "fast", 2);
                        var first = await Until(s => s.All(x => x.Frames > 2 && x.Opacity > .95));
                        var clock = Stopwatch.StartNew(); await Task.Delay(350);
                        var second = await renderer.SnapshotAsync(); var elapsed = clock.Elapsed.TotalSeconds;
                        double Progress(ScreenPlaybackSnapshot after, ScreenPlaybackSnapshot before) => after.Position - before.Position + (after.Loops - before.Loops) * 2;
                        var slowRate = Progress(second[0], first[0]) / elapsed; var fastRate = Progress(second[1], first[1]) / elapsed;
                        Check(slowRate is > .2 and < .9 && fastRate is > 1.2 and < 2.8, $"Native playback advances at different requested rates: slow={slowRate:F2}×, fast={fastRate:F2}×.");
                        Check(second[0].PlaybackSpeed == .5 && second[1].PlaybackSpeed == 2, "Playback speed is isolated per screen.");
                        var frames = second[0].Frames; var position = second[0].Position;
                        await Send("slow-now-fast", "slow", 2);
                        var changed = await Until(s => s[0].RequestId == "slow-now-fast" && s[0].PlaybackSpeed == 2);
                        Check(changed[0].Frames >= frames && changed[0].Position >= position && changed[0].Opacity > .95,
                            "Changing speed on the same video preserves its decoder, position and visibility.");
                        await Until(s => s[0].Loops > 0 && s[1].Loops > 0);
                        Check(true, "Native playback loops normally at double speed.");
                    }
                    done.TrySetResult();
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { Directory.Delete(root, true); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { thread.Join(3000); Environment.SetEnvironmentVariable(LocalTestNetwork.Variable, previous); }
    }
    private static int FreeUdp() { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)socket.Client.LocalEndPoint!).Port; }
    private static int FreeTcp() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return ((IPEndPoint)socket.LocalEndpoint).Port; }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
