using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using Controller.Application;
using Controller.Configuration;
using Controller.Windows;
using PhotoBooth.Diagnostics;
using PhotoBooth.Networking;

internal static class LivePageChecks
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
                var folder = Path.Combine(Path.GetTempPath(), "Controller-live-ui-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                Exception? failure = null;
                try
                {
                    var options = new ControllerOptions
                    {
                        DeviceName = "live-ui-controller", UdpCommandsPort = FreeUdpPort(), UdpEventsPort = FreeUdpPort(),
                        DefinitionsPort = FreeTcpPort(), UdpSendAttempts = 1, UdpInterfaceAddress = "127.0.0.1",
                        Telemetry = JsonSerializer.SerializeToElement(new { Directory = Path.Combine(folder, "logs") })
                    };
                    var path = Path.Combine(folder, "appsettings.json");
                    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(options, ControllerOptions.Json));
                    await using var host = new ControllerHost(path);
                    await using var server = new SnapshotHttpServer(); server.Bind(0);
                    var inventory = new ExperienceInventory
                    {
                        Hostname = Environment.MachineName, DeviceName = "live-ui-peer", InstanceId = Guid.NewGuid().ToString("N"),
                        Screens = [new() { Id = "screen", Name = "screen1", Display = @"\\.\DISPLAY1", DisplayLabel = "Projector", Available = true, Roles = "Scary" }],
                        Speakers = [new() { Id = "left", Name = "Left subwoofer", Channels = 2, Roles = "subwoofer" }, new() { Id = "right", Name = "Right subwoofer", Channels = 2, Roles = "SUBWOOFER" }]
                    };
                    void Announce(ExperienceInventory snapshot)
                    {
                        var bytes = snapshot.Serialize(); var revision = ExperienceInventory.Revision(bytes); server.Publish(bytes, revision);
                        host.Experiences.Receive(BroadcastEnvelope.Parse(ExperienceDiscovery.Announce(new() { DeviceName = snapshot.DeviceName }, snapshot.InstanceId, revision, server.Port)) with { SenderAddress = "127.0.0.1" });
                    }
                    Announce(inventory); await Until(() => host.Experiences.Snapshot().Any(p => p.Usable));
                    using var form = new Form { ClientSize = new(1180, 760), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new(-32000, -32000) };
                    using var page = new LivePage(host); form.Controls.Add(page); form.Show(); page.RefreshState();
                    Button Choice(string text) => Descendants(page).OfType<Button>().Single(b => b.Text == text);
                    LiveTile[] Tiles() => Descendants(page).OfType<LiveTile>().ToArray();
                    Check(Tiles().Length == 1 && Tiles()[0].Tag is LiveTarget { Audio: false }, "Video mode shows only screens");
                    Tiles()[0].PerformClick();
                    Choice("Roles").PerformClick();
                    var role = Tiles().Single(t => t.Tag is string name && name.Equals("subwoofer", StringComparison.OrdinalIgnoreCase));
                    role.PerformClick();
                    Check(role.Selected && role.Text.Contains("2 speakers") && Tiles().Length == 2, "Roles mode selects one discovered subwoofer role representing two speakers");
                    Choice("Audio").PerformClick();
                    Check(Tiles().Length == 2 && Tiles().All(t => t.Tag is LiveTarget { Audio: true } && !t.Selected), "Audio mode does not inherit hidden video or role selection");
                    Choice("Select all").PerformClick();
                    Check(Tiles().All(t => t.Selected), "Audio Select all selects only the visible speakers");
                    Choice("Video").PerformClick();
                    Check(Tiles().Single().Selected, "Video selection survives switching target modes");
                    Choice("Roles").PerformClick();
                    Check(Tiles().Single(t => t.Tag is string name && name.Equals("subwoofer", StringComparison.OrdinalIgnoreCase)).Selected, "Role selection survives switching target modes");
                    form.PerformLayout(); page.PerformLayout();
                    using (var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                    {
                        form.DrawToBitmap(image, new(0, 0, image.Width, image.Height));
                        var screenshot = Path.Combine(Path.GetTempPath(), "Controller-Live-Roles.png"); image.Save(screenshot);
                        Console.WriteLine("Live screenshot: " + screenshot);
                    }
                    inventory = inventory with { Speakers = [inventory.Speakers[0] with { Roles = "Rear" }, inventory.Speakers[1] with { Roles = "Rear" }] };
                    Announce(inventory); await Until(() => host.Experiences.Snapshot()[0].Inventory.Speakers[0].Roles == "Rear"); page.RefreshState();
                    Check(Tiles().Any(t => t.Tag is string name && name == "Rear") && !Tiles().Any(t => t.Tag is string name && name.Equals("subwoofer", StringComparison.OrdinalIgnoreCase)), "Live role tiles refresh after an inventory update");
                }
                catch (Exception ex) { failure = ex; }
                finally { Telemetry.Stop(); Directory.Delete(folder, true); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
                if (failure is not null) completion.TrySetException(failure); else completion.TrySetResult();
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
    private static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    private static async Task Until(Func<bool> ready) { var end = DateTime.UtcNow.AddSeconds(5); while (!ready()) { if (DateTime.UtcNow > end) throw new TimeoutException("Live inventory did not update."); await Task.Delay(20); } }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static int FreeUdpPort() { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)socket.Client.LocalEndPoint!).Port; }
    private static int FreeTcpPort() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return ((IPEndPoint)socket.LocalEndpoint).Port; }
}
