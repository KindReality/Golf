using System.Text.Json;
using ExperienceX;
using Forms = System.Windows.Forms;

internal static class ScreenLockChecks
{
    internal static async Task Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-screen-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "appsettings.json");
            var points = new PointOptions { Enabled = true, TopLeft = new(.1, .1), TopRight = new(.9, .1), BottomLeft = new(.1, .9), BottomRight = new(.9, .9) };
            var options = new ExperienceOptions
            {
                ConfigurationMode = true,
                DisplayScreens = new()
                {
                    ["display"] = [new() { Id = "video", Name = "video", Points = points }, new() { Id = "mat", Name = "mat", Type = "mat", Points = points }],
                    ["visual-display"] = [new() { Id = "visual", Name = "gforce", Type = "visualization", IsLocked = true }]
                }
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(options));
            var store = new PointConfigurationStore(path);
            PointCalibration? calibration = null;
            async Task Reload()
            {
                var before = options; options = ExperienceOptions.Parse(await File.ReadAllTextAsync(path));
                calibration?.ConfigurationChanged(options);
                if (calibration is not null) await calibration.ConfigurationAppliedAsync(before, options);
            }
            ScreenOptions Video() => options.ScreensFor("display").Single(s => s.Id == "video");
            ScreenOptions Mat() => options.ScreensFor("display").Single(s => s.Id == "mat");
            await store.ToggleScreenLockAsync("display", "video"); await Reload();
            Check(Video().IsLocked && Video().Points == points && !Mat().IsLocked, "Lock is persisted per item without changing its geometry or other items");
            var unchanged = await File.ReadAllBytesAsync(path);
            async Task Blocked(string name, Func<Task> edit)
            {
                try { await edit(); throw new Exception(name + " was not blocked"); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("locked")) { }
                Check(unchanged.SequenceEqual(await File.ReadAllBytesAsync(path)), name + " leaves the locked configuration unchanged");
            }
            await Blocked("Point save", () => store.SaveAsync("display", points, PointEditing.Move(points, 0, .2, .2), "video"));
            await Blocked("Rename", () => store.RenameScreenAsync("display", "video", "video", "renamed"));
            await Blocked("Media selection", () => store.SelectMediaAsync("display", Video(), "Effects/Bell.mp4"));
            await Blocked("Brightness", () => store.AdjustScreenBrightnessAsync("display", "video", -5));
            await Blocked("Chroma key", () => store.ToggleScreenTransparencyAsync("display", "video"));
            await Blocked("Video reset", () => store.ResetVideoAsync("display", Video()));
            await Blocked("Framing", () => store.SaveVideoFramingAsync("display", Video(), new() { Zoom = 2 }));
            await Blocked("Perspective", () => store.SetPerspectiveAsync("display", "video", null));
            await Blocked("Layer order", () => store.MoveScreenLayerAsync("display", "video", true));
            await Blocked("Deletion", () => store.DeleteScreenAsync("display", Video()));
            await Blocked("Background shortcut deleting a locked visualization", () => store.SetBackgroundAsync("visual-display", false, "#000000"));
            var video = new Surface("video"); var mat = new Surface("mat");
            calibration = new(() => options, () => new[] { video, mat }, store, Reload);
            await using (calibration)
            {
                Check(!calibration.Begin(video, .1, .1, 0) && calibration.SelectedScreen == video, "A locked point selects its item without starting a drag");
                Check(!calibration.BeginScreenDrag(video, .5, .5) && !calibration.BeginEdgeDrag(video, 0, .5, .1), "Locked interiors and edges cannot move");
                Check(!calibration.BeginPointSelection(video) && !calibration.PointSelectionMode, "A locked item cannot enter point-selection mode");
                foreach (var key in new[] { Forms.Keys.Delete, Forms.Keys.R, Forms.Keys.Left, Forms.Keys.J, Forms.Keys.OemCloseBrackets }) calibration.Key(video, key);
                await calibration.ResetAsync(video); await calibration.InsertPointAsync(video, 0, new(.5, .1)); await calibration.UndoAsync(video);
                Check(!await calibration.DeleteSelectedAsync() && unchanged.SequenceEqual(await File.ReadAllBytesAsync(path)), "Locked keyboard actions, reset, insertion, undo and deletion are inert");
                calibration.Key(video, Forms.Keys.Tab);
                Check(calibration.SelectedScreen == mat, "Tab can navigate away from a locked item");
                Check(calibration.Begin(mat, .1, .1, 0), "An unlocked item remains editable beside a locked one");
                calibration.Move(mat, .2, .2); await calibration.FinishAsync(mat);
                Check(Video().Points == points && Mat().Points.TopLeft == new ScreenPoint(.2, .2), "Linked coincident points never move a locked item indirectly");
                calibration.Begin(mat, .2, .2, 0); calibration.Move(mat, .3, .3);
                await store.ToggleScreenLockAsync("display", "mat"); await Reload();
                Check(!calibration.Editing && !await calibration.FinishAsync(mat) && Mat().Points.TopLeft == new ScreenPoint(.2, .2), "A remote lock cancels an unfinished point movement");
                unchanged = await File.ReadAllBytesAsync(path);
                await Blocked("Mat mode", () => store.ToggleMatNegativeAsync("display", "mat"));
                await store.ToggleScreenLockAsync("display", "video"); await Reload();
                Check(!Video().IsLocked && calibration.Begin(video, .1, .1, 0), "Unlocking restores normal editing");
                calibration.Cancel("Test complete");
            }
            // Frame gestures must not preview a locked item even before persistence is attempted.
            var previews = 0;
            await using (var framing = new VideoFramingEditor((_, _) => previews++, (_, _, _) => Task.CompletedTask, (_, _) => { }))
            {
                var locked = Video() with { IsLocked = true };
                Check(!framing.Wheel(locked, .5, .5, 120) && !framing.BeginPan(locked, .5, .5) &&
                    !framing.BeginZoom(locked, .5, .5, 1920, 1080) && !framing.BeginRotate(locked, .5, .5, 1920, 1080) && previews == 0,
                    "Locked media cannot preview pan, zoom or rotation");
            }
            var lockedText = ScreenActionHint.TextFor("locked");
            Check(lockedText.Contains("L: unlock") && !lockedText.Contains("Delete") && !lockedText.Contains("move"), "The locked HUD only offers unlocking and selection");
            var lockedOptions = options with { DisplayScreens = new() { ["display"] = [Video() with { IsLocked = true }] } };
            Check(ScreenSelection.VideoOutputs(new() { Experience = "video", Value = "test.mp4", Screen = "*" }, lockedOptions, ["display"], "display").Count == 1,
                "The editing lock does not exclude the item from playback commands");
        }
        finally { Directory.Delete(folder, true); }
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private sealed class Surface(string id) : IPointSurface
    {
        public string DisplayName => "display"; public string ScreenId => id; public int PixelWidth => 1920; public int PixelHeight => 1080;
        public void ShowCalibration(int point) { } public void FadeCalibration() { } public void FlashCalibration(int point, bool success) { }
        public void FlashCalibrationPoints(int mask) { } public void PreviewPoints(PointOptions? points) { } public void ReleaseCalibrationCapture() { }
        public Task<bool> ConfirmCalibrationAsync(PointOptions expected) => Task.FromResult(true);
    }
}
