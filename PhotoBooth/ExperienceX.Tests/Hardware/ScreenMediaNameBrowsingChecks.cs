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
                        DisplayScreens = Forms.Screen.AllScreens.ToDictionary(s => s.DeviceName, _ => new List<ScreenOptions>())
                    };
                    var path = Path.Combine(folder, "appsettings.json");
                    File.WriteAllText(path, JsonSerializer.Serialize(options));
                    await using var host = new ExperienceHost(dispatcher, path);
                    host.Start();
                    var window = host.DisplayWindows.Single(w => w.DisplayName == display);
                    var renderer = window.Renderer!;
                    string screenId = "";
                    // Reproduce A,V creation, then browse directly without first pressing N.
                    await GForceFullscreenChecks.FocusForTest(window.Handle);
                    await Task.Delay(150);
                    using (var accept = new Forms.Timer { Interval = 20 })
                    {
                        accept.Tick += (_, _) =>
                        {
                            var picker = Forms.Application.OpenForms.OfType<VideoScreenDialog>().FirstOrDefault();
                            if (picker is null) return;
                            picker.DialogResult = Forms.DialogResult.OK;
                            picker.Close();
                        };
                        accept.Start();
                        SendMessage(host.HotkeyWindowHandle, 0x312, ApplicationHotkeys.AddId, (nint)(0x41 << 16));
                        SendMessage(host.HotkeyWindowHandle, 0x312, ApplicationHotkeys.VideoId, (nint)(0x56 << 16));
                    }
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
                        while (!ready((await renderer.SnapshotAsync()).Single(t => t.Id == screenId)))
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
                        await Until(() => Read().Name == name && window.ScreenDefinition(screenId)?.Name == name &&
                            window.Controls.OfType<ScreenNameEditor>().SingleOrDefault()?.Text == name &&
                            window.Controls.OfType<ScreenNameEditor>().Single().ReadOnly == false);
                        Check(Read().DefaultName == "screen1", "Browsing changed the saved default name.");
                    }
                    await Until(() => host.SelectedScreen?.ScreenName == "screen1");
                    screenId = Read().Id;
                    await Until(() => window.ScreenDefinition(screenId) is not null);
                    Check(!window.Controls.OfType<ScreenNameEditor>().Any(), "A new screen unexpectedly opened name editing.");
                    var originalPoints = Read().Points;
                    async Task DirectName(string name)
                    {
                        await Until(() => Read().Name == name && window.ScreenDefinition(screenId)?.Name == name);
                        Check(Read().Points == originalPoints, "Media browsing moved a corner point.");
                        Check(!window.Controls.OfType<ScreenNameEditor>().Any(), "Direct browsing opened the name editor.");
                    }
                    SendMessage(window.Handle, 0x100, 39, 0); await DirectName("AVideo");
                    await Track(t => t.NameLocked && t.Frames > 2 && t.Loops >= 1 && t.Opacity == 1);
                    SendMessage(window.Handle, 0x100, 39, 0); await DirectName("BImage");
                    await Track(t => t.NameLocked && t.Frames == 1 && t.BoundPath?.EndsWith("BImage.png") == true);
                    SendMessage(window.Handle, 0x100, 37, 0); await DirectName("AVideo");
                    SendMessage(window.Handle, 0x100, 37, 0); await DirectName("screen1");
                    SendMessage(window.Handle, 0x100, 37, 0); await DirectName("ZImage");
                    SendMessage(window.Handle, 0x100, 39, 0); await DirectName("screen1");
                    SendMessage(window.Handle, 0x100, 39, 0); SendMessage(window.Handle, 0x100, 39, 0);
                    await DirectName("BImage");
                    SendMessage(window.Handle, 0x100, 37, 0); SendMessage(window.Handle, 0x100, 37, 0);
                    await DirectName("screen1");
                    // Tab focuses a corner: arrows must now adjust geometry, not media.
                    SendMessage(window.Handle, 0x100, 9, 0);
                    SendMessage(window.Handle, 0x100, 39, 0);
                    SendMessage(window.Handle, 0x100, 13, 0);
                    await Until(() => Read().Points.TopLeft.X > originalPoints.TopLeft.X);
                    Check(Read().Name == "screen1", "A point-selection arrow browsed media instead of moving the point.");
                    await Task.Delay(150);
                    var x = (int)(window.ClientSize.Width * .65); var y = (int)(window.ClientSize.Height * .65);
                    var position = (nint)((y << 16) | x);
                    SendMessage(window.Handle, 0x201, 1, position); SendMessage(window.Handle, 0x202, 0, position);
                    Check(host.SelectedScreen?.ScreenId == screenId, "The native click did not select the video screen.");
                    BrightnessKey(ApplicationHotkeys.BrightnessMinusId, 0xBD);
                    BrightnessKey(ApplicationHotkeys.BrightnessNumpadMinusId, 0x6D);
                    await Until(() => Read().Brightness == 90 && window.ScreenDefinition(screenId)?.Brightness == 90);
                    await Track(t => t.Brightness == 90);
                    var editor = OpenName();
                    Check(editor.Text == "screen1" && editor.SelectedText == "screen1", "N did not open and select the current name.");
                    BrightnessKey(ApplicationHotkeys.BrightnessMinusId, 0xBD);
                    await Task.Delay(100);
                    Check(Read().Brightness == 90, "Brightness changed while typing a screen name.");
                    Key(editor, 39); await Name("AVideo");
                    await Track(t => t.NameLocked && t.Frames > 2 && t.Loops >= 1 && t.Opacity == 1);
                    Key(editor, 13);
                    var beforeBrightness = (await renderer.SnapshotAsync()).Single(t => t.Id == screenId);
                    BrightnessKey(ApplicationHotkeys.BrightnessMinusId, 0xBD);
                    await Until(() => Read().Brightness == 85);
                    await Track(t => t.Brightness == 85 && t.RequestId == beforeBrightness.RequestId && t.Frames > beforeBrightness.Frames);
                    editor = OpenName();
                    Key(editor, 39); await Name("BImage");
                    await Track(t => t.NameLocked && t.Frames == 1 && t.BoundPath?.EndsWith("BImage.png") == true);
                    Key(editor, 13);
                    var imageBeforeBrightness = (await renderer.SnapshotAsync()).Single(t => t.Id == screenId);
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
                    await Until(() => Read().Name == "CustomName" && window.ScreenDefinition(screenId)?.Name == "CustomName");
                    editor = OpenName(); Key(editor, 39); await Name("AVideo");
                    Key(editor, 27);
                    // An explicit filename must start at its media entry, rather than position zero.
                    var store = new PointConfigurationStore(path);
                    await store.RenameScreenAsync(display, screenId, "AVideo", "AVideo.mp4");
                    await Until(() => window.ScreenDefinition(screenId)?.Name == "AVideo.mp4");
                    editor = OpenName(); Key(editor, 39); await Name("BImage");
                    await store.RenameScreenAsync(display, screenId, "BImage", "RemoteName");
                    await Until(() => window.ScreenDefinition(screenId)?.Name == "RemoteName" && !window.Controls.OfType<ScreenNameEditor>().Any());
                    Check(Read().Name == "RemoteName", "Name editing overwrote a remote rename.");
                    SendMessage(host.HotkeyWindowHandle, 0x312, ApplicationHotkeys.AddId, (nint)(0x41 << 16));
                    SendMessage(host.HotkeyWindowHandle, 0x312, ApplicationHotkeys.MatId, (nint)(0x4D << 16));
                    await Until(() => ExperienceOptions.Parse(File.ReadAllText(path)).ScreensFor(display).Count == 2 && host.SelectedScreen?.ScreenId != screenId);
                    var mat = ExperienceOptions.Parse(File.ReadAllText(path)).ScreensFor(display).Last();
                    Check(mat.IsMat && mat.Name == "screen2", "Adding a mat reused the browsed screen's saved default name.");
                    editor = OpenName(); Key(editor, 39); Key(editor, 37);
                    await Task.Delay(100);
                    Check(editor.Text == "screen2", "A mat name editor browsed playable media.");
                    editor.Text = "BImage"; Key(editor, 13);
                    await Until(() => window.ScreenDefinition(mat.Id)?.Name == "BImage");
                    // Media already used by another screen remains selectable with a unique screen name.
                    window.EditScreenName(window.Surface(screenId));
                    editor = window.Controls.OfType<ScreenNameEditor>().Single();
                    Key(editor, 39); await Name("AVideo");
                    Key(editor, 39); await Name("BImage-2");
                    Check(Read().MediaFile == "BImage.png", "A repeated image was not bound to the original local file.");
                    Key(editor, 39); await Name("ZImage");
                    Key(editor, 27);
                    // Two independently positioned screens can browse to and render one local video concurrently.
                    window.EditScreenName(window.Surface(screenId)); editor = window.Controls.OfType<ScreenNameEditor>().Single();
                    Key(editor,39); await Name("screen1"); Key(editor,39); await Name("AVideo"); Key(editor,13);
                    var duplicateStore = new PointConfigurationStore(path);
                    var duplicate = await duplicateStore.AddScreenAsync(display);
                    await Until(()=>window.ScreenDefinition(duplicate.Id) is not null);
                    window.EditScreenName(window.Surface(duplicate.Id)); editor = window.Controls.OfType<ScreenNameEditor>().Single();
                    Key(editor,39);
                    await Until(()=>window.ScreenDefinition(duplicate.Id)?.Name == "AVideo-2" && window.ScreenDefinition(duplicate.Id)?.MediaFile == "AVideo.mp4");
                    Key(editor,13);
                    var bothDeadline = DateTime.UtcNow.AddSeconds(15);
                    ScreenPlaybackSnapshot[] before;
                    while(true)
                    {
                        before=(await renderer.SnapshotAsync()).Where(t=>t.Id==screenId || t.Id==duplicate.Id).ToArray();
                        if(before.Length==2 && before.All(t=>t.NameLocked && t.Frames>2 && t.Opacity==1 && t.BoundPath?.EndsWith("AVideo.mp4")==true)) break;
                        if(DateTime.UtcNow>bothDeadline) throw new TimeoutException("Duplicate video screens did not render together: "+JsonSerializer.Serialize(before));
                        await Task.Delay(25);
                    }
                    await Task.Delay(750);
                    var after=await renderer.SnapshotAsync();
                    Check(before.All(t=>after.Single(n=>n.Id==t.Id).Frames>t.Frames),"One duplicate video screen stopped advancing when the other started.");
                    var saved=ExperienceOptions.Parse(File.ReadAllText(path)).ScreensFor(display).Single(s=>s.Id==duplicate.Id);
                    Check(saved.Name=="AVideo-2" && saved.MediaFile=="AVideo.mp4" && saved.Points==duplicate.Points,"Duplicate playback did not persist its name/file independently of its geometry.");
                    Console.WriteLine("Passed two real screens concurrently advancing frames from the same video selected with arrow keys.");
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
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(90)); }
        finally { await Task.Run(() => thread.Join(TimeSpan.FromSeconds(10))); }
        Console.WriteLine("Passed native N name editing, alphabetical Left/Right media playback, both wraps, immediate persistence, rapid keys and Enter/Tab/Escape behavior.");
        Console.WriteLine("Passed A/V new-screen direct media browsing, video/image rendering, wraps, rapid keys and point-arrow precedence.");
        Console.WriteLine("Passed selected-screen +/- brightness persistence, queued steps, typing guards, remote updates and uninterrupted video/image playback.");
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
