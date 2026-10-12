using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using Forms = System.Windows.Forms;

internal static class VisualizationChecks
{
    public static async Task Run()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                Exception? failure = null;
                ExperienceHost? host = null;
                var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-visualization-" + Guid.NewGuid().ToString("N"));
                var preferences = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundSpectrum", "G-Force", "Preferences (Standalone).txt");
                byte[]? backup = null;
                int[] owned = [];
                try
                {
                    Check(!Process.GetProcesses().Any(p => { using (p) { try { return p.ProcessName.StartsWith("G-Force Standalone", StringComparison.OrdinalIgnoreCase); } catch { return false; } } }), "No independent Standalone is disturbed by this test");
                    backup = File.ReadAllBytes(preferences);
                    File.WriteAllText(preferences, File.ReadAllText(preferences).Replace("G-Force.AutoLaunchToolbar = 1", "G-Force.AutoLaunchToolbar = 0"));
                    Directory.CreateDirectory(folder);
                    var displays = Forms.Screen.AllScreens;
                    var assigned = Environment.GetEnvironmentVariable("EXPERIENCEX_VISUALIZATION_TEST_DISPLAY") is { } requested ? displays.Single(d => d.DeviceName == requested) : Forms.Screen.PrimaryScreen!;
                    var primary = assigned.DeviceName;
                    using var commands = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    using var events = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    var options = new ExperienceOptions
                    {
                        ConfigurationMode = true, CalibrationGridEnabled = false, DefaultDisplayName = primary,
                        UdpCommandsPort = ((IPEndPoint)commands.Client.LocalEndPoint!).Port,
                        UdpEventsPort = ((IPEndPoint)events.Client.LocalEndPoint!).Port,
                        MediaDiscovery = new() { Enabled = false }, TransitionInSeconds = .1,
                        DisplayScreens = displays.ToDictionary(d => d.DeviceName, _ => new List<ScreenOptions>()),
                        DisplayBackgrounds = displays.ToDictionary(d => d.DeviceName, _ => new DisplayBackgroundOptions { BackgroundColor = "#102030" })
                    };
                    commands.Dispose(); events.Dispose();
                    var path = Path.Combine(folder, "appsettings.json");
                    File.WriteAllText(path, JsonSerializer.Serialize(options));
                    PhotoBooth.Diagnostics.Telemetry.Start("VisualizationTest", path);
                    ExperienceOptions Read()
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(stream);
                        return ExperienceOptions.Parse(reader.ReadToEnd());
                    }
                    host = new(dispatcher, path); host.Start();
                    var window = host.DisplayWindows.Single(w => w.DisplayName == primary);
                    window.Activate(); window.Focus();
                    await Until(() => PhotoBooth.Windows.ConfigurationShortcut.ApplicationFocused, "Experience has focus for configuration shortcuts");
                    await Task.Delay(200);
                    void Add() { SendMessage(host.HotkeyWindowHandle, 0x312, ApplicationHotkeys.AddId, (nint)(0x41 << 16)); SendMessage(host.HotkeyWindowHandle, 0x312, ApplicationHotkeys.VisualizationId, (nint)(0x47 << 16)); }
                    Add();
                    await Until(() => Read().ScreensFor(primary).Any(s => s.IsVisualization), "A then G creates a visualization screen");
                    var visual = Read().ScreensFor(primary).Single();
                    Check(visual.Points == new PointOptions(), "Visualization covers the complete display");
                    Check(DisplayBackground.For(primary, Read()).A == 0 && Read().DisplayBackgrounds[primary].BackgroundColor == "#102030", "Visualization makes the background transparent without losing the configured color");
                    Check(!ScreenSelection.VideoOutputs(new() { Screen = "*" }, Read(), [primary], primary).Any(), "Video broadcasts exclude visualization screens");
                    Add(); await Task.Delay(300);
                    Check(Read().ScreensFor(primary).Count == 1, "A second visualization on the same display is rejected");
                    await Until(() => host.VisualizationProcessIds.Length == 1 && GForceWindow.Find(host.VisualizationProcessIds[0], GForceWindow.FullscreenClass) != 0, "Owned G-Force starts in actual fullscreen", 23000);
                    owned = host.VisualizationProcessIds;
                    var fullscreen = GForceWindow.Find(owned[0], GForceWindow.FullscreenClass);
                    Check(GForceWindow.Bounds(fullscreen) == assigned.Bounds, "Fullscreen is on its assigned display");
                    var store = new PointConfigurationStore(path);
                    var calibration = new PointCalibration(Read, () => window.Surfaces, store, () => Task.CompletedTask);
                    Check(!calibration.BeginScreenDrag(window.Surface(visual.Id), .2, .2) && calibration.SelectedScreen?.ScreenId == visual.Id, "Visualization can be selected but cannot be dragged");
                    await calibration.DisposeAsync();
                    try { await store.SaveAsync(primary, visual.Points, visual.Points with { TopLeft = new(.1, .1) }, visual.Id); throw new Exception("Visualization point edit was accepted."); } catch (InvalidOperationException) { Console.WriteLine("PASS Visualization point edits are rejected"); }
                    var media = await store.AddScreenAsync(primary);
                    var mat = await store.AddScreenAsync(primary, type: "mat");
                    await store.SaveAsync(primary, mat.Points, new() { Enabled = true, TopLeft = new(.7, .2), TopRight = new(.9, .2), BottomLeft = new(.7, .7), BottomRight = new(.9, .7) }, mat.Id);
                    await Until(() => window.ScreenDefinition(mat.Id) is not null, "Media and mat configuration reloads immediately");
                    var video = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Home Technologies", "Controller", "Media", "Eye.mp4");
                    window.PlayVideo(video, new() { Experience = "video", Value = "Eye.mp4", Loop = true, Mute = true, ScreenId = media.Id, RequestId = "visualization-test" });
                    await UntilAsync(async () => (await window.Renderer!.SnapshotAsync()).Single(s => s.Id == media.Id).Frames > 30, "Eye video renders above the visualization");
                    await store.SetConfigurationModeAsync(false);
                    await Task.Delay(700);
                    var b = assigned.Bounds;
                    using (var image = new Bitmap(b.Width, b.Height))
                    {
                        using var graphics = Graphics.FromImage(image); graphics.CopyFromScreen(b.Location, Point.Empty, b.Size);
                        image.Save(Path.Combine(folder, "overlay.png"), ImageFormat.Png);
                        var black = image.GetPixel((int)(b.Width * .8), (int)(b.Height * .4));
                        Check(black.R < 3 && black.G < 3 && black.B < 3, "The mat remains opaque black above G-Force");
                    }
                    if (displays.Length > 1)
                    {
                        await store.SetConfigurationModeAsync(true);
                        var other = displays.First(d => d.DeviceName != primary).DeviceName;
                        try { await store.AddScreenAsync(other, type: "visualization"); throw new Exception("A second visualization was accepted."); }
                        catch (InvalidOperationException) { Console.WriteLine("PASS Additional displays cannot create a competing fullscreen visualization"); }
                    }
                    var expectedCount = host.VisualizationProcessIds.Length;
                    var old = host.VisualizationProcessIds[0];
                    using (var crashed = Process.GetProcessById(old)) crashed.Kill();
                    await Until(() => !host.VisualizationProcessIds.Contains(old) && host.VisualizationProcessIds.Length == expectedCount && host.VisualizationProcessIds.All(pid => GForceWindow.Find(pid, GForceWindow.FullscreenClass) != 0), "A crashed visualization recovers with an owned replacement", 26000);
                    owned = host.VisualizationProcessIds;
                    await store.SetConfigurationModeAsync(true);
                    await GForceFullscreenChecks.FocusForTest(window.Handle);
                    window.Activate(); window.Focus();
                    await Until(() => PhotoBooth.Windows.ConfigurationShortcut.ApplicationFocused, "Experience regains focus for visualization editing");
                    await Task.Delay(200);
                    var click = (nint)(((window.ClientSize.Height / 10) << 16) | (window.ClientSize.Width / 10));
                    SendMessage(window.Handle, 0x201, 1, click);
                    SendMessage(window.Handle, 0x200, 1, (nint)(((window.ClientSize.Height / 5) << 16) | (window.ClientSize.Width / 5)));
                    SendMessage(window.Handle, 0x202, 0, click);
                    Check(host.SelectedScreen?.ScreenId == visual.Id && Read().PointsFor(primary, visual.Id) == visual.Points, "Mouse selects the visualization without moving its full-display geometry");
                    SendMessage(host.HotkeyWindowHandle, 0x312, ApplicationHotkeys.NameId, (nint)(0x4e << 16));
                    var editor = window.Controls.OfType<ScreenNameEditor>().Single();
                    editor.Text = "Spectrum";
                    SendMessage(editor.Handle, 0x100, 13, 0);
                    await Until(() => Read().ScreensFor(primary).Single(s => s.IsVisualization).Name == "Spectrum", "N edits and saves the visualization name");
                    SendMessage(window.Handle, 0x100, 0x2e, 0);
                    await Until(() => !Read().ScreensFor(primary).Any(s => s.IsVisualization) && host.VisualizationProcessIds.All(pid => GForceWindow.Bounds(GForceWindow.Find(pid, GForceWindow.FullscreenClass)) != b), "Deleting the visualization closes its fullscreen process");
                    Check(DisplayBackground.For(primary, Read()).A == 1, "Deletion restores the original solid display background");
                    owned = owned.Concat(host.VisualizationProcessIds).Distinct().ToArray();
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    try
                    {
                        if (host is not null) await host.DisposeAsync();
                        foreach (var pid in owned) Check(!Alive(pid), "Owned visualizer is closed after deletion or shutdown");
                        if (backup is not null) File.WriteAllBytes(preferences, backup);
                        PhotoBooth.Diagnostics.Telemetry.Stop();
                    }
                    catch (Exception ex) { failure ??= ex; }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    Console.WriteLine("Visualization evidence: " + folder);
                    if (failure is null) done.SetResult(); else done.SetException(failure);
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(120)); }
        finally { if (!thread.Join(TimeSpan.FromSeconds(10))) throw new Exception("Visualization test dispatcher did not close."); }
    }
    private static bool Alive(int pid) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; } }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static async Task Until(Func<bool> ready, string message, int timeout = 7000) => await UntilAsync(() => Task.FromResult(ready()), message, timeout);
    private static async Task UntilAsync(Func<Task<bool>> ready, string message, int timeout = 12000)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeout) { try { if (await ready()) { Check(true, message); return; } } catch (IOException) { } await Task.Delay(50); }
        throw new TimeoutException(message);
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint h, uint message, nint w, nint l);
}
