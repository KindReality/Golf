using System.Text.Json;
using ExperienceX;
using Forms = System.Windows.Forms;

internal static class PointSnappingChecks
{
    internal static async Task Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-snapping-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var sourcePoints = Quad(.1, .1, .4, .4);
            var targetPoints = Quad(.5, .1, .8, .4);
            var thirdPoints = Quad(.5, .1, .9, .6);
            var source = new Surface("DISPLAY1", "source");
            var target = new Surface("DISPLAY1", "target");
            var third = new Surface("DISPLAY1", "third");
            var otherDisplay = new Surface("DISPLAY2", "source");
            var surfaces = new[] { source, target, third, otherDisplay };
            var current = new ExperienceOptions
            {
                ConfigurationMode = true,
                DisplayScreens = new()
                {
                    ["DISPLAY1"] = [new() { Id = "source", Name = "source", Points = sourcePoints },
                        new() { Id = "target", Name = "target", Type = "mat", Points = targetPoints },
                        new() { Id = "third", Name = "third", Points = thirdPoints }],
                    ["DISPLAY2"] = [new() { Id = "source", Name = "source", Points = sourcePoints }]
                }
            };
            var path = Path.Combine(folder, "appsettings.json");
            File.WriteAllText(path, JsonSerializer.Serialize(current));
            var store = new PointConfigurationStore(path);
            PointCalibration? editor = null;
            async Task Reload()
            {
                var before = current;
                current = ExperienceOptions.Parse(File.ReadAllText(path));
                editor?.ConfigurationChanged(current);
                if (editor is not null) await editor.ConfigurationAppliedAsync(before, current);
            }
            PointOptions Shape(Surface surface) => surface.Preview ?? current.PointsFor(surface.DisplayName, surface.ScreenId);
            editor = new(() => current, () => surfaces, store, Reload);
            Check(editor.Begin(source, .4, .1, 1), "Source point selection");
            editor.Move(source, .49, .1);
            Check(Shape(source).TopRight == new ScreenPoint(.5, .1), "Point jumps exactly to the nearest eligible coordinate");
            editor.Move(source, .54, .15);
            Check(Shape(source).TopRight == Shape(target).TopLeft && Shape(target).TopLeft == Shape(third).TopLeft,
                "Snapped media and mat points continue moving as one group");
            Check(Shape(otherDisplay) == sourcePoints, "Matching coordinates on another display remain independent");
            Check(target.Selected == 0 && third.Selected == 0, "All linked members show the selected point indicator");
            Check(await editor.FinishAsync(source), "Linked edit saves successfully");
            Check(current.PointsFor("DISPLAY1", "source").TopRight == current.PointsFor("DISPLAY1", "target").TopLeft &&
                current.PointsFor("DISPLAY1", "target").TopLeft == current.PointsFor("DISPLAY1", "third").TopLeft,
                "All linked coordinates are persisted together");
            Check(source.Flashes == 1 && target.Flashes == 1 && third.Flashes == 1, "Each changed screen receives one save confirmation");
            editor.SelectScreen(target);
            Check(editor.Key(target, Forms.Keys.OemOpenBrackets), "[ moves the selected screen to the bottom");
            var layerDeadline = DateTime.UtcNow.AddSeconds(3);
            while (current.ScreensFor("DISPLAY1")[0].Id != "target")
            {
                if (DateTime.UtcNow > layerDeadline) throw new TimeoutException("[ did not persist the selected layer.");
                await Task.Delay(10);
            }
            Check(editor.Key(target, Forms.Keys.Control | Forms.Keys.Z), "Ctrl+Z invokes movement undo");
            var undoDeadline = DateTime.UtcNow.AddSeconds(3);
            while (current.PointsFor("DISPLAY1", "source") != sourcePoints)
            {
                if (DateTime.UtcNow > undoDeadline) throw new TimeoutException("Ctrl+Z did not restore the linked movement.");
                await Task.Delay(10);
            }
            Check(current.PointsFor("DISPLAY1", "source") == sourcePoints && current.PointsFor("DISPLAY1", "target") == targetPoints &&
                current.PointsFor("DISPLAY1", "third") == thirdPoints, "Undo from any affected screen restores the entire transaction");
            Check(current.ScreensFor("DISPLAY1")[0].Id == "target", "Movement undo preserves layer order");
            editor.Begin(source, .4, .1, 1);
            editor.Move(source, .49, .1);
            Check(editor.Key(source, Forms.Keys.Control | Forms.Keys.Z), "Ctrl+Z handles an active snapped movement");
            var draftDeadline = DateTime.UtcNow.AddSeconds(3);
            while (source.Preview is not null || target.Preview is not null)
            {
                if (DateTime.UtcNow > draftDeadline) throw new TimeoutException("Ctrl+Z did not cancel the complete snapped movement.");
                await Task.Delay(10);
            }
            Check(current.PointsFor("DISPLAY1", "source") == sourcePoints && current.PointsFor("DISPLAY1", "target") == targetPoints, "Draft undo did not alter persisted geometry");

