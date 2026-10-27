using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using Forms = System.Windows.Forms;

internal static class ScreenClickSelectionChecks
{
    internal static async Task Run()
    {
        long now = 0;
        var selection = new ScreenClickSelection(() => now);
        string[] stack = ["first", "mat", "last"];
        Check(selection.Pick(stack, null) == "first", "An initial overlap click selects the first item in layer order");
        now = 999;
        Check(selection.Pick(stack, "first") == "mat", "Single clicks less than one second apart cycle overlapping items");
        now = 1999;
        Check(selection.Pick(stack, "mat") == "mat", "A pause of exactly one second retains the selected item");
        now = 2100;
        Check(selection.Pick(stack, "mat") == "last", "Rapid cycling resumes from the retained item");
        now = 2200;
        Check(selection.Pick(stack, "first") == "first", "An external selection is retained rather than advancing the old cycle");
        now = 2300;
        Check(selection.Pick(["last", "first", "mat"], "first") == "first", "Layer reordering retains the selected item's identity");
        selection.Reset(); now = 2400;
        Check(selection.Pick(stack, "first") == "first", "Completing a drag ends the rapid-click sequence");
        Check(selection.Pick([], "first") is null && selection.Pick(["last"], "first") == "last", "Background clicks and non-overlapping items begin a fresh selection");
        await Native();
    }
    private static async Task Native()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-overlap-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var shape = new PointOptions { Enabled = true, TopLeft = new(.1, .1), TopRight = new(.9, .1), BottomLeft = new(.1, .9), BottomRight = new(.9, .9) };
                    var options = new ExperienceOptions { ConfigurationMode = true, DisplayScreens = new() { ["display"] =
                        [new() { Id = "first", Name = "first", Points = shape, IsLocked = true },
                         new() { Id = "mat", Name = "mat", Type = "mat", Points = shape },
                         new() { Id = "last", Name = "last", Points = shape }] } };
                    var path = Path.Combine(folder, "appsettings.json"); await File.WriteAllTextAsync(path, JsonSerializer.Serialize(options));
                    using var window = new MainWindow("display", options) { ClientSize = new(800, 600) };
                    PointCalibration? calibration = null;
                    async Task Reload() { options = ExperienceOptions.Parse(await File.ReadAllTextAsync(path)); window.UpdateOptions(options); calibration!.ConfigurationChanged(options); }
                    calibration = new(() => options, () => window.Surfaces, new(path), Reload);
                    await using (calibration)
                    {
                        window.AttachCalibration(calibration); _ = window.Handle;
                        void Mouse(uint message, double x, double y, int buttons = 0) => SendMessage(window.Handle, message, buttons,
                            (nint)(((int)Math.Round(y * (window.ClientSize.Height - 1)) << 16) | (int)Math.Round(x * (window.ClientSize.Width - 1))));
                        void Click() { Mouse(0x201, .35, .4, 1); Mouse(0x202, .35, .4); }
                        var original = await File.ReadAllBytesAsync(path);
                        Click(); Check(calibration.SelectedScreen?.ScreenId == "first", "A body click can select a locked overlapping item");
                        Click(); Check(calibration.SelectedScreen?.ScreenId == "mat", "A rapid next click selects the overlapping mat");
                        Click(); Check(calibration.SelectedScreen?.ScreenId == "last", "Another rapid click selects the next video");
                        Check(original.SequenceEqual(await File.ReadAllBytesAsync(path)), "Rapid selection clicks do not save geometry");
                        await Task.Delay(1050);
                        Mouse(0x201, .35, .4, 1);
                        Check(calibration.SelectedScreen?.ScreenId == "last" && window.Capture && calibration.Editing,
                            "After a one-second pause, holding the mouse begins dragging the selected video");
                        Mouse(0x200, .4, .45, 1); Mouse(0x202, .4, .45);
                        var end = Environment.TickCount64 + 4000;
                        while (calibration.Busy || options.ScreensFor("display")[2].Points == shape)
                        { if (Environment.TickCount64 > end) throw new TimeoutException("Overlap drag did not save."); await Task.Delay(10); }
                        Check(options.ScreensFor("display")[0].Points == shape && options.ScreensFor("display")[1].Points == shape,
                            "The delayed drag moves only the selected item, preserving the overlapping video and mat");
                        Click(); Check(calibration.SelectedScreen?.ScreenId == "last", "The first press after dragging retains that item");
                        Click(); Check(calibration.SelectedScreen?.ScreenId == "first" && !window.Capture, "Rapid cycling resumes and still respects a locked item's editing lock");
                    }
                    done.TrySetResult();
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(12)); }
        finally { Directory.Delete(folder, true); }
    }
    private static void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS " + text); }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint w, nint l);
}
