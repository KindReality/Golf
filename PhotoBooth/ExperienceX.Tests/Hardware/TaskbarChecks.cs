using ExperienceX;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

internal static class TaskbarChecks
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
                try
                {
                    var display = Forms.Screen.PrimaryScreen ?? throw new Exception("A display is required.");
                    foreach (var development in new[] { false, true })
                    {
                        var options = new ExperienceOptions
                        {
                            DeveloperMode = development,
                            DisplayBackgrounds = new() { [display.DeviceName] = new() { IsTransparent = true } },
                            DisplayScreens = new() { [display.DeviceName] = [] }
                        };
                        using var window = new MainWindow(display.DeviceName, options);
                        try
                        {
                            window.Start();
                            var deadline = Environment.TickCount64 + 10000;
                            while (window.State == RendererState.Starting && Environment.TickCount64 < deadline) await Task.Delay(25);
                            Check(window.Visible && window.State == RendererState.Idle, "Experience starts with a visible idle display window");
                            Eligible(window);
                            Check(SendMessage(window.Handle, 0x7F, 1, 0) != 0, "Taskbar window exposes the application icon"); // WM_GETICON / ICON_BIG
                            var bounds = window.Bounds;
                            window.WindowState = Forms.FormWindowState.Minimized;
                            await Task.Delay(1250);
                            window.RefreshDisplay();
                            Check(IsIconic(window.Handle) && window.ShowInTaskbar, "Display polling and topmost watchdog leave the window minimized in the taskbar");
                            SendMessage(window.Handle, 0x112, 0xF120, 0); // WM_SYSCOMMAND / SC_RESTORE, as used by the taskbar
                            await Task.Delay(50);
                            Check(!IsIconic(window.Handle) && window.Visible && window.Bounds == bounds, "Taskbar restore returns Experience to its display bounds");
                            window.UpdateOptions(options with { ConfigurationMode = true });
                            Eligible(window);
                            Check((GetWindowLongPtr(window.Handle, -20).ToInt64() & 0x08000000) == 0, "Configuration mode allows window activation");
                            window.UpdateOptions(options);
                            Eligible(window);
                            Check((GetWindowLongPtr(window.Handle, -20).ToInt64() & 0x08000000) != 0, "Playback retains its no-activation behavior");
                        }
                        finally { await window.ShutdownWindowAsync(); }
                        Console.WriteLine($"Passed Experience {(development ? "development" : "production")} taskbar icon, minimize/restore, display polling and configuration transitions.");
                    }
                    done.TrySetResult();
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static void Eligible(MainWindow window)
    {
        var style = GetWindowLongPtr(window.Handle, -20).ToInt64();
        Check(window.ShowInTaskbar && (style & 0x00040000) != 0 && (style & 0x00000080) == 0 && GetWindow(window.Handle, 4) == 0,
            "Experience is an unowned taskbar application window, not a hidden tool window");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
}