            editor.Begin(source, .4, .1, 1);
            editor.Move(source, .49, .1);
            await editor.UndoAsync(source);
            Check(Shape(source).TopRight == new ScreenPoint(.49, .1) && Shape(target).TopLeft == new ScreenPoint(.5, .1), "Z during a drag undoes the magnet jump");
            editor.Move(source, .495, .1);
            Check(Shape(source).TopRight.X == .495, "Undo prevents an immediate accidental resnap");
            editor.Move(source, .43, .1);
            editor.Move(source, .49, .1);
            Check(Shape(source).TopRight == Shape(target).TopLeft, "Snapping resumes after leaving the click radius");
            Check(editor.Key(source, Forms.Keys.Delete), "Delete disconnects the selected point");
            editor.Move(source, .51, .1);
            Check(Shape(source).TopRight != Shape(target).TopLeft && Shape(target) == targetPoints,
                "Disconnected point moves independently within the suppression radius");
            await editor.FinishAsync(source);
            Check(current.ScreensFor("DISPLAY1").Count == 3, "Delete on a selected point never deletes its screen");
            editor.Key(source, Forms.Keys.Delete);
            Check(current.ScreensFor("DISPLAY1").Count == 3, "A released but selected point still protects screen deletion");
            editor.SelectScreen(source);
            Check(await editor.DeleteSelectedAsync(), "Screen selection retains whole-screen deletion");
            Check(current.ScreensFor("DISPLAY1").Count == 2, "Only the selected screen is deleted");
            await editor.DisposeAsync();

            // Coincidence alone defines a group, including after creating a fresh editor.
            editor = new(() => current, () => new[] { target, third }, store, Reload);
            editor.Begin(target, .5, .1, 0);
            editor.Move(target, .55, .15);
            Check(Shape(target).TopLeft == Shape(third).TopLeft, "Pre-existing exact coordinates move together without a prior snap");
            await editor.FinishAsync(target);
            await editor.DisposeAsync();
            editor = new(() => current, () => new[] { target, third }, store, Reload);
            editor.Begin(third, .55, .15, 0);
            await editor.FinishAsync(third); // Click/release must preserve point focus.
            editor.Key(third, Forms.Keys.Right);
            Check(Shape(target).TopLeft == Shape(third).TopLeft && Shape(third).TopLeft.X > .55, "Arrow keys move a released selected point and its linked peers");
            editor.Cancel("KeyboardCheckComplete");

            var before = current.PointsFor("DISPLAY1", "target");
            var siblingBefore = current.PointsFor("DISPLAY1", "third");
            var remote = PointEditing.Move(siblingBefore, 1, .92, .1);
            await store.SaveAsync("DISPLAY1", siblingBefore, remote, "third");
            var fileBefore = File.ReadAllBytes(path);
            try
            {
                await store.SaveManyAsync("DISPLAY1", [new("target", before, PointEditing.Move(before, 0, .56, .16)), new("third", siblingBefore, siblingBefore)]);
                throw new Exception("Stale linked transaction was accepted.");
            }
            catch (PointEditConflictException) { }
            Check(File.ReadAllBytes(path).SequenceEqual(fileBefore), "A stale participant prevents every write in a linked transaction");
            await Reload();
            editor.Begin(target, Shape(target).TopLeft.X, Shape(target).TopLeft.Y, 0);
            editor.Move(target, .65, .2);
            var replacement = PointEditing.Move(current.PointsFor("DISPLAY1", "third"), 1, .94, .1);
            await store.SaveAsync("DISPLAY1", current.PointsFor("DISPLAY1", "third"), replacement, "third");
            await Reload();
            Check(target.Preview is null && third.Preview is null, "A remote participant edit cancels every linked preview");
            var matBeforeInsert = current.PointsFor("DISPLAY1", "target");
            var a = matBeforeInsert.Vertices[3]; var b = matBeforeInsert.Vertices[0];
            await editor.InsertPointAsync(target, 3, new((a.X + b.X) / 2, (a.Y + b.Y) / 2));
            Check(current.PointsFor("DISPLAY1", "target").Vertices.Count == 5 && target.Selected == 4, "A new point on the last mat edge can be selected before and after saving");
            await editor.UndoAsync(target);
            Check(current.PointsFor("DISPLAY1", "target") == matBeforeInsert, "Undo removes the inserted mat point without selecting a stale index");
            await editor.DisposeAsync();

            var polygon = Quad(.1, .1, .4, .4);
            var only = new PointSnapping(new() { [target] = polygon }, new() { [target] = true }, (a, b) => a == b);
            only.Select(new(target, 0));
            only.Move(new(target, 0), .399, .1, 5, 0, 0);
            only.Drafts[target].ValidatePolygon();
            Check(only.Drafts[target].TopLeft != only.Drafts[target].TopRight, "Snapping cannot collapse vertices of the same screen");
            Console.WriteLine("Passed point snapping, exact-coordinate groups, media/mat sharing, linked save/undo, Delete separation, keyboard movement and atomic remote-conflict handling.");
        }
        finally { Directory.Delete(folder, true); }
    }
    private static PointOptions Quad(double x, double y, double right, double bottom) => new() { Enabled = true, TopLeft = new(x, y), TopRight = new(right, y), BottomLeft = new(x, bottom), BottomRight = new(right, bottom) };
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Surface(string display, string id) : IPointSurface
    {
        public string DisplayName => display;
        public string ScreenId => id;
        public string ScreenName => id;
        public int PixelWidth => 1920;
        public int PixelHeight => 1080;
        public PointOptions? Preview;
        public int Flashes, Selected;
        public void SelectScreen(bool selected) { }
        public void ShowCalibration(int selected) => Selected = selected;
        public void FadeCalibration() => Selected = -1;
        public void FlashCalibration(int point, bool success) { if (success) Flashes++; }
        public void FlashCalibrationPoints(int pointMask) => Flashes++;
        public void PreviewPoints(PointOptions? value) => Preview = value;
        public Task<bool> ConfirmCalibrationAsync(PointOptions expected) => Task.FromResult(true);
        public void ReleaseCalibrationCapture() { }
    }
}
