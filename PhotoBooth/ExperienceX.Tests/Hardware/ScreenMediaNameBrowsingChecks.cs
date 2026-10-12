using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using Forms = System.Windows.Forms;

internal static class ScreenMediaNameBrowsingChecks
{
    public static async Task Run()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-name-browse-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                try
                {
                    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
                    var fixture = Environment.GetEnvironmentVariable("EXPERIENCEX_BOUND_TEST_VIDEO") ?? Path.Combine(root, ".tools", "screen-media-fixture.mp4");
                    if (!File.Exists(fixture)) throw new FileNotFoundException("Provide EXPERIENCEX_BOUND_TEST_VIDEO for name browsing checks.", fixture);
                    var media = Path.Combine(folder, "Media"); Directory.CreateDirectory(media);
                    File.Copy(fixture, Path.Combine(media, "AVideo.mp4"));
                    foreach (var name in new[] { "BImage", "ZImage" })
                    {
                        using var bitmap = new Bitmap(16, 16);
                        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.Red);
                        bitmap.Save(Path.Combine(media, name + ".png"), ImageFormat.Png);
                    }
                    using var commandSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    using var eventSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    var commands = ((IPEndPoint)commandSocket.Client.LocalEndPoint!).Port;
                    var events = ((IPEndPoint)eventSocket.Client.LocalEndPoint!).Port;
                    commandSocket.Dispose(); eventSocket.Dispose();
                    var display = Forms.Screen.PrimaryScreen!.DeviceName;
                    var options = new ExperienceOptions
                    {
                        DeveloperMode = true, ConfigurationMode = true, MediaDiscovery = new() { Enabled = false },
                        VideoDirectory = media, UdpCommandsPort = commands, UdpEventsPort = events,
                        CalibrationGridEnabled = false, TransitionInSeconds = .05,
                        DisplayBackgrounds = Forms.Screen.AllScreens.ToDictionary(s => s.DeviceName, _ => new DisplayBackgroundOptions { IsTransparent = true }),
                        DisplayScreens = Forms.Screen.AllScreens.ToDictionary(s => s.DeviceName,
                            s => s.DeviceName == display ? new List<ScreenOptions> { new() } : [])
                    };
                    var path = Path.Combine(folder, "appsettings.json");
                    File.WriteAllText(path, JsonSerializer.Serialize(options));
                    await using var host = new ExperienceHost(dispatcher, path);
                    host.Start();
                    var window = host.DisplayWindows.Single(w => w.DisplayName == display);
                    var renderer = window.Renderer!;
                    ScreenOptions Read() => ExperienceOptions.Parse(File.ReadAllText(path)).ScreensFor(display).First();
                    void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
                    async Task Until(Func<bool> ready)
                    {
                        var deadline = DateTime.UtcNow.AddSeconds(8);
                        while (true)
                        {
                            try { if (ready()) return; } catch (IOException) { }
                            if (DateTime.UtcNow > deadline) throw new TimeoutException("Name browsing timed out: " + Read().Name);
                            await Task.Delay(20);
                        }
                    }
                    async Task Track(Func<ScreenPlaybackSnapshot, bool> ready)
                    {
                        var deadline = DateTime.UtcNow.AddSeconds(12);
                        while (!ready((await renderer.SnapshotAsync()).Single(t => t.Id == "screen1")))
                        {
                            if (DateTime.UtcNow > deadline) throw new TimeoutException("Browsed media did not render: " + Read().Name);
                            await Task.Delay(25);
                        }
                    }
                    void Key(ScreenNameEditor editor, int key) => SendMessage(editor.Handle, 0x100, key, 0);
                    ScreenNameEditor OpenName()
                    {
                        SendMessage(host.HotkeyWindowHandle, 0x312, ApplicationHotkeys.NameId, (nint)(0x4E << 16));
                        return window.Controls.OfType<ScreenNameEditor>().Single();
                    }
                    void BrightnessKey(int id, int key, int modifiers = 0) =>
                        SendMessage(host.HotkeyWindowHandle, 0x312, id, (nint)((key << 16) | modifiers));
                    async Task Name(string name)
                    {
                        await Until(() => Read().Name == name && window.ScreenDefinition("screen1")?.Name == name &&
                            window.Controls.OfType<ScreenNameEditor>().SingleOrDefault()?.Text == name &&
                            window.Controls.OfType<ScreenNameEditor>().Single().ReadOnly == false);
                        Check(Read().DefaultName == "screen1", "Browsing changed the saved default name.");
                    }
                    var x = (int)(window.ClientSize.Width * .65); var y = (int)(window.ClientSize.Height * .65);
                    var position = (nint)((y << 16) | x);
                    SendMessage(window.Handle, 0x201, 1, position); SendMessage(window.Handle, 0x202, 0, position);
                    Check(host.SelectedScreen?.ScreenId == "screen1", "The native click did not select the video screen.");
                    BrightnessKey(ApplicationHotkeys.BrightnessMinusId, 0xBD);
                    BrightnessKey(ApplicationHotkeys.BrightnessNumpadMinusId, 0x6D);
                    await Until(() => Read().Brightness == 90 && window.ScreenDefinition("screen1")?.Brightness == 90);
                    await Track(t => t.Brightness == 90);
                    var editor = OpenName();
                    Check(editor.Text == "screen1" && editor.SelectedText == "screen1", "N did not open and select the current name.");
                    BrightnessKey(ApplicationHotkeys.BrightnessMinusId, 0xBD);
                    await Task.Delay(100);
                    Check(Read().Brightness == 90, "Brightness changed while typing a screen name.");
                    Key(editor, 39); await Name("AVideo");
                    await Track(t => t.NameLocked && t.Frames > 2 && t.Loops >= 1 && t.Opacity == 1);
                    Key(editor, 13);
                    var beforeBrightness = (await renderer.SnapshotAsync()).Single(t => t.Id == "screen1");
                    BrightnessKey(ApplicationHotkeys.BrightnessMinusId, 0xBD);
                    await Until(() => Read().Brightness == 85);
                    await Track(t => t.Brightness == 85 && t.RequestId == beforeBrightness.RequestId && t.Frames > beforeBrightness.Frames);
                    editor = OpenName();
                    Key(editor, 39); await Name("BImage");
                    await Track(t => t.NameLocked && t.Frames == 1 && t.BoundPath?.EndsWith("BImage.png") == true);
                    Key(editor, 13);
                    var imageBeforeBrightness = (await renderer.SnapshotAsync()).Single(t => t.Id == "screen1");
                    BrightnessKey(ApplicationHotkeys.BrightnessShiftPlusId, 0xBB, 4);
                    await Until(() => Read().Brightness == 90);
                    await Track(t => t.Brightness == 90 && t.RequestId == imageBeforeBrightness.RequestId && t.Frames == 1);
                    // Remote configuration changes also reach the renderer without restarting the image.
                    var remote = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
                    remote["DisplayScreens"]![display]![0]!["Brightness"] = 62.5;
                    File.WriteAllText(path, remote.ToJsonString());
                    await Track(t => t.Brightness == 62.5 && t.RequestId == imageBeforeBrightness.RequestId);
                    editor = OpenName();
                    Key(editor, 37); await Name("AVideo");
                    Key(editor, 37); await Name("screen1");
                    await Track(t => !t.NameLocked && t.RequestId is null);
                    Key(editor, 37); await Name("ZImage");
                    Key(editor, 39); await Name("screen1");
                    // Queue directions before the first atomic save finishes, then commit immediately.
                    Key(editor, 39); Key(editor, 39); Key(editor, 13);
                    await Until(() => Read().Name == "BImage" && !window.Controls.OfType<ScreenNameEditor>().Any());
                    Check(Read().DefaultName == "screen1", "Rapid browsing/Enter lost the zero position.");
                    editor = OpenName(); Key(editor, 39); await Name("ZImage");
                    editor.Text = "Unsaved"; Key(editor, 27);
                    Check(!window.Controls.OfType<ScreenNameEditor>().Any() && Read().Name == "ZImage",
                        "Escape did not discard only unsaved typing while retaining the saved media selection.");
                    editor = OpenName(); editor.Text = "CustomName"; Key(editor, 9);
                    await Until(() => Read().Name == "CustomName" && window.ScreenDefinition("screen1")?.Name == "CustomName");
                    editor = OpenName(); Key(editor, 39); await Name("AVideo");
                    Key(editor, 27);
                    // An explicit filename must start at its media entry, rather than position zero.
                    var store = new PointConfigurationStore(path);
                    await store.RenameScreenAsync(display, "screen1", "AVideo", "AVideo.mp4");
                    await Until(() => window.ScreenDefinition("screen1")?.Name == "AVideo.mp4");
                    editor = OpenName(); Key(editor, 39); await Name("BImage");
                    await store.RenameScreenAsync(display, "screen1", "BImage", "RemoteName");
                    await Until(() => window.ScreenDefinition("screen1")?.Name == "RemoteName" && !window.Controls.OfType<ScreenNameEditor>().Any());
                    Check(Read().Name == "RemoteName", "Name editing overwrote a remote rename.");
                    SendMessage(host.HotkeyWindowHandle, 0x312, ApplicationHotkeys.AddId, (nint)(0x41 << 16));
                    SendMessage(host.HotkeyWindowHandle, 0x312, ApplicationHotkeys.MatId, (nint)(0x4D << 16));
                    await Until(() => ExperienceOptions.Parse(File.ReadAllText(path)).ScreensFor(display).Count == 2 && host.SelectedScreen?.ScreenId != "screen1");
                    var mat = ExperienceOptions.Parse(File.ReadAllText(path)).ScreensFor(display).Last();
                    Check(mat.IsMat && mat.Name == "screen2", "Adding a mat reused the browsed screen's saved default name.");
                    editor = OpenName(); Key(editor, 39); Key(editor, 37);
                    await Task.Delay(100);
                    Check(editor.Text == "screen2", "A mat name editor browsed playable media.");
                    editor.Text = "BImage"; Key(editor, 13);
                    await Until(() => window.ScreenDefinition(mat.Id)?.Name == "BImage");
                    // Names already owned by other screens are skipped rather than causing failed saves.
                    window.EditScreenName(window.Surface("screen1"));
                    editor = window.Controls.OfType<ScreenNameEditor>().Single();
                    Key(editor, 39); await Name("AVideo");
                    Key(editor, 39); await Name("ZImage");
                    Key(editor, 27);
                }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally
                {
                    try { Directory.Delete(folder, true); }
                    catch (Exception ex) { completion.TrySetException(ex); }
                    if (!completion.Task.IsCompleted) completion.TrySetResult();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(60)); }
        finally { await Task.Run(() => thread.Join(TimeSpan.FromSeconds(10))); }
        Console.WriteLine("Passed native N name editing, alphabetical Left/Right media playback, both wraps, immediate persistence, rapid keys and Enter/Tab/Escape behavior.");
        Console.WriteLine("Passed selected-screen +/- brightness persistence, queued steps, typing guards, remote updates and uninterrupted video/image playback.");
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
