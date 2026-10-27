using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using ExperienceX;
using Forms = System.Windows.Forms;

internal static class PointSelectionChecks
{
    internal static async Task Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-point-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { await Model(Path.Combine(folder, "model.json")); await Native(Path.Combine(folder, "native.json")); }
        finally { Directory.Delete(folder, true); }
    }
    private static ExperienceOptions Settings() => new()
    {
        ConfigurationMode = true,
        DisplayScreens = new()
        {
            ["display"] = [new() { Id = "mat", Name = "mask", Type = "mat", Points = Shape() },
                new() { Id = "video", Name = "video", Points = Shape() }],
            ["other"] = [new() { Id = "other", Name = "other" }]
        }
    };
    private static PointOptions Shape() => new() { Enabled = true, TopLeft = new(.1, .1), TopRight = new(.9, .1), BottomLeft = new(.1, .9), BottomRight = new(.9, .9) };
    private static async Task Model(string path)
    {
        var settings = Settings();
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(settings));
        var mat = new Surface("display", "mat"); var video = new Surface("display", "video"); var other = new Surface("other", "other");
        PointCalibration? calibration = null;
        async Task Reload() { var before = settings; settings = ExperienceOptions.Parse(await File.ReadAllTextAsync(path)); calibration!.ConfigurationChanged(settings); await calibration.ConfigurationAppliedAsync(before, settings); }
        calibration = new(() => settings, () => new[] { mat, video, other }, new(path), Reload);
        await using (calibration)
        {
            var initial = await File.ReadAllBytesAsync(path);
            calibration.Key(mat, Forms.Keys.Tab);
            Check(calibration.SelectedScreen == mat && !calibration.PointSelectionMode, "Tab starts item selection on the current display");
            calibration.Key(mat, Forms.Keys.Tab);
            Check(calibration.SelectedScreen == video, "Tab selects the next item in definition order");
            calibration.Key(video, Forms.Keys.Tab);
            Check(calibration.SelectedScreen == mat, "Item selection wraps without entering another display");
            calibration.Key(mat, Forms.Keys.Shift | Forms.Keys.Tab);
            Check(calibration.SelectedScreen == video, "Shift+Tab reverses item selection");
            Check(calibration.BeginPointSelection(video) && video.Point == 0 && video.Pointer == Shape().TopLeft, "Point mode selects the first point and moves the pointer there");
            foreach (var index in new[] { 1, 2, 3, 0 })
            {
                calibration.Key(video, Forms.Keys.Tab);
                Check(calibration.SelectedScreen == video && video.Point == index && video.Pointer == PointEditing.Points(Shape())[index], "Point Tab stays on the item and moves the pointer in UL/UR/BL/BR order: " + index);
            }
            calibration.Key(video, Forms.Keys.Shift | Forms.Keys.Tab);
            Check(video.Point == 3 && video.Pointer == Shape().BottomRight, "Shift+Tab reverses and wraps point selection");
            Check(initial.SequenceEqual(await File.ReadAllBytesAsync(path)), "Selecting items and points never changes the configuration");
            calibration.Begin(video, .9, .9, 3); calibration.Move(video, .85, .85);
            await calibration.FinishAsync(video);
            Check(calibration.PointSelectionMode && video.Point == 3 && settings.PointsFor("display", "video").BottomRight == new ScreenPoint(.85, .85), "Mouse release saves the point while retaining point mode");
            calibration.Key(video, Forms.Keys.Escape);
            Check(!calibration.PointSelectionMode && calibration.SelectedScreen == video && video.Point == -1, "Escape leaves point mode without deselecting the item");
            calibration.Key(video, Forms.Keys.Tab);
            Check(calibration.SelectedScreen == mat, "Tab resumes item selection after Escape");
            var polygon = new PointOptions { Enabled = true, Vertices = [new(.1, .1), new(.9, .1), new(.9, .9), new(.5, .7), new(.1, .9)] };
            settings = settings with { DisplayScreens = new() { ["display"] = [new() { Id = "mat", Name = "mask", Type = "mat", Points = polygon }] } };
            calibration.ConfigurationChanged(settings); calibration.BeginPointSelection(mat);
            foreach (var index in new[] { 1, 2, 3, 4, 0 })
            {
                calibration.Key(mat, Forms.Keys.Tab);
                Check(mat.Point == index && mat.Pointer == polygon.Vertices[index], "All polygon points can be selected: " + index);
            }
            calibration.SelectScreen(video);
            Check(!calibration.PointSelectionMode, "Selecting another item leaves point mode");
            calibration.BeginPointSelection(mat);
            settings = settings with { ConfigurationMode = false }; calibration.ConfigurationChanged(settings);
            Check(!calibration.PointSelectionMode && !calibration.Key(mat, Forms.Keys.Tab), "Leaving configuration mode disables item and point navigation");
        }
    }
    private static async Task Native(string path)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var settings = Settings(); await File.WriteAllTextAsync(path, JsonSerializer.Serialize(settings));
                    using var window = new MainWindow("display", settings) { ClientSize = new(800, 600) };
                    System.Drawing.Point pointer = default;
                    window.PointerPositionForTest = position => pointer = position;
                    PointCalibration? calibration = null;
                    async Task Reload() { var before = settings; settings = ExperienceOptions.Parse(await File.ReadAllTextAsync(path)); window.UpdateOptions(settings); calibration!.ConfigurationChanged(settings); await calibration.ConfigurationAppliedAsync(before, settings); }
                    calibration = new(() => settings, () => window.Surfaces, new(path), Reload);
                    await using (calibration)
                    {
                        window.AttachCalibration(calibration); _ = window.Handle;
                        var video = window.Surface("video"); calibration.SelectScreen(video);
                        var center = ScreenNameLabel.Center(Shape(), window.ClientSize.Width, window.ClientSize.Height);
                        void Mouse(int message, double x, double y, int buttons = 0) => SendMessage(window.Handle, (uint)message, buttons,
                            (nint)(((int)Math.Round(y * (window.ClientSize.Height - 1)) << 16) | (int)Math.Round(x * (window.ClientSize.Width - 1))));
                        void Key(Forms.Keys key) => SendMessage(window.Handle, 0x100, (nint)key, 0);
                        void DoubleClick(double x, double y)
                        {
                            Mouse(0x203, x, y, 1);
                            // Hidden test windows fail WinForms' WindowFromPoint click validation.
                            // Raise the same event explicitly without activating a desktop window.
                            typeof(MainWindow).GetMethod("OnMouseDoubleClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                                .Invoke(window, [new Forms.MouseEventArgs(Forms.MouseButtons.Left, 2,
                                    (int)Math.Round(x * (window.ClientSize.Width - 1)), (int)Math.Round(y * (window.ClientSize.Height - 1)), 0)]);
                            Mouse(0x202, x, y);
                        }
                        var cx = center.X / (window.ClientSize.Width - 1); var cy = center.Y / (window.ClientSize.Height - 1);
                        Mouse(0x201, cx, cy, 1); Mouse(0x202, cx, cy);
                        DoubleClick(cx, cy);
                        Check(calibration.PointSelectionMode && calibration.SelectedScreen == video && calibration.SelectedPoint(video) == 0,
                            "Double-clicking the name area enters first-point mode instead of opening rename");
                        Check(pointer == window.PointToScreen(new((int)Math.Round(.1 * (window.ClientSize.Width - 1)), (int)Math.Round(.1 * (window.ClientSize.Height - 1)))),
                            "The pointer destination uses the display window's screen coordinates");
                        await Task.Delay(Forms.SystemInformation.DoubleClickTime + 40);
                        Check(!window.Controls.OfType<ScreenNameEditor>().Any(), "A double-click cancels the delayed single-click name editor");
                        Mouse(0x201, .1, .1, 1);
                        Check(calibration.SelectedScreen == video && calibration.SelectedPoint(video) == 0, "One press grabs the selected point even when another item's point overlaps it");
                        Mouse(0x202, .1, .1);
                        Key(Forms.Keys.Tab);
                        Check(calibration.SelectedPoint(video) == 1 && calibration.SelectedScreen == video, "Native Tab cycles points after releasing the mouse");
                        Key(Forms.Keys.Escape); Key(Forms.Keys.Tab);
                        Check(!calibration.PointSelectionMode && calibration.SelectedScreen?.ScreenId == "mat", "Native Escape returns Tab to whole-item navigation");
                        // Edge insertion must retain precedence over the new double-click action.
                        DoubleClick(.5, .1);
                        await Until(() => settings.ScreensFor("display").Sum(s => s.Points.Vertices.Count) == 9 && !calibration.Busy);
                        Check(!calibration.PointSelectionMode, "Double-clicking an edge still inserts a point");
                        calibration.SelectScreen(video);
                        Mouse(0x201, cx, cy, 1); Mouse(0x202, cx, cy);
                        await Until(() => window.Controls.OfType<ScreenNameEditor>().Any());
                        Check(true, "A single click on the name still opens rename after the double-click interval");
                        var name = window.Controls.OfType<ScreenNameEditor>().Single(); name.Text = "Editing";
                        SendMessage(name.Handle, 0x100, (nint)Forms.Keys.Tab, 0);
                        Check(!window.Controls.OfType<ScreenNameEditor>().Any() && !calibration.PointSelectionMode, "Tab inside the name editor retains its commit behavior");
                    }
                    done.TrySetResult();
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    private static async Task Until(Func<bool> ready)
    {
        var end = Environment.TickCount64 + 4000;
        while (!ready()) { if (Environment.TickCount64 > end) throw new TimeoutException("Point selection did not update."); await Task.Delay(10); }
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private sealed class Surface(string display, string id) : IPointSurface
    {
        public string DisplayName => display; public string ScreenId => id; public int PixelWidth => 1920; public int PixelHeight => 1080;
        internal int Point = -1; internal ScreenPoint? Pointer;
        public void ShowCalibration(int point) => Point = point;
        public void MovePointerToPoint(ScreenPoint point) => Pointer = point;
        public void FadeCalibration() { } public void FlashCalibration(int point, bool success) { } public void FlashCalibrationPoints(int mask) { }
        public void PreviewPoints(PointOptions? points) { } public void ReleaseCalibrationCapture() { }
        public Task<bool> ConfirmCalibrationAsync(PointOptions expected) => Task.FromResult(true);
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint w, nint l);
}
