using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using PhotoBooth.Networking;
using Forms = System.Windows.Forms;

internal static class ConfigurationSubmenuChecks
{
    public static async Task Run()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                var previousNetwork = Environment.GetEnvironmentVariable(LocalTestNetwork.Variable);
                Environment.SetEnvironmentVariable(LocalTestNetwork.Variable, "127.0.0.3");
                try
                {
                    var root = Path.Combine(Path.GetTempPath(), "ExperienceX-submenus-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(root);
                    var displays = Forms.Screen.AllScreens.Select(s => s.DeviceName).ToArray();
                    var target = Forms.Screen.FromPoint(Forms.Cursor.Position).DeviceName;
                    int Port() { using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)udp.Client.LocalEndPoint!).Port; }
                    var commands = Port(); var events = Port(); while (events == commands) events = Port();
                    var options = new ExperienceOptions
                    {
                        DeveloperMode = true, ConfigurationMode = true, CalibrationGridEnabled = false,
                        MediaDiscovery = new() { Enabled = false }, VideoDirectory = root, AudioDirectory = root,
                        UdpCommandsPort = commands, UdpEventsPort = events, UdpSendAttempts = 1,
                        DisplayBackgrounds = displays.ToDictionary(d => d, _ => new DisplayBackgroundOptions { IsTransparent = true }),
                        DisplayScreens = displays.ToDictionary(d => d, _ => new List<ScreenOptions> { new()
                        {
                            Id = "base", Name = "screen1", Points = new() { Enabled = true,
                                TopLeft = new(.2,.2), TopRight = new(.8,.2), BottomLeft = new(.2,.8), BottomRight = new(.8,.8) }
                        } })
                    };
                    var path = Path.Combine(root, "appsettings.json");
                    File.WriteAllText(path, JsonSerializer.Serialize(options));
                    await using (var host = new ExperienceHost(dispatcher, path, () => true))
                    {
                        host.Start();
                        var window = host.DisplayWindows.Single(w => w.DisplayName == target);
                        int restarted = 0; host.RestartRequested += () => restarted++;
                        var at = (nint)(((int)(window.ClientSize.Height * .2) << 16) | (int)(window.ClientSize.Width * .2));
                        SendMessage(window.Handle, 0x201, 1, at); SendMessage(window.Handle, 0x202, 0, at);
                        await Until(() => host.SelectedScreen?.ScreenId == "base", "A screen point is selected before opening a submenu");
                        var before = Read().ScreensFor(target).Single();
                        void Press(int id, int key, int modifiers = 0) => SendMessage(host.HotkeyWindowHandle, 0x312, id, (nint)((key << 16) | modifiers));
                        void BlockedActions()
                        {
                            Press(ApplicationHotkeys.TransparencyId, 0x4b); Press(ApplicationHotkeys.NameId, 0x4e);
                            Press(ApplicationHotkeys.RestartId, 0x58); Press(ApplicationHotkeys.PerspectiveId, 0x50);
                            Press(ApplicationHotkeys.PasteScreenId, 0x56, 2); Press(ApplicationHotkeys.BrightnessPlusId, 0xbb);
                            foreach (var key in new[] { 0x2e, 0x27, 0x52, 0x4a, 0x09, 0xdd }) SendMessage(window.Handle, 0x100, key, 0);
                        }
                        Press(ApplicationHotkeys.AddId, 0x41);
                        Check(host.ActiveSubmenu == ConfigurationSubmenu.AddScreen && host.DisplayWindows.All(w => w.Submenu == ConfigurationSubmenu.AddScreen), "A opens one submenu across all display windows");
                        Check(host.HudMessage == ConfigurationSubmenuChoices.Text(ConfigurationSubmenu.AddScreen), "Add HUD contains only V/M/G/L and Esc");
                        BlockedActions(); Press(ApplicationHotkeys.BackgroundId, 0x42);
                        window.ModifierInputForTest = () => new(Forms.Keys.Control, new(.5,.5), true);
                        await Task.Delay(2100); // Both the status timeout and focus-registration timer must preserve the menu.
                        var notice = Forms.Application.OpenForms.OfType<ConfigurationModeNotice>().Single();
                        Check(notice.Visible && host.ActiveSubmenu == ConfigurationSubmenu.AddScreen && host.HudMessage == ConfigurationSubmenuChoices.Text(ConfigurationSubmenu.AddScreen), "Submenu persists until a choice or cancellation");
                        Check(Read().ScreensFor(target).Single() == before && restarted == 0 && !window.ModifierFramingActive && !Forms.Application.OpenForms.OfType<ConfigurationDialog>().Any(), "Hidden keyboard actions and modifier gestures cannot edit, delete, restart or open dialogs");
                        window.ModifierInputForTest = () => new(Forms.Keys.None, new(.5,.5), true);
                        using (var bitmap = new System.Drawing.Bitmap(notice.Width, notice.Height))
                        {
                            notice.DrawToBitmap(bitmap, new(0, 0, bitmap.Width, bitmap.Height));
                            var image = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.tools/submenu-hud.png"));
                            Directory.CreateDirectory(Path.GetDirectoryName(image)!); bitmap.Save(image);
                        }
                        SendMessage(window.Handle, 0x100, 0x1b, 0);
                        Check(host.ActiveSubmenu == ConfigurationSubmenu.None && host.DisplayWindows.All(w => w.Submenu == ConfigurationSubmenu.None) && !notice.Visible, "Esc on the content window dismisses the submenu and restores normal HUD state");
                        var center = ScreenNameLabel.Center(before.Points, window.ClientSize.Width, window.ClientSize.Height);
                        var centerAt = (nint)(((int)center.Y << 16) | (int)center.X);
                        SendMessage(window.Handle, 0x201, 1, centerAt);
                        Press(ApplicationHotkeys.AddId, 0x41);
                        SendMessage(window.Handle, 0x202, 0, centerAt);
                        Check(!window.Capture && !window.Controls.OfType<ScreenNameEditor>().Any() && host.ActiveSubmenu == ConfigurationSubmenu.AddScreen, "Opening a submenu finishes an existing mouse gesture without opening a hidden name editor");
                        Press(ApplicationHotkeys.CancelSubmenuId, 0x1b);
                        Press(ApplicationHotkeys.BackgroundId, 0x42);
                        Check(host.ActiveSubmenu == ConfigurationSubmenu.Background && host.HudMessage == ConfigurationSubmenuChoices.Text(ConfigurationSubmenu.Background), "B on transparent opens only B/W/Esc");
                        BlockedActions(); Press(ApplicationHotkeys.AddId, 0x41);
                        Check(host.ActiveSubmenu == ConfigurationSubmenu.Background && Read().ScreensFor(target).Single() == before, "Unrelated keys cannot replace the background submenu or change the selected screen");
                        Press(ApplicationHotkeys.CancelSubmenuId, 0x1b);
                        Check(host.ActiveSubmenu == ConfigurationSubmenu.None && Read().DisplayBackgrounds[target].IsTransparent, "Cancel leaves the transparent background unchanged");
                        Press(ApplicationHotkeys.BackgroundId, 0x42); Press(ApplicationHotkeys.WhiteBackgroundId, 0x57);
                        await Until(() => !Read().DisplayBackgrounds[target].IsTransparent && Read().DisplayBackgrounds[target].BackgroundColor == "#FFFFFF" && host.HudMessage.Contains(": white"), "B,W saves white and exits the submenu");
                        Check(host.ActiveSubmenu == ConfigurationSubmenu.None, "Normal keyboard routing returns after a background choice");
                        Press(ApplicationHotkeys.BackgroundId, 0x42);
                        await Until(() => Read().DisplayBackgrounds[target].IsTransparent && host.HudMessage.Contains(": transparent"), "B on solid white makes it transparent immediately");
                        Press(ApplicationHotkeys.BackgroundId, 0x42); Press(ApplicationHotkeys.BackgroundId, 0x42);
                        await Until(() => !Read().DisplayBackgrounds[target].IsTransparent && Read().DisplayBackgrounds[target].BackgroundColor == "#000000" && host.HudMessage.Contains(": black"), "B,B saves black and exits the submenu");
                        Press(ApplicationHotkeys.AddId, 0x41); Press(ApplicationHotkeys.MatId, 0x4d);
                        await Until(() => Read().ScreensFor(target).Count == 2 && host.SelectedScreen?.ScreenId != "base", "A,M creates a mat and restores normal controls");
                        Check(host.ActiveSubmenu == ConfigurationSubmenu.None, "A screen-type choice consumes the Add submenu");
                        Press(ApplicationHotkeys.MatId, 0x4d);
                        await Until(() => Read().ScreensFor(target).Last().IsNegative, "M works normally again after completing Add");
                        var count = Read().ScreensFor(target).Count;
                        using (var cancel = new Forms.Timer { Interval = 40 })
                        {
                            cancel.Tick += (_, _) =>
                            {
                                var dialog = Forms.Application.OpenForms.OfType<VideoScreenDialog>().FirstOrDefault();
                                if (dialog is null) return;
                                cancel.Stop(); dialog.DialogResult = Forms.DialogResult.Cancel;
                            };
                            cancel.Start(); Press(ApplicationHotkeys.AddId, 0x41); Press(ApplicationHotkeys.VideoId, 0x56);
                        }
                        await Task.Delay(100);
                        Check(host.ActiveSubmenu == ConfigurationSubmenu.None && Read().ScreensFor(target).Count == count, "Canceling video creation does not leave a stale Add submenu");
                        Press(ApplicationHotkeys.AddId, 0x41);
                        var store = new PointConfigurationStore(path);
                        await store.SetConfigurationModeAsync(false, CancellationToken.None);
                        await Until(() => host.ActiveSubmenu == ConfigurationSubmenu.None && host.DisplayWindows.All(w => w.Submenu == ConfigurationSubmenu.None) && !notice.Visible, "Remote configuration-mode exit dismisses the pending submenu");
                        ExperienceOptions Read() { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var reader = new StreamReader(stream); return ExperienceOptions.Parse(reader.ReadToEnd()); }
                    }
                    done.TrySetResult();
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { Environment.SetEnvironmentVariable(LocalTestNetwork.Variable, previousNetwork); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(60)); }
        finally { await Task.Run(() => thread.Join(10000)); }
    }
    private static void Check(bool condition, string text) { if (!condition) throw new Exception(text); Console.WriteLine("PASS " + text); }
    private static async Task Until(Func<bool> ready, string text)
    {
        var deadline = Environment.TickCount64 + 10000;
        while (true) { try { if (ready()) break; } catch (IOException) { } if (Environment.TickCount64 >= deadline) throw new TimeoutException(text); await Task.Delay(40); }
        Check(true, text);
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint w, nint l);
}
