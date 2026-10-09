using System.Runtime.InteropServices;
using ExperienceX;

internal static class ConfigurationHotkeyChecks
{
    public static async Task Run()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                int presses = 0, closes = 0, backgrounds = 0;
                using var first = new ApplicationHotkeys(() => presses++, () => closes++, () => backgrounds++);
                using var second = new ApplicationHotkeys(() => presses += 10, () => closes += 10, () => backgrounds += 10);
                void Check(bool condition, string message)
                {
                    if (!condition) throw new Exception(message);
                }
                void Send(ApplicationHotkeys owner, int id, uint key, uint modifiers = 0) =>
                    SendMessage(owner.Handle, 0x0312, id, (nint)((key << 16) | modifiers));

                Check(first.Register().Length == 0 && first.Register().Length == 0, "Entry chord registration failed.");
                Check(second.Register().SequenceEqual(["Ctrl+Shift+Alt+C"]), "Entry chord conflict was not detected.");
                Send(first, ApplicationHotkeys.ConfigurationId, 0x43);
                Send(first, ApplicationHotkeys.ConfigurationId, 0x43, 3);
                Send(first, ApplicationHotkeys.CloseId, 0x58);
                Send(first, ApplicationHotkeys.BackgroundId, 0x54);
                Check(presses == 0 && closes == 0 && backgrounds == 0, "Letters or incomplete entry chord were active outside configuration mode.");
                Send(first, ApplicationHotkeys.ConfigurationId, 0x43, 7);
                Check(presses == 1, "Ctrl+Shift+Alt+C did not enable configuration.");

                var lettersMissing = first.Register(true);
                Check(lettersMissing.Length == 0, "Configuration letter registration failed: " + string.Join(", ", lettersMissing));
                Check(second.Register(true).Length == 3, "Configuration letter conflicts were not detected.");
                Send(first, ApplicationHotkeys.ConfigurationId, 0x43, 7);
                Send(first, ApplicationHotkeys.ConfigurationId, 0x4B);
                Check(presses == 1, "Stale entry message or incorrect key dispatched after changing mode.");
                Send(first, ApplicationHotkeys.ConfigurationId, 0x43);
                Send(first, ApplicationHotkeys.CloseId, 0x58);
                Send(first, ApplicationHotkeys.BackgroundId, 0x54);
                Check(presses == 2 && closes == 1 && backgrounds == 1, "Single-letter callbacks were not independent.");

                Check(first.Register(false).Length == 0, "Entry chord did not return when configuration was disabled.");
                Send(first, ApplicationHotkeys.ConfigurationId, 0x43);
                Send(first, ApplicationHotkeys.CloseId, 0x58);
                Send(first, ApplicationHotkeys.BackgroundId, 0x54);
                Check(presses == 2 && closes == 1 && backgrounds == 1, "Queued configuration letters dispatched after disabling mode.");
                Check(second.Register(true).Length == 0, "Letters remained claimed after disabling configuration.");
                second.Dispose();
                second.Dispose();
                Check(first.Register(true).Length == 0, "Letters remained claimed after shutdown.");
                first.Dispose();

                var blockerWindow = new System.Windows.Forms.NativeWindow();
                blockerWindow.CreateHandle(new System.Windows.Forms.CreateParams { Parent = new nint(-3) });
                const int blocker = 0x5943;
                Check(RegisterHotKey(blockerWindow.Handle, blocker, ApplicationHotkeys.LetterModifiers, 0x43), "Cannot reserve C for partial-conflict test.");
                try
                {
                    using var partial = new ApplicationHotkeys(() => presses++, () => closes++, () => backgrounds++);
                    Check(partial.Register(true).SequenceEqual(["C"]), "Configuration conflict blocked other available letters.");
                    Send(partial, ApplicationHotkeys.ConfigurationId, 0x43);
                    Send(partial, ApplicationHotkeys.CloseId, 0x58);
                    Send(partial, ApplicationHotkeys.BackgroundId, 0x54);
                    Check(presses == 2 && closes == 2 && backgrounds == 2, "Conflict isolation dispatched an unavailable key or blocked available keys.");
                }
                finally { UnregisterHotKey(blockerWindow.Handle, blocker); blockerWindow.DestroyHandle(); }
                Check(ApplicationHotkeys.Modifiers == 0x4007 && ApplicationHotkeys.LetterModifiers == 0x4000, "Shortcut modifiers or repeat suppression are incorrect.");
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await NativeDelivery();
        Console.WriteLine("Passed Ctrl+Shift+Alt+C entry, configuration-only C/X/T letters, mode transitions, stale-message rejection, conflicts and shutdown release.");
    }

    private static async Task NativeDelivery()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            ApplicationHotkeys? hotkeys = null;
            int presses = 0, closes = 0, backgrounds = 0, step = 0;
            void Register(bool mode)
            {
                var missing = hotkeys!.Register(mode);
                if (missing.Length != 0) throw new Exception("Native shortcut conflict: " + string.Join(", ", missing));
            }
            hotkeys = new(() => { presses++; Register(presses == 1); }, () => closes++, () => backgrounds++);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            timer.Tick += (_, _) =>
            {
                try
                {
                    switch (step++)
                    {
                        case 0: Inject(0x11, 0x10, 0x12, 0x43); break;
                        case 1:
                            if (presses != 1) throw new Exception("Real Ctrl+Shift+Alt+C did not reach ApplicationHotkeys on the production WPF dispatcher.");
                            Inject(0x58); break;
                        case 2:
                            if (closes != 1) throw new Exception("Real X did not reach the close callback.");
                            Inject(0x54); break;
                        case 3:
                            if (backgrounds != 1) throw new Exception("Real T did not reach the background callback.");
                            Inject(0x43); break;
                        default:
                            if (presses != 2) throw new Exception("Real C did not exit configuration mode.");
                            completion.TrySetResult();
                            timer.Stop();
                            dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Normal);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                    timer.Stop();
                    dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Normal);
                }
            };
            try { Register(false); timer.Start(); System.Windows.Threading.Dispatcher.Run(); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { timer.Stop(); hotkeys.Dispose(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static void Inject(params byte[] keys)
    {
        try { foreach (var key in keys) keybd_event(key, 0, 0, 0); }
        finally { foreach (var key in keys.Reverse()) keybd_event(key, 0, 2, 0); }
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
