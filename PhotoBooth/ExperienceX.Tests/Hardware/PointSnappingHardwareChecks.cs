using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using Forms = System.Windows.Forms;

internal static class PointSnappingHardwareChecks
{
    internal static async Task Run()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                Exception? failure = null;
                ExperienceHost? host = null;
                var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-snapping-ui-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                try
                {
                    var display = Forms.Screen.PrimaryScreen!.DeviceName;
                    using var commands = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    using var events = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    var source = new ScreenOptions { Id = "source", Name = "source", Points = Quad(.1, .1, .4, .4) };
                    var mat = new ScreenOptions { Id = "mat", Name = "mat", Type = "mat", Points = Quad(.5, .1, .8, .4) };
                    var options = new ExperienceOptions
                    {
                        DeveloperMode = true, ConfigurationMode = true, CalibrationGridEnabled = false,
                        MediaDiscovery = new() { Enabled = false }, DefaultDisplayName = display,
                        UdpCommandsPort = ((IPEndPoint)commands.Client.LocalEndPoint!).Port,
                        UdpEventsPort = ((IPEndPoint)events.Client.LocalEndPoint!).Port,
                        DisplayScreens = Forms.Screen.AllScreens.ToDictionary(d => d.DeviceName, d => d.DeviceName == display ? new List<ScreenOptions> { source, mat } : []),
                        DisplayBackgrounds = Forms.Screen.AllScreens.ToDictionary(d => d.DeviceName, _ => new DisplayBackgroundOptions { IsTransparent = true })
                    };
                    commands.Dispose(); events.Dispose();
                    var path = Path.Combine(folder, "appsettings.json");
                    File.WriteAllText(path, JsonSerializer.Serialize(options));
                    ExperienceOptions Read()
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(stream);
                        return ExperienceOptions.Parse(reader.ReadToEnd());
                    }
                    host = new(dispatcher, path); host.Start();
                    var window = host.DisplayWindows.Single(w => w.DisplayName == display);
                    await GForceFullscreenChecks.FocusForTest(window.Handle);
                    nint Position(double x, double y) => (nint)(((int)Math.Round(y * (window.ClientSize.Height - 1)) << 16) | (int)Math.Round(x * (window.ClientSize.Width - 1)));
                    void Down(ScreenPoint p) => SendMessage(window.Handle, 0x201, 1, Position(p.X, p.Y));
                    void Move(double x, double y) => SendMessage(window.Handle, 0x200, 1, Position(x, y));
                    void Up(double x, double y) => SendMessage(window.Handle, 0x202, 0, Position(x, y));
                    Down(source.Points.TopRight);
                    Check(window.Capture && host.SelectedScreen?.ScreenId == source.Id, "Native mouse-down selects and captures the source point");
                    Move(.49, .1); Move(.54, .15); Up(.54, .15);
                    await Until(() => Read().PointsFor(display, source.Id).TopRight == Read().PointsFor(display, mat.Id).TopLeft && Read().PointsFor(display, source.Id).TopRight.X > .5,
                        "Native drag and release save identical linked media/mat coordinates");
                    await Task.Delay(250);
                    var joined = Read().PointsFor(display, source.Id).TopRight;
                    SendMessage(window.Handle, 0x100, 0x2e, 0);
                    Check(Read().ScreensFor(display).Count == 2, "Delete on a released point keeps both screens");
                    Down(joined); Move(joined.X + .01, joined.Y); Up(joined.X + .01, joined.Y);
                    await Until(() => Read().PointsFor(display, source.Id).TopRight != Read().PointsFor(display, mat.Id).TopLeft,
                        "Native Delete allows pulling a shared point apart without immediate resnapping");
                    Check(Read().PointsFor(display, mat.Id).TopLeft == joined, "Disconnect leaves the other screen's point stationary");
                    await Task.Delay(250);
                    SendMessage(window.Handle, 0x100, 0x5a, 0);
                    await Until(() => Read().PointsFor(display, source.Id).TopRight == joined, "Native Z undoes the saved separation");
                    await Task.Delay(250);
                    Down(joined); Move(joined.X + .06, joined.Y + .03); Up(joined.X + .06, joined.Y + .03);
                    await Until(() => Read().PointsFor(display, source.Id).TopRight == Read().PointsFor(display, mat.Id).TopLeft && Read().PointsFor(display, source.Id).TopRight.X > joined.X + .04,
                        "Undo restores exact-coordinate grouping on the next mouse drag");
                    await Task.Delay(250);
                    Down(new(.2, .2)); Up(.2, .2);
                    SendMessage(window.Handle, 0x100, 0x2e, 0);
                    await Until(() => Read().ScreensFor(display).Count == 1 && Read().ScreensFor(display).Single().IsMat,
                        "Delete on the screen interior retains normal screen deletion");
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    try { if (host is not null) await host.DisposeAsync(); Directory.Delete(folder, true); }
                    catch (Exception ex) { failure ??= ex; }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    if (failure is null) done.SetResult(); else done.SetException(failure);
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(40)); }
        finally { if (!thread.Join(TimeSpan.FromSeconds(10))) throw new Exception("Snapping UI test did not shut down."); }
    }
    private static PointOptions Quad(double x, double y, double right, double bottom) => new() { Enabled = true, TopLeft = new(x, y), TopRight = new(right, y), BottomLeft = new(x, bottom), BottomRight = new(right, bottom) };
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static async Task Until(Func<bool> ready, string message)
    {
        var until = DateTime.UtcNow.AddSeconds(7);
        while (DateTime.UtcNow < until)
        {
            try { if (ready()) { Check(true, message); return; } } catch (IOException) { }
            await Task.Delay(25);
        }
        throw new TimeoutException(message);
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint h, uint message, nint w, nint l);
}
