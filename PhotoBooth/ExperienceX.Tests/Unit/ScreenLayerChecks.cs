using System.Text.Json;
using System.Text.Json.Nodes;
using ExperienceX;

internal static class ScreenLayerChecks
{
    public static async Task Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-layers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "appsettings.json");
            var options = new ExperienceOptions
            {
                ConfigurationMode = true, DisplayScreens = new()
                {
                    ["display"] = [new() { Id = "a", Name = "video1", Roles = "Entry" }, new() { Id = "m1", Name = "mat1", Type = "mat" }, new() { Id = "b", Name = "video2" }, new() { Id = "m2", Name = "mat2", Type = "mat" }],
                    ["other"] = [new() { Id = "other", Name = "screen1" }]
                }
            };
            var root = JsonSerializer.SerializeToNode(options)!;
            root["Custom"] = 17; root["DisplayScreens"]!["display"]![0]!["CustomScreen"] = 9;
            await File.WriteAllTextAsync(path, root.ToJsonString());
            var store = new PointConfigurationStore(path);
            string[] Order() => ExperienceOptions.Parse(File.ReadAllText(path)).ScreensFor("display").Select(screen => screen.Id).ToArray();
            var video = await store.AddScreenAsync("display");
            Check(Order().SequenceEqual(["a", "m1", "b", video.Id, "m2"]), "New video is immediately above the highest video, below later mats.");
            var mat = await store.AddScreenAsync("display", type: "mat");
            Check(Order()[^1] == mat.Id, "New mat is above the highest existing mat.");
            var firstMat = await store.AddScreenAsync("other", type: "mat");
            Check(ExperienceOptions.Parse(File.ReadAllText(path)).ScreensFor("other")[^1].Id == firstMat.Id, "First mat is above every existing item on its display.");
            await store.MoveScreenLayerAsync("display", "m1", false);
            Check(Order()[0] == "m1", "Mat can move below videos.");
            await store.MoveScreenLayerAsync("display", "a", true);
            Check(Order()[^1] == "a", "Video can move above mats.");
            var visualization = await store.AddScreenAsync("display", type: "visualization");
            Check(Order()[0] == visualization.Id, "G-Force is inserted beneath all other screens.");
            await store.MoveScreenLayerAsync("display", "m2", false);
            Check(Order()[0] == visualization.Id && Order()[1] == "m2", "Mat bottom remains above G-Force.");
            var before = await File.ReadAllTextAsync(path);
            await store.MoveScreenLayerAsync("display", visualization.Id, true);
            Check(await File.ReadAllTextAsync(path) == before, "G-Force cannot be moved above media or mats.");
            var result = JsonNode.Parse(before)!;
            Check(result["Custom"]!.GetValue<int>() == 17 && result["DisplayScreens"]!["display"]!.AsArray().Single(screen => screen!["Id"]!.GetValue<string>() == "a")!["CustomScreen"]!.GetValue<int>() == 9 &&
                ExperienceOptions.Parse(before).ScreensFor("display").Single(screen => screen.Id == "a").Roles == "Entry", "Layer saves preserve roles and unknown configuration.");
            result["configurationMode"] = false;
            await File.WriteAllTextAsync(path, result.ToJsonString());
            try { await store.MoveScreenLayerAsync("display", "a", false); throw new Exception("Layer move worked outside configuration mode."); }
            catch (PointEditConflictException) { }
            Console.WriteLine("Passed creation/layer ordering across video and mat types, G-Force bottom protection, safe saves and configuration-mode gating.");
        }
        finally { Directory.Delete(folder, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
