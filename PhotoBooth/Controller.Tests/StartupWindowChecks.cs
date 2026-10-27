using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Controller.Configuration;
using Controller.Windows;
using PhotoBooth.Diagnostics;
using PhotoBooth.Networking;

internal static class StartupWindowChecks
{
    internal static void Run()
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        CheckStartup(false);
        CheckStartup(true);
    }
    private static void CheckStartup(bool configurationMode)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Controller-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var options = new ControllerOptions
        {
            DeviceName = "startup-test", ConfigurationMode = configurationMode, UdpInterfaceAddress = "127.0.0.1",
            UdpCommandsPort = FreeUdp(), UdpEventsPort = FreeUdp(), DefinitionsPort = FreeTcp(), UdpSendAttempts = 1,
            ExperienceSupervisionEnabled = false, ArduinoDiscovery = new() { Enabled = false },
            Scenes = [new() { Name = "idle", AmbientPlaylist = "ambient" }, new() { Name = "active", AmbientPlaylist = "ambient" }],
            Playlists = [new() { Name = "ambient", Type = "ambient" }],
            Telemetry = JsonSerializer.SerializeToElement(new { Directory = Path.Combine(folder, "logs") })
        };
        var path = Path.Combine(folder, "appsettings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(options, ControllerOptions.Json));
        Exception? failure = null;
        using var window = new ControlWindow(path);
        window.Shown += async (_, _) =>
        {
            try
            {
                await Until(() => window.Opacity == 1);
                Check(window.Visible && window.ShowInTaskbar && window.WindowState == (configurationMode ? FormWindowState.Normal : FormWindowState.Minimized),
                    configurationMode ? "Configuration mode starts with a visible workspace" : "Configuration mode off starts minimized with a taskbar entry");
                if (!configurationMode)
                {
                    // Exercise actual command handling while minimized, rather than only checking a window flag.
                    using var sender = new UdpClient();
                    var command = BroadcastEnvelope.Create(new() { DeviceName = "startup-check" }, "command", "startup-test", new { command = "scene", value = "active" });
                    await sender.SendAsync(command, new IPEndPoint(IPAddress.Loopback, options.UdpCommandsPort));
                    await Until(() => Descendants(window).OfType<Label>().Any(label => label.Text == "Scene: active • Active"));
                    await Task.Delay(1100);
                    Check(window.WindowState == FormWindowState.Minimized, "Scene commands and display polling continue without restoring the minimized window");
                    if (Screen.AllScreens.FirstOrDefault(s => !s.Primary) is { } secondary)
                    {
                        ConfigurationDefaults.Write(path, JsonSerializer.Serialize(options with { DefaultDisplayName = secondary.DeviceName }, ControllerOptions.Json));
                        await Until(() => window.RestoreBounds == secondary.Bounds);
                        Check(window.WindowState == FormWindowState.Minimized, "Changing the assigned display does not restore the minimized window");
                    }
                    window.RestoreFromTaskbar();
                    Check(window.WindowState == FormWindowState.Normal && window.Opacity == 1 && window.ShowInTaskbar, "Taskbar restore still opens the Controller workspace");
                    await Task.Delay(300);
                    Check(window.WindowState == FormWindowState.Normal, "Configuration mode off does not minimize an explicitly restored window again");
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { window.ExitController(); }
        };
        try { System.Windows.Forms.Application.Run(window); if (failure is not null) throw failure; }
        finally { Telemetry.Stop(); Directory.Delete(folder, true); }
    }
    private static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    private static async Task Until(Func<bool> ready)
    {
        var end = Environment.TickCount64 + 5000;
        while (!ready()) { if (Environment.TickCount64 > end) throw new TimeoutException("Controller startup state did not update."); await Task.Delay(10); }
    }
    private static int FreeUdp() { using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)client.Client.LocalEndPoint!).Port; }
    private static int FreeTcp() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
