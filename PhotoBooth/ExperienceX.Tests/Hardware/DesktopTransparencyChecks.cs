using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using ExperienceX;
using Forms = System.Windows.Forms;

internal static class DesktopTransparencyChecks
{
    public static async Task Run()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var display = Forms.Screen.PrimaryScreen ?? throw new Exception("A display is required.");
            var backingColor = Color.FromArgb(183, 41, 137);
            using var backing = new Forms.Form
            {
                FormBorderStyle = Forms.FormBorderStyle.None,
                StartPosition = Forms.FormStartPosition.Manual,
                Bounds = display.Bounds,
                BackColor = backingColor,
                ShowInTaskbar = false,
                TopMost = true
            };
            var options = new ExperienceOptions
            {
                DisplayBackgrounds = new() { [display.DeviceName] = new() { IsTransparent = true } },
                DisplayScreens = new() { [display.DeviceName] = [new ScreenOptions()] },
                CalibrationGridEnabled = false
            };
            using var window = new MainWindow(display.DeviceName, options);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            int phase = -1, ticks = 0;
            void End(Exception? error)
            {
                timer.Stop();
                window.ShutdownWindow();
                backing.Close();
                if (error is null) completion.TrySetResult();
                else completion.TrySetException(error);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
            void CheckPixel(Color expected, string operation)
            {
                SetWindowPos(phase < 0 ? backing.Handle : window.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x13);
                DwmFlush();
                using var pixel = new Bitmap(1, 1);
                using (var graphics = Graphics.FromImage(pixel))
                    graphics.CopyFromScreen(display.Bounds.Left + display.Bounds.Width / 3,
                        display.Bounds.Top + display.Bounds.Height / 3, 0, 0, new Size(1, 1));
                var actual = pixel.GetPixel(0, 0);
                Console.WriteLine($"{operation}: desktop RGB={actual.R},{actual.G},{actual.B}; expected={expected.R},{expected.G},{expected.B}");
                if (Math.Abs(actual.R - expected.R) > 5 || Math.Abs(actual.G - expected.G) > 5 || Math.Abs(actual.B - expected.B) > 5)
                    throw new Exception($"{operation}: underlying desktop composition is incorrect ({actual}, expected {expected}).");
            }
            timer.Tick += (_, _) =>
            {
                try
                {
                    if (++ticks < 5) return;
                    ticks = 0;
                    if (phase < 0)
                    {
                        CheckPixel(backingColor, "Backing window baseline");
                        window.Start();
                        Console.WriteLine($"Native window exstyle: 0x{GetWindowLongPtr(window.Handle, -20).ToInt64():X}");
                        phase = 0;
                        return;
                    }
                    if (window.State != RendererState.Idle) throw new Exception($"Renderer is {window.State}.");
                    if (phase == 0)
                    {
                        CheckPixel(backingColor, "Transparent startup");
                        options = options with { DisplayBackgrounds = new() { [display.DeviceName] = new() { IsTransparent = false } } };
                    }
                    else if (phase == 1)
                    {
                        CheckPixel(Color.Black, "Opaque background");
                        options = options with { DisplayBackgrounds = new() { [display.DeviceName] = new() { IsTransparent = true } }, ConfigurationMode = true };
                    }
                    else if (phase == 2)
                    {
                        var faintBlue = Color.FromArgb((int)Math.Round(backingColor.R * .96 + 255 * .05 * .04),
                            (int)Math.Round(backingColor.G * .96 + 255 * .55 * .04), (int)Math.Round(backingColor.B * .96 + 255 * .04));
                        CheckPixel(faintBlue, "Transparent background with faint configuration screen tint");
                        options = options with { ConfigurationMode = false };
                    }
                    else if (phase == 3)
                    {
                        CheckPixel(backingColor, "Transparent background after configuration mode exits");
                        options = options with { DisplayBackgrounds = new() { [display.DeviceName] = new() { BackgroundColor = "#FFFFFF", IsTransparent = false } } };
                    }
                    else if (phase == 4)
                    {
                        CheckPixel(Color.White, "White background selection");
                        options = options with { DisplayBackgrounds = new() { [display.DeviceName] = new() { BackgroundColor = "#FFFFFF", IsTransparent = true } } };
                    }
                    else
                    {
                        CheckPixel(backingColor, "White background changed to transparent");
                        End(null);
                        return;
                    }
                    phase++;
                    window.UpdateOptions(options);
                }
                catch (Exception ex) { End(ex); }
            };
            try
            {
                backing.Show();
                timer.Start();
                Dispatcher.Run();
            }
            catch (Exception ex) { End(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Console.WriteLine("Passed real desktop composition through the production display window at startup and after background/configuration toggles.");
    }

    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
}
