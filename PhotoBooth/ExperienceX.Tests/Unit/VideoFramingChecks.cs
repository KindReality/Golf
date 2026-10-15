using System.Text.Json;
using System.Text.Json.Nodes;
using ExperienceX;
using PhotoBooth.Networking;

internal static class VideoFramingChecks
{
    internal static async Task Run()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
        var full = new VideoFramingOptions();
        Check(full.Crop(null) == new PerspectiveRectangle(), "Default framing changed existing full-video presentation.");
        var pointer = new ScreenPoint(.7, .3);
        var zoom = full.ZoomAt(pointer, 8);
        Check(Near(zoom.CenterX + (pointer.X - .5) / zoom.Zoom, pointer.X) && Near(zoom.CenterY + (pointer.Y - .5) / zoom.Zoom, pointer.Y),
            "Zoom did not retain the source detail beneath the pointer.");
        Check(zoom.ZoomAt(pointer, -1000) == full, "Zoom-out passed full perspective or retained panning at 1x.");
        var perspective = new PerspectiveRectangle { X = 20, Y = 10, Width = 60, Height = 40 };
        var crop = new VideoFramingOptions { Zoom = 2, CenterX = .75 }.Crop(perspective);
        Check(crop == new PerspectiveRectangle { X = 50, Y = 20, Width = 30, Height = 20 }, "Zoom/pan did not compose inside the selected perspective.");
        var pan = zoom.Pan(new(.5, .5), new(100, -100));
        pan.Validate(); crop = pan.Crop(perspective);
        Check(crop.X >= perspective.X && crop.Y >= perspective.Y && crop.X + crop.Width <= 80.000001 && crop.Y + crop.Height <= 50.000001,
            "Panning exposed blank media outside its perspective.");
        var quad = new PointOptions { Enabled = true, TopLeft = new(.2, .15), TopRight = new(.8, .3), BottomLeft = new(.1, .8), BottomRight = new(.85, .9) };
        Check(VideoFramingOptions.TryPointer(quad, .8, .3, out var local) && Math.Abs(local.X - 1) < 1e-6 && Math.Abs(local.Y) < 1e-6,
            "Pointer zoom did not respect a skewed screen's projective coordinates.");
        var rotated = new VideoFramingOptions { Zoom = 2, RotationDegrees = 90 };
        var moved = rotated.Pan(new(.5,.5),new(.6,.5),2);
        Check(Near(moved.CenterX,.5) && Near(moved.CenterY,.6) && moved.RotationDegrees==90,
            "Moving rotated media did not follow the pointer or lost its rotation.");
        var rotatedZoom = rotated.ZoomAt(new(.6,.5),1,2);
        Check(Near(rotatedZoom.CenterY - .2/rotatedZoom.Zoom, .4) && rotatedZoom.RotationDegrees==90,
            "Zooming rotated media lost its pointer anchor or rotation.");
        Check(rotated.Rotate(360)==rotated && rotated.Rotate(-180).RotationDegrees==-90 && full.Rotate(90).Crop(perspective)==perspective,
            "Rotation changed the perspective crop or failed to wrap whole turns.");
        Check(Near(VideoFramingOptions.ScreenAspect(new(),1920,1080),1920.0/1080), "Rotation did not use physical display proportions.");
        foreach (var bad in new[] { full with { Zoom = .5 }, full with { CenterX = .2 }, full with { Zoom = double.NaN }, full with { Zoom = double.PositiveInfinity }, full with { RotationDegrees = double.NaN }, full with { RotationDegrees = double.PositiveInfinity } })
        {
            try { bad.Validate(); throw new Exception("Invalid framing accepted."); } catch (ArgumentException) { }
        }
        var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-framing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "appsettings.json");
            var video = new ScreenOptions { Id = "video", Perspective = new() { ControllerId = Guid.NewGuid().ToString(), PerspectiveId = Guid.NewGuid().ToString(), Name = "crop", Rectangle = perspective }, Points = quad };
            var options = new ExperienceOptions { ConfigurationMode = true, DisplayScreens = new() { ["TEST"] = [video, new() { Id = "mat", Name = "mask", Type = "mat" }] } };
            var root = JsonSerializer.SerializeToNode(options)!.AsObject(); root["Custom"] = 11;
            File.WriteAllText(path, root.ToJsonString());
            var store = new PointConfigurationStore(path);
            await store.AdjustScreenBrightnessAsync("TEST", video.Id, -5);
            zoom = zoom with { RotationDegrees = 37.5 };
            await store.SaveVideoFramingAsync("TEST", video, zoom);
            var restored = ExperienceOptions.Parse(new PointConfigurationStore(path).EnsureDisplays(["TEST"])).ScreensFor("TEST")[0];
            Check(restored.Framing == zoom && restored.Points == quad && restored.Perspective == video.Perspective && restored.Brightness == 95 &&
                JsonNode.Parse(File.ReadAllText(path))!["Custom"]!.GetValue<int>() == 11, "Framing save/restart changed perspective, geometry, brightness or unrelated configuration.");
            try { await store.SaveVideoFramingAsync("TEST", video, full); throw new Exception("Stale framing overwrote the newer settings."); } catch (PointEditConflictException) { }
            await store.SetConfigurationModeAsync(false);
            try { await store.SaveVideoFramingAsync("TEST", restored, full); throw new Exception("Framing saved outside configuration mode."); } catch (PointEditConflictException) { }
            await store.SetConfigurationModeAsync(true);
            try { await store.SaveVideoFramingAsync("TEST", options.ScreensFor("TEST")[1], zoom); throw new Exception("Mat accepted video framing."); } catch (PointEditConflictException) { }
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("PASS pointer-anchored zoom, 1x minimum, perspective composition, bounded pan, projective coordinates, persistence and stale/configuration guards.");
    }
}
