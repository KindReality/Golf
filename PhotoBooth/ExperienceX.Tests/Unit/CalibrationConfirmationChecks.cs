using ExperienceX;
using System.Text.Json;

internal static class CalibrationConfirmationChecks
{
    public static async Task Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-confirm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "appsettings.json");
            var initial = new KeystoneOptions { Enabled = true, TopLeft = new(0.2, 0.1) };
            var current = new ExperienceOptions { ConfigurationMode = true, DisplayKeystones = new() { ["DISPLAY1"] = initial } };
            File.WriteAllText(path, JsonSerializer.Serialize(current));
            var store = new KeystoneConfigurationStore(path);
            var surface = new Surface();
            KeystoneCalibration? editor = null;
            async Task Reload()
            {
                var previous = current;
                current = ExperienceOptions.Parse(File.ReadAllText(path));
                editor!.ConfigurationChanged(current);
                await editor.ConfigurationAppliedAsync(previous, current);
            }
            editor = new(() => current, () => new[] { surface }, store, Reload);
            async Task WaitFor(Func<bool> finished)
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!finished())
                {
                    if (DateTime.UtcNow >= deadline) throw new Exception("Calibration letter shortcut timed out.");
                    await Task.Delay(5);
                }
            }
            if (!editor.Key(surface, System.Windows.Forms.Keys.R))
                throw new Exception("R without modifiers did not handle reset.");
            await WaitFor(() => surface.Masks.Count == 1);
            if (current.KeystoneFor("DISPLAY1") != new KeystoneOptions() || !surface.Masks.SequenceEqual([15]))
                throw new Exception("Per-monitor reset failed to save/confirm all corners.");
            if (!editor.Key(surface, System.Windows.Forms.Keys.Z))
                throw new Exception("Z without modifiers did not handle undo.");
            await WaitFor(() => surface.Masks.Count == 2);
            if (current.KeystoneFor("DISPLAY1") != initial || !surface.Masks.SequenceEqual([15, 15]))
                throw new Exception("Undo did not persist the previous calibration.");
            surface.Confirm = _ => Task.FromResult(false);
            editor.Begin(surface, 0.2, 0.1);
            editor.Move(surface, 0.3, 0.1);
            if (await editor.FinishAsync(surface) || surface.WhiteFlashes != 0)
                throw new Exception("Unapplied local settings produced a white success flash.");
            var expected = KeystoneEditing.Move(current.KeystoneFor("DISPLAY1"), 0, 0.35, 0.1);
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            surface.Confirm = value => value == expected ? pending.Task : Task.FromResult(true);
            editor.Begin(surface, 0.3, 0.1);
            editor.Move(surface, 0.35, 0.1);
            var saving = editor.FinishAsync(surface);
            while (current.KeystoneFor("DISPLAY1") != expected)
                await Task.Delay(5);
            var remote = KeystoneEditing.Move(expected, 1, 0.9, 0);
            current = current with
            {
                DisplayKeystones = new()
                {
                    ["DISPLAY1"] = remote
                }
            };
            File.WriteAllText(path, JsonSerializer.Serialize(current));
            pending.SetResult(true);
            if (await saving || surface.WhiteFlashes != 0)
                throw new Exception("Superseded local save produced a false confirmation.");
            surface.Confirm = _ => Task.FromResult(true);
            // A remote change invalidates local undo history, so Undo cannot overwrite it.
            await editor.ConfigurationAppliedAsync(current with
            {
                DisplayKeystones = new()
                {
                    ["DISPLAY1"] = expected
                }
            }, current);
            await editor.UndoAsync(surface);
            if (current.KeystoneFor("DISPLAY1") != remote)
                throw new Exception("Undo overwrote a remote calibration.");
            var first = KeystoneEditing.Move(remote, 0, 0.4, 0.1);
            var second = KeystoneEditing.Move(first, 0, 0.45, 0.1);
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            surface.Confirm = value => value == first ? pending.Task : Task.FromResult(true);
            var previous = current;
            current = current with
            {
                DisplayKeystones = new()
                {
                    ["DISPLAY1"] = first
                }
            };
            var firstConfirmation = editor.ConfigurationAppliedAsync(previous, current);
            previous = current;
            current = current with
            {
                DisplayKeystones = new()
                {
                    ["DISPLAY1"] = second
                }
            };
            var flashes = surface.Masks.Count;
            await editor.ConfigurationAppliedAsync(previous, current);
            pending.SetResult(true);
            await firstConfirmation;
            if (surface.Masks.Count != flashes + 1)
                throw new Exception("An obsolete remote revision flashed after a newer revision.");
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("Passed persisted reset/undo, remote undo invalidation, renderer acknowledgement, and superseded local/remote confirmation races.");
    }
    private sealed class Surface : IKeystoneSurface
    {
        public string MonitorName => "DISPLAY1"; public int PixelWidth => 1920; public int PixelHeight => 1080;
        public List<int> Masks = []; public int WhiteFlashes;
        public Func<KeystoneOptions, Task<bool>> Confirm = _ => Task.FromResult(true);
        public void ShowCalibration(int selected)
        {
        }
        public void FadeCalibration()
        {
        }
        public void PreviewKeystone(KeystoneOptions? value)
        {
        }
        public void ReleaseCalibrationCapture()
        {
        }
        public void FlashCalibration(int point, bool success)
        {
            if (success)
                WhiteFlashes++;
        }
        public void FlashCalibrationPoints(int mask) => Masks.Add(mask);
        public Task<bool> ConfirmCalibrationAsync(KeystoneOptions expected) => Confirm(expected);
    }
}
