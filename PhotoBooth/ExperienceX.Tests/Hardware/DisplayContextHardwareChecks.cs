using ExperienceX;
using PhotoBooth.Networking;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

internal static class DisplayContextHardwareChecks
{
    internal static async Task Run()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                ExperienceHost? host = null;
                Exception? failure = null;
                var previousNetwork = Environment.GetEnvironmentVariable(LocalTestNetwork.Variable);
                var pointer = Forms.Cursor.Position;
                var root = Path.Combine(Path.GetTempPath(), "ExperienceX-display-context-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Environment.SetEnvironmentVariable(LocalTestNetwork.Variable, "127.0.0.3");
                    Directory.CreateDirectory(root);
                    var displays = Forms.Screen.AllScreens;
                    var target = displays.FirstOrDefault(d => !d.Primary) ?? displays.Single();
                    // On a single-display machine, an unavailable default exposes the same fallback bug.
                    var defaultDisplay = displays.Length > 1 ? Forms.Screen.PrimaryScreen!.DeviceName : "DISPLAY-UNAVAILABLE-TEST";
                    int Port() { using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)udp.Client.LocalEndPoint!).Port; }
                    var commands = Port(); var events = Port(); while (events == commands) events = Port();
                    var options = new ExperienceOptions
                    {
                        DeveloperMode = true, ConfigurationMode = true, DefaultDisplayName = defaultDisplay,
                        LocalController = new() { Enabled = false }, VideoDirectory = root, AudioDirectory = root,
                        UdpCommandsPort = commands, UdpEventsPort = events,
                        DisplayBackgrounds = displays.ToDictionary(d => d.DeviceName, _ => new DisplayBackgroundOptions { IsTransparent = true }),
                        DisplayScreens = displays.ToDictionary(d => d.DeviceName, _ => new List<ScreenOptions>())
                    };
                    var path = Path.Combine(root, "appsettings.json"); File.WriteAllText(path, JsonSerializer.Serialize(options));
                    host = new(dispatcher, path, () => true); host.Start();
                    var window = host.DisplayWindows.Single(w => w.DisplayName == target.DeviceName);
                    await Until(() => window.Visible && window.State == RendererState.Idle, "Non-default display starts with no screens");
                    Click(window, .05, .75);
                    Check(host.SelectedScreen is null && host.SelectedDisplayName == target.DeviceName,
                        "An empty display's background selects the display without requiring an existing screen");
                    void Press(int id, int key) => SendMessage(host.HotkeyWindowHandle, 0x312, id, (nint)(key << 16));
                    Press(ApplicationHotkeys.AddId, 0x41);
                    // Cursor movement after opening Add must not retarget creation.
                    Forms.Cursor.Position = Forms.Screen.PrimaryScreen!.WorkingArea.Location;
                    Press(ApplicationHotkeys.MatId, 0x4d);
                    await Until(() => Read().ScreensFor(target.DeviceName).Count == 1 && host.SelectedScreen?.ScreenId == Read().ScreensFor(target.DeviceName).Single().Id,
                        "A,M saves and selects the new mat on the background-selected display");
                    Check(Read().ScreensFor(target.DeviceName).Single().IsMat, "Created item is a mat");
                    Click(window, .05, .75);
                    Check(host.SelectedScreen is null && host.SelectedDisplayName == target.DeviceName, "Blank background retains creation context after deselecting the mat");
                    Exception? dialogFailure = null; bool accepted = false;
                    using (var accept = new Forms.Timer { Interval = 25 })
                    {
                        accept.Tick += (_, _) =>
                        {
                            var dialog = Forms.Application.OpenForms.OfType<ScreenPropertiesDialog>().FirstOrDefault();
                            if (dialog is null) return;
                            accept.Stop();
                            try
                            {
                                Check(dialog.Owner == window, "New-video dialog belongs to the selected display window");
                                if (dialog.AcceptButton is not Forms.Button button) throw new Exception("Create button missing.");
                                button.PerformClick(); accepted = true;
                            }
                            catch (Exception ex) { dialogFailure = ex; dialog.DialogResult = Forms.DialogResult.Cancel; }
                        };
                        accept.Start(); Press(ApplicationHotkeys.AddId, 0x41); Press(ApplicationHotkeys.VideoId, 0x56);
                    }
                    if (dialogFailure is not null) throw dialogFailure;
                    Check(accepted, "Video creation dialog accepted");
                    await Until(() => Read().ScreensFor(target.DeviceName).Count == 2 && host.SelectedScreen?.ScreenId == Read().ScreensFor(target.DeviceName).Single(s => s.IsVideo).Id,
                        "A,V saves and selects the new video on the background-selected display");
                    Check(Read().DisplayScreens.Where(d => d.Key != target.DeviceName).All(d => d.Value.Count == 0), "Other displays receive no new items");
                    Console.WriteLine($"PASS creation context: selected {target.DeviceName}, configured default {defaultDisplay}; mat and video use the selected background.");
                    ExperienceOptions Read() { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var reader = new StreamReader(stream); return ExperienceOptions.Parse(reader.ReadToEnd()); }
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    try { if (host is not null) await host.DisposeAsync(); }
                    catch (Exception ex) { failure ??= ex; }
                    Environment.SetEnvironmentVariable(LocalTestNetwork.Variable, previousNetwork); Forms.Cursor.Position = pointer;
                    Console.WriteLine("Display-context evidence: " + root);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    if (failure is null) done.TrySetResult(); else done.TrySetException(failure);
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(45));
        if (!thread.Join(TimeSpan.FromSeconds(5))) throw new Exception("Display-context dispatcher did not stop.");
    }
    private static void Click(MainWindow window, double x, double y)
    {
        var position = (nint)(((int)(y * (window.ClientSize.Height - 1)) << 16) | (int)(x * (window.ClientSize.Width - 1)));
        SendMessage(window.Handle, 0x201, 1, position); SendMessage(window.Handle, 0x202, 0, position);
    }
    private static void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS " + text); }
    private static async Task Until(Func<bool> ready, string text)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!ready()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException(text); await Task.Delay(25); }
        Console.WriteLine("PASS " + text);
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint w, nint l);
}
