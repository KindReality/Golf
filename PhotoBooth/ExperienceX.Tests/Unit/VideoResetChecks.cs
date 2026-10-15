using System.Text.Json;
using System.Text.Json.Nodes;
using ExperienceX;

internal static class VideoResetChecks
{
    internal static async Task Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-video-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "appsettings.json");
            var video = new ScreenOptions
            {
                Id = "video", Name = "Eye", DefaultName = "screen1", MediaFile = "Eye.mp4", Roles = "front,scary",
                Brightness = 25, Framing = new() { Zoom = 3, CenterX = .6, CenterY = .4 },
                BlackKey = new() { Enabled = true, Threshold = .2, Softness = .1 },
                Perspective = new() { ControllerId = Guid.NewGuid().ToString("N"), PerspectiveId = Guid.NewGuid().ToString("N"), Name = "crop", Rectangle = new() { X = 10, Width = 90 } },
                Points = ScreenOptions.CenteredPoints()
            };
            var other = new ScreenOptions { Id = "other", Name = "other", Brightness = 72 };
            var mat = new ScreenOptions { Id = "mat", Name = "mat", Type = "mat" };
            var options = new ExperienceOptions { ConfigurationMode = true, BlackKey = new() { Enabled = false, Threshold = .11, Softness = .12 },
                DisplayScreens = new() { ["TEST"] = [other, video, mat] } };
            var original = JsonSerializer.SerializeToNode(options)!.AsObject();
            original["Custom"] = 17;
            original["DisplayScreens"]!["TEST"]![1]!["CustomScreenSetting"] = "retain";
            File.WriteAllText(path, original.ToJsonString());
            var store = new PointConfigurationStore(path);
            await store.ResetVideoAsync("TEST", video);
            var reset = ExperienceOptions.Parse(File.ReadAllText(path));
            var screen = reset.ScreensFor("TEST")[1];
            Check(screen == video with { Framing = new(), Brightness = 100, Perspective = null, BlackKey = null }, "Reset restores all video presentation settings while retaining geometry, name, binding and roles");
            Check(screen.EffectiveBlackKey(reset) == options.BlackKey, "Reset restores configured application chroma-key defaults");
            Check(reset.ScreensFor("TEST")[0] == other && reset.ScreensFor("TEST")[2] == mat, "Reset retains other screens and layer order");
            var saved = JsonNode.Parse(File.ReadAllText(path))!;
            Check(saved["Custom"]!.GetValue<int>() == 17 && saved["DisplayScreens"]!["TEST"]![1]!["CustomScreenSetting"]!.GetValue<string>() == "retain", "Reset preserves unrelated and unknown configuration");
            Check(ExperienceOptions.Parse(File.ReadAllText(path + ".bak")).ScreensFor("TEST")[1] == video, "All reset fields are saved in one atomic update");
            foreach (var changed in new[] { video with { Brightness = 26 }, video with { Framing = new() { Zoom = 2 } },
                video with { Perspective = null }, video with { BlackKey = null }, video with { Points = new() } })
            {
                var remote = options with { DisplayScreens = new() { ["TEST"] = [other, changed, mat] } };
                File.WriteAllText(path, JsonSerializer.Serialize(remote));
                var before = File.ReadAllText(path);
                await Reject(() => store.ResetVideoAsync("TEST", video));
                Check(File.ReadAllText(path) == before, "Reset rejects conflicting remote video settings without overwriting them");
            }
            File.WriteAllText(path, JsonSerializer.Serialize(options with { ConfigurationMode = false }));
            await Reject(() => store.ResetVideoAsync("TEST", video));
            File.WriteAllText(path, JsonSerializer.Serialize(options));
            await Reject(() => store.ResetVideoAsync("TEST", mat));
            await Reject(() => store.ResetVideoAsync("TEST", video with { Id = "missing" }));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await store.ResetVideoAsync("TEST", video, canceled.Token); throw new Exception("Canceled reset wrote settings."); }
            catch (OperationCanceledException) { }
            Check(ExperienceOptions.Parse(File.ReadAllText(path)).ScreensFor("TEST")[1] == video, "Production, non-video, missing-screen and canceled resets cannot change video settings");
            Check(!ScreenActionHint.TextFor("video").Contains("Ctrl+C") && !ScreenActionHint.TextFor("video").Contains("Ctrl+V") && !ScreenActionHint.TextFor("video").Contains("Ctrl+0") &&
                ScreenActionHint.TextFor("video").Contains("Ctrl+R: reset video"), "Grouped media HUD contains the new reset shortcut without clipboard or obsolete reset hints");
        }
        finally { Directory.Delete(folder, true); }
    }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (PointEditConflictException) { return; }
        throw new Exception("Unsafe or stale video reset was accepted.");
    }
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); Console.WriteLine("PASS " + text); }
}
