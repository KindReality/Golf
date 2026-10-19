using ExperienceX;
using System.Runtime.InteropServices;

internal static class DisplayContextChecks
{
    internal static async Task Run()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                const string left = "DISPLAY-CONTEXT-LEFT", right = "DISPLAY-CONTEXT-RIGHT";
                var options = new ExperienceOptions
                {
                    ConfigurationMode = true,
                    DisplayScreens = new()
                    {
                        [left] = [new() { Id = "left-video", Points = ScreenOptions.CenteredPoints() }],
                        [right] = [new() { Id = "right-mat", Type = "mat", Points = ScreenOptions.CenteredPoints() }]
                    }
                };
                using var first = new MainWindow(left, options) { ClientSize = new(800, 600) };
                using var second = new MainWindow(right, options) { ClientSize = new(800, 600) };
                var calibration = new PointCalibration(() => options, () => first.Surfaces.Concat(second.Surfaces),
                    new PointConfigurationStore("unused-display-context.json"), () => Task.CompletedTask);
                first.AttachCalibration(calibration); second.AttachCalibration(calibration);
                Check(calibration.SelectedDisplayName is null, "No selection preserves the default-display fallback");
                calibration.SelectScreen(first.Surface("left-video"));
                Check(calibration.SelectedDisplayName == left, "Selecting an item selects its display context");
                Click(second, 50, 450);
                Check(calibration.SelectedScreen is null && calibration.SelectedDisplayName == right,
                    "Clicking another display's background clears the old screen and selects the clicked display");
                calibration.SelectScreen(second.Surface("right-mat"));
                Click(first, 50, 450);
                Check(calibration.SelectedScreen is null && calibration.SelectedDisplayName == left,
                    "Background selection can switch back from a selected mat");
                calibration.Begin(second.Surface("right-mat"), 1.0 / 3, 1.0 / 3, 0);
                Check(calibration.SelectedDisplayName == right && calibration.SelectedScreen?.ScreenId == "right-mat",
                    "Selecting a point also updates the display context");
                calibration.SelectScreen(null);
                Check(calibration.SelectedDisplayName == right, "Clearing an item retains its display context");
                options = options with { ConfigurationMode = false };
                first.UpdateOptions(options); second.UpdateOptions(options);
                Click(first, 50, 450);
                Check(calibration.SelectedDisplayName == right, "Production clicks do not change configuration selection");
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (!thread.Join(TimeSpan.FromSeconds(2))) throw new Exception("Display-context test did not stop.");
    }
    private static void Click(MainWindow window, int x, int y)
    {
        var position = (nint)((y << 16) | x);
        SendMessage(window.Handle, 0x201, 1, position);
        SendMessage(window.Handle, 0x202, 0, position);
    }
    private static void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS " + text); }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint w, nint l);
}
