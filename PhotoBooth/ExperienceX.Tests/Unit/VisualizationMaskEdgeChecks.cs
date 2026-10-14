using System.Text.Json;
using ExperienceX;

internal static class VisualizationMaskEdgeChecks
{
    internal static async Task Run()
    {
        Check(PolygonGeometry.OutsideTriangles(new()).Length == 0, "Full-display visualization mask has no opaque geometry");
        var concave = new PointOptions { Vertices = [new(.1, .1), new(.9, .1), new(.9, .9), new(.5, .5), new(.1, .9)] };
        var inside = PolygonGeometry.Triangles(concave); var outside = PolygonGeometry.OutsideTriangles(concave);
        double Area(ScreenPoint[] points) => Enumerable.Range(0, points.Length / 3).Sum(i => Math.Abs(PolygonGeometry.Cross(points[i * 3], points[i * 3 + 1], points[i * 3 + 2])) / 2);
        Check(Math.Abs(Area(inside) + Area(outside) - 1) < 1e-9, "Concave inverse geometry covers exactly the complement of its polygon");
        for (var y = 0; y < 31; y++) for (var x = 0; x < 31; x++)
        {
            var p = new ScreenPoint((x + .37) / 31, (y + .29) / 31);
            var covered = Enumerable.Range(0, outside.Length / 3).Any(i => PolygonGeometry.Cross(outside[i * 3], outside[i * 3 + 1], p) >= 0 && PolygonGeometry.Cross(outside[i * 3 + 1], outside[i * 3 + 2], p) >= 0 && PolygonGeometry.Cross(outside[i * 3 + 2], outside[i * 3], p) >= 0);
            Check(covered != ScreenSelection.Contains(concave, p.X, p.Y), "Inverse geometry overlaps its hole or leaves an outside gap");
        }
        var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-mask-edge-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "appsettings.json");
            var video = new Surface("video"); var mat = new Surface("mat"); var visual = new Surface("visual");
            var videoPoints = new PointOptions { Enabled = true, TopLeft = new(.1, .1), TopRight = new(.7, .1), BottomRight = new(.7, .9), BottomLeft = new(.1, .9) };
            var matPoints = videoPoints with { BottomRight = new(.7, .6), BottomLeft = new(.1, .6) };
            var settings = new ExperienceOptions { ConfigurationMode = true, DisplayScreens = new() { ["display"] = [new() { Id = "visual", Name = "gforce", Type = "visualization" }, new() { Id = "video", Points = videoPoints }, new() { Id = "mat", Name = "mat", Type = "mat", Points = matPoints }] } };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(settings));
            var store = new PointConfigurationStore(path); PointCalibration? calibration = null;
            async Task Reload() { var before = settings; settings = ExperienceOptions.Parse(await File.ReadAllTextAsync(path)); calibration?.ConfigurationChanged(settings); if (calibration is not null) await calibration.ConfigurationAppliedAsync(before, settings); }
            PointOptions Shape(Surface s) => s.Preview ?? settings.PointsFor("display", s.ScreenId);
            calibration = new(() => settings, () => new[] { video, mat, visual }, store, Reload);
            await using (calibration)
            {
                Check(ScreenSelection.EditablePoints(settings.ScreensFor("display"), "video").All(s => !s.IsVisualization), "The inverse mask has no ordinary point/edge hit target");
                Check(ScreenSelection.EditablePoints(settings.ScreensFor("display"), "visual").Select(s => s.Id).SequenceEqual(["visual"]), "Selecting G-Force exposes only its owned mask handles");
                Check(calibration.BeginEdgeDrag(video, 0, .4, .1), "Video edge drag begins"); calibration.Move(video, .45, .2);
                var moved = Shape(video);
                Check(Math.Abs((moved.TopRight.X - moved.TopLeft.X) - .6) < 1e-10 && moved.TopLeft.Y == moved.TopRight.Y && moved.BottomRight == videoPoints.BottomRight, "Edge endpoints translate without rotation or length change");
                Check(Shape(mat).TopLeft == moved.TopLeft && Shape(mat).TopRight == moved.TopRight && Shape(visual) == new PointOptions(), "Coincident endpoint groups follow together while the protected mask stays independent");
                Check(await calibration.FinishAsync(video), "Edge release atomically saves the changed screens");
                await calibration.UndoAsync(video, movementOnly: true);
                Check(settings.PointsFor("display", "video") == videoPoints && settings.PointsFor("display", "mat") == matPoints, "Ctrl+Z restores both linked endpoint groups");
                calibration.SelectScreen(visual); Check(calibration.Begin(visual, 0, 0, 0), "Selected visualization mask permits point movement"); calibration.Move(visual, .2, .2);
                Check(await calibration.FinishAsync(visual) && settings.PointsFor("display", "visual").TopLeft == new ScreenPoint(.2, .2), "Mask edits persist without moving the external fullscreen window");
                await calibration.InsertPointAsync(visual, 0, new(.6, .1));
                Check(settings.PointsFor("display", "visual").Vertices.Count == 5, "Visualization mask supports adding polygon points");
                var before = settings.PointsFor("display", "visual"); calibration.BeginEdgeDrag(visual, 0, .4, .15); calibration.Move(visual, .42, .18);
                Check(await calibration.FinishAsync(visual), "Mask edge movement is saved");
                await calibration.UndoAsync(visual, movementOnly: true); Check(settings.PointsFor("display", "visual") == before, "Mask edge movement supports undo");
                await calibration.ResetAsync(visual); Check(settings.PointsFor("display", "visual") == new PointOptions(), "Reset restores a fully open visualization mask");
            }
            var matrix = new Surface("matrix");
            const double w = 1919, h = 1079, angle = .3;
            ScreenPoint At(double u, double v) => new(.5 + (u * 400 * Math.Cos(angle) - v * 200 * Math.Sin(angle)) / w, .5 + (u * 400 * Math.Sin(angle) + v * 200 * Math.Cos(angle)) / h);
            var rectangle = new PointOptions { Vertices = [At(-.5, -.5), At(.5, -.5), At(.5, .5), At(-.5, .5)] };
            var drag = new PointSnapping(new() { [matrix] = rectangle }, new() { [matrix] = false }, (a, b) => a == b, new() { [matrix] = true });
            drag.MoveEdge(matrix, 0, .02, .03); LedGeometry.Validate(drag.Drafts[matrix], false);
            var edge = drag.Drafts[matrix]; Check(Math.Abs((edge.TopRight.X - edge.TopLeft.X) - (rectangle.TopRight.X - rectangle.TopLeft.X)) < 1e-9 && Math.Abs((edge.TopRight.Y - edge.TopLeft.Y) - (rectangle.TopRight.Y - rectangle.TopLeft.Y)) < 1e-9, "Rotated matrix edge keeps its angle and rectangular constraints");
            Console.WriteLine("Passed visualization inverse geometry, protected selection, mask points/save/reset/undo, paired edge motion, shared endpoints and rotated LED rectangle constraints.");
        }
        finally { Directory.Delete(folder, true); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Surface(string id) : IPointSurface
    {
        public string DisplayName => "display"; public string ScreenId => id; public string ScreenName => id; public int PixelWidth => 1920; public int PixelHeight => 1080;
        public PointOptions? Preview;
        public void SelectScreen(bool selected) { } public void ShowCalibration(int selected) { } public void FadeCalibration() { }
        public void FlashCalibration(int point, bool success) { } public void FlashCalibrationPoints(int pointMask) { }
        public void PreviewPoints(PointOptions? points) => Preview = points;
        public Task<bool> ConfirmCalibrationAsync(PointOptions expected) => Task.FromResult(true); public void ReleaseCalibrationCapture() { }
    }
}
