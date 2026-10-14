using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExperienceX;

internal static class VisualizationCommandChecks
{
    internal static async Task Run()
    {
        ExperienceRequest Parse(object body) => ExperienceRequest.Parse(JsonSerializer.SerializeToUtf8Bytes(body));
        var defaults = new VisualizationOptions();
        Check(defaults.Theme == "(Standard)" && defaults.ColorMap == "Clear Blue Water" && defaults.WaveShape == "Random" && defaults.FlowField == "Random" &&
            defaults.FlowFieldUnion == "None" && defaults.Particle == "None" && defaults.Sprite == "None", "Explicit launch selections differ from the agreed defaults");
        Check(defaults.TransitionSeconds is null && defaults.ChangeDelaySeconds is null && defaults.ParticleDurationSeconds is null && defaults.SpriteDurationSeconds is null, "Optional timings must be null");
        foreach (var family in new[] { "theme", "colorMap", "waveShape", "flowField", "flowFieldUnion" })
        {
            _ = Parse(new { experience = "visualization", command = "select", visualType = family, value = "Random", screen = "Projector/Spectrum" });
            _ = Parse(new { experience = "visualization", command = "previous", visualType = family });
            _ = Parse(new { experience = "visualization", command = "next", visualType = family, screen = "*" });
        }
        foreach (var family in new[] { "particle", "sprite" })
        {
            _ = Parse(new { experience = "visualization", command = "start", visualType = family, value = "Random", durationSeconds = 12 });
            _ = Parse(new { experience = "visualization", command = "stop", visualType = family, transitionSeconds = 0 });
        }
        var timed = Parse(new { experience = "visualization", command = "select", visualType = "flowField", value = "Linear Spread", transitionSeconds = 0, delaySeconds = 5 });
        Check(timed.TransitionSeconds == 0 && timed.DelaySeconds == 5, "Zero must not become an empty/default timing");
        foreach (var unsupported in new[] { "preset", "script", "overlay", "screenshot", "slideshow" })
            Invalid(() => Parse(new { experience = "visualization", command = unsupported, visualType = "colorMap" }));
        Invalid(() => Parse(new { experience = "visualization", command = "select", visualType = "preset", value = "1" }));
        Invalid(() => Parse(new { experience = "visualization", command = "stop", visualType = "theme" }));
        Invalid(() => Parse(new { experience = "visualization", command = "select", visualType = "sprite", value = "Plasma" }));
        Invalid(() => Parse(new { experience = "visualization", command = "select", visualType = "colorMap", value = "None" }));
        Invalid(() => Parse(new { experience = "visualization", command = "next", visualType = "colorMap", value = "Unexpected" }));
        Invalid(() => Parse(new { experience = "visualization", command = "next", visualType = "colorMap", delaySeconds = 1 }));
        Invalid(() => Parse(new { experience = "visualization", command = "next", visualType = "colorMap", durationSeconds = 1 }));
        Invalid(() => Parse(new { experience = "visualization", command = "next", visualType = "colorMap", transitionSeconds = -1 }));
        Invalid(() => Parse(new { experience = "visualization", command = "next", visualType = "colorMap", volume = 50 }));
        Invalid(() => Parse(new { experience = "visualization", command = "start", visualType = "particle", value = "Explosion", transitionSeconds = 1 }));
        Invalid(() => Parse(new { experience = "visualization", command = "select", visualType = "waveShape", value = "None" }));
        Invalid(() => Parse(new { experience = "visualization", command = "stop", visualType = "sprite", value = "Random" }));
        Invalid(() => ExperienceRequest.Parse(Encoding.UTF8.GetBytes("{\"experience\":\"visualization\",\"command\":\"stop\",\"visualType\":\"sprite\",\"value\":null}")));
        Invalid(() => (defaults with { TransitionSeconds = double.NaN }).Validate());
        Invalid(() => (defaults with { Theme = "" }).Validate());
        var options = new ExperienceOptions { DisplayAliases = new() { ["Projector"] = "D1" }, DisplayScreens = new() { ["D1"] = [new(), new() { Id = "v", Name = "Spectrum", Type = "visualization" }] } };
        Check(ScreenSelection.VisualizationOutputs(timed, options, ["D1"], "D1").Single().Screen.Id == "v", "Visualization target selected media instead of the visualization screen");
        var folder = Path.Combine(Path.GetTempPath(), "VisualizationCommands-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "appsettings.json");
            var root = JsonSerializer.SerializeToNode(options)!.AsObject(); root["Visualization"] = new JsonObject { ["Theme"] = "Infernal", ["ExecutablePath"] = "custom.exe" };
            File.WriteAllText(path, root.ToJsonString());
            var store = new PointConfigurationStore(path); store.EnsureDisplays(["D1"]);
            var saved = JsonNode.Parse(File.ReadAllText(path))!["Visualization"]!;
            Check((string?)saved["Theme"] == "Infernal" && (string?)saved["ExecutablePath"] == "custom.exe" && (string?)saved["ColorMap"] == "Clear Blue Water", "Seeding overwrote a user selection or omitted defaults");
            Check(saved.AsObject().ContainsKey("TransitionSeconds") && saved["TransitionSeconds"] is null, "Empty timing fields were not saved explicitly");
            var once = File.ReadAllText(path); store.EnsureDisplays(["D1"]);
            Check(File.ReadAllText(path) == once, "Default population is not idempotent");
        }
        finally { Directory.Delete(folder, true); }
        await Task.CompletedTask;
        Console.WriteLine("PASS visualization command vocabulary, timing defaults, output routing, rejected media/script controls and preserved explicit configuration defaults");
    }
    private static void Check(bool passed, string message) { if (!passed) throw new Exception(message); }
    private static void Invalid(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid visualization input was accepted"); }
}
