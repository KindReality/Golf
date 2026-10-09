using System.Text.Json;
using System.Text.Json.Nodes;
using ExperienceX;
using Forms = System.Windows.Forms;

internal static class CalibrationChecks
{
    public static async Task Run()
    {
        if (Math.Abs(KeystoneEditing.HitRadius(1920, 1080, 5) - 55.07267925) > 0.000001)
            throw new Exception("Hit diameter is not 5% of the diagonal.");
        var k = new KeystoneOptions();
        if (!KeystoneEditing.Hits(k, 0.02, 0, 1920, 1080, 5).SequenceEqual([0]) || KeystoneEditing.Hits(k, 0.04, 0, 1920, 1080, 5).Length != 0)
            throw new Exception("Near-corner selection radius is incorrect.");
        var cycle = new KeystoneHitCycle();
        if (new[] { cycle.Pick([0, 1, 2, 3]), cycle.Pick([0, 1, 2, 3]), cycle.Pick([0, 1, 2, 3]), cycle.Pick([0, 1, 2, 3]), cycle.Pick([0, 1, 2, 3]) }.SequenceEqual([0, 1, 2, 3, 0]) == false)
            throw new Exception("Overlapping corners did not cycle UL/UR/BL/BR.");
        var random = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            k = KeystoneEditing.Move(k, i % 4, random.NextDouble() * 2 - 0.5, random.NextDouble() * 2 - 0.5);
            k.Validate();
            var inverse = VideoTransform.Inverse(k);
            if (!float.IsFinite(inverse.Item1.X))
                throw new Exception("Dragging produced an unusable transform.");
        }
        var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-calibration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "appsettings.json");
        try
        {
            File.WriteAllText(path, "{\"configurationMode\":true,\"FutureSetting\":{\"Keep\":123},\"Keystone\":{\"TopLeft\":{\"X\":0.1,\"Y\":0}}}");
            var store = new KeystoneConfigurationStore(path);
            var json = store.EnsureDisplays(["DISPLAY1", "DISPLAY2"]);
            var current = ExperienceOptions.Parse(json);
            if (current.DisplayKeystones.Count != 2 || current.KeystoneFor("display1").TopLeft.X != 0.1)
                throw new Exception("Display defaults/migration failed.");
            var bytes = File.ReadAllBytes(path);
            store.EnsureDisplays(["DISPLAY1", "DISPLAY2"]);
            if (!bytes.SequenceEqual(File.ReadAllBytes(path)))
                throw new Exception("Initialization rewrote existing settings.");
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            root["RemoteUnrelated"] = 17;
            File.WriteAllText(path, root.ToJsonString());
            var original = current.KeystoneFor("DISPLAY1");
            var changed = KeystoneEditing.Move(original, 0, 0.2, 0.1);
            await store.SaveAsync("DISPLAY1", original, changed);
            root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if (root["FutureSetting"]!["Keep"]!.GetValue<int>() != 123 || root["RemoteUnrelated"]!.GetValue<int>() != 17 ||
                ExperienceOptions.Parse(root.ToJsonString()).KeystoneFor("DISPLAY2") != current.KeystoneFor("DISPLAY2"))
                throw new Exception("Saving lost unrelated settings or another display's calibration.");
            try
            {
                await store.SaveAsync("DISPLAY1", original, changed);
                throw new Exception("Stale save was accepted.");
            }
            catch (KeystoneEditConflictException) { }
            current = ExperienceOptions.Parse(File.ReadAllText(path));
            var left = new Surface("DISPLAY1");
            var right = new Surface("DISPLAY2");
            KeystoneCalibration? editor = null;
            async Task Reload()
            {
                var previous = current;
                current = ExperienceOptions.Parse(await Task.Run(() => store.EnsureDisplays(["DISPLAY1", "DISPLAY2"])));
                editor?.ConfigurationChanged(current);
                if (editor is not null)
                    await editor.ConfigurationAppliedAsync(previous, current);
            }
            editor = new KeystoneCalibration(() => current, () => new[] { left, right }, store, Reload);
            var p = current.KeystoneFor("DISPLAY1").TopLeft;
            editor.Begin(left, p.X + 0.01, p.Y + 0.01);
            editor.Move(left, p.X + 0.01, p.Y + 0.01);
            if (left.Preview != current.KeystoneFor("DISPLAY1"))
                throw new Exception("Near-corner acquisition made the point jump.");
            editor.Move(left, p.X + 0.02, p.Y + 0.02);
            if (Math.Abs(left.Preview!.TopLeft.X - p.X - 0.01) > 1e-10)
                throw new Exception("Dragging lost the initial pointer offset.");
            editor.Cancel("OffsetCheckComplete");
            if (!editor.Begin(left, p.X, p.Y) || left.Selected != 0 || right.Selected != -1 || right.Shows == 0)
                throw new Exception("All-monitor reveal/selection failed.");
            editor.Move(left, 0.3, 0.15);
            if (left.Preview?.TopLeft.X != 0.3 || !left.Preview.Enabled)
                throw new Exception("Live preview/automatic enable failed.");
            await editor.FinishAsync(left);
            if (current.KeystoneFor("DISPLAY1").TopLeft.X != 0.3 || left.WhiteFlashes != 1 || left.Preview is not null)
                throw new Exception("Mouse-release save failed.");
            if (left.RemoteMasks.Count != 0 || right.RemoteMasks.Count != 0)
                throw new Exception("Local save produced a duplicate remote flash.");
            editor.Key(left, Forms.Keys.Tab);
            if (left.Selected != 0)
                throw new Exception("Keyboard focus did not start at UL.");
            editor.Key(left, Forms.Keys.Tab);
            if (left.Selected != 1)
                throw new Exception("UR focus order failed.");
            editor.Key(left, Forms.Keys.Tab);
            if (left.Selected != 2)
                throw new Exception("BL focus order failed.");
            editor.Key(left, Forms.Keys.Tab);
            if (left.Selected != 3)
                throw new Exception("BR focus order failed.");
            editor.Key(left, Forms.Keys.Shift | Forms.Keys.Tab);
            if (left.Selected != 2)
                throw new Exception("Reverse focus failed.");
            editor.Key(left, Forms.Keys.Up);
            if (left.Preview?.BottomLeft.Y >= 1)
                throw new Exception("Arrow movement failed.");
            editor.Key(left, Forms.Keys.Escape);
            if (left.Preview is not null)
                throw new Exception("Escape did not cancel.");
            p = current.KeystoneFor("DISPLAY1").TopLeft;
            editor.Begin(left, p.X, p.Y);
            editor.Move(left, 0.35, 0.2);
            root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            root["DisplayKeystones"]!["DISPLAY1"]!["TopLeft"]!["X"] = 0.25;
            File.WriteAllText(path, root.ToJsonString());
            await Reload();
            editor.ConfigurationChanged(current);
            await editor.FinishAsync(left);
            if (left.Preview is not null || current.KeystoneFor("DISPLAY1").TopLeft.X != 0.25 || left.WhiteFlashes != 1)
                throw new Exception("Remote edits were overwritten or falsely confirmed.");
            if (!left.RemoteMasks.SequenceEqual([1]) || right.RemoteMasks.Count != 0)
                throw new Exception("Remote single-corner change flashed the wrong display/points.");
            p = current.KeystoneFor("DISPLAY1").TopLeft;
            editor.Begin(left, p.X, p.Y);
            editor.Move(left, 0.4, 0.1);
            root["configurationMode"] = false;
            File.WriteAllText(path, root.ToJsonString());
            await Reload();
            editor.ConfigurationChanged(current);
            if (left.Preview is not null || editor.Begin(left, 0.25, 0))
                throw new Exception("Disabling configurationMode did not cancel/disable editing.");
            if (editor.Key(left, Forms.Keys.R) || editor.Key(left, Forms.Keys.Z))
                throw new Exception("Calibration letters remained active with configuration mode disabled.");
            root["DisplayKeystones"]!["DISPLAY1"]!["TopLeft"]!["X"] = 0.2;
            root["DisplayKeystones"]!["DISPLAY1"]!["TopRight"]!["X"] = 0.9;
            root["DisplayKeystones"]!["DISPLAY2"]!["BottomRight"]!["Y"] = 0.9;
            File.WriteAllText(path, root.ToJsonString());
            await Reload();
            if (!left.RemoteMasks.SequenceEqual([1, 3]) || !right.RemoteMasks.SequenceEqual([8]))
                throw new Exception("Remote multiple-corner/display flashes failed with configuration mode off.");
            root["RemoteUnrelated"] = 18;
            File.WriteAllText(path, root.ToJsonString());
            await Reload();
            if (left.RemoteMasks.Count != 2 || right.RemoteMasks.Count != 1)
                throw new Exception("Unrelated configuration changes produced a flash.");
            root["DisplayKeystones"]!["DISPLAY1"]!["Enabled"] = false;
            File.WriteAllText(path, root.ToJsonString());
            await Reload();
            if (left.RemoteMasks.Last() != 15)
                throw new Exception("Enabled-only changes did not confirm all corners.");
            var count = left.RemoteMasks.Count;
            await editor.ConfigurationAppliedAsync(current with
            {
                DisplayKeystones = new()
            }, current);
            if (left.RemoteMasks.Count != count)
                throw new Exception("Initial display settings produced a remote save flash.");
            var partial = new JsonObject { ["DisplayKeystones"] = new JsonObject { ["DISPLAY1"] = new JsonObject { ["TopLeft"] = new JsonObject { ["X"] = 0.1 } } } };
            File.WriteAllText(path, partial.ToJsonString());
            store.EnsureDisplays(["DISPLAY1"]);
            var complete = JsonNode.Parse(File.ReadAllText(path))!["DisplayKeystones"]!["DISPLAY1"]!;
            if (complete["TopLeft"]!["X"]!.GetValue<double>() != 0.1 || complete["BottomRight"]!["Y"]!.GetValue<double>() != 1)
                throw new Exception("Partial display settings were not filled without overwriting existing values.");
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("Passed calibration hit diameter/cycling, drag constraints, auto-created per-display settings, merge saves, live preview, keyboard order/cancel, remote-edit conflicts, and remote save flashes without local duplicates.");
    }
    private sealed class Surface(string name) : IKeystoneSurface
    {
        public string MonitorName => name; public int PixelWidth => 1920; public int PixelHeight => 1080;
        public int Selected = -1, Shows, WhiteFlashes; public KeystoneOptions? Preview;
        public List<int> RemoteMasks = [];
        public void ShowCalibration(int point)
        {
            Selected = point;
            Shows++;
        }
        public void FadeCalibration()
        {
        }
        public void FlashCalibration(int point, bool success)
        {
            if (success)
                WhiteFlashes++;
        }
        public void FlashCalibrationPoints(int pointMask) => RemoteMasks.Add(pointMask);
        public void PreviewKeystone(KeystoneOptions? value) => Preview = value;
        public Task<bool> ConfirmCalibrationAsync(KeystoneOptions expected) => Task.FromResult(true);
        public void ReleaseCalibrationCapture()
        {
        }
    }
}
