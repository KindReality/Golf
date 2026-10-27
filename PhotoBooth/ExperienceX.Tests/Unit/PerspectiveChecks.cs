using System.Text.Json;
using ExperienceX;
using PhotoBooth.Networking;

internal static class PerspectiveChecks
{
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "PerspectiveChecks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var perspective = new PerspectiveDefinition { Name = "bottom-third", Rectangle = new() { Y = 66.67, Height = 33.33 } };
            var local = new ControllerSharedDefinitions { ControllerId = Guid.NewGuid().ToString("N"), ControllerName = "local", Hostname = Environment.MachineName, Perspectives = [perspective] };
            var remote = local with { ControllerId = Guid.NewGuid().ToString("N"), ControllerName = "remote", Hostname = "remote" };
            var path = Path.Combine(root, "appsettings.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new ExperienceOptions { DisplayScreens = new() { ["display1"] = [new ScreenOptions { Perspective = remote.Select(perspective) }] } }, ConfigurationSchema.Json));
            var store = new PointConfigurationStore(path);
            var client = new ControllerDefinitionsCache(d => store.ApplyControllerDefinitionsAsync(d));
            ControllerCatalog Catalog() => new() { LocalControllerId = local.ControllerId, Controllers = [local, remote] };
            await client.ApplyAsync(Catalog());
            Check(client.Perspectives.Count == 2, "Local Controller catalog includes distinct perspectives from multiple Controllers");
            Check(client.LocalController?.ControllerId == local.ControllerId, "New perspectives can be saved only to the local Controller");
            remote = remote with { Perspectives = [perspective with { Name = "renamed", Rectangle = new() { X = 25, Width = 50 } }] };
            await client.ApplyAsync(Catalog());
            var before = ExperienceOptions.Parse(File.ReadAllText(path)).ScreensFor("display1")[0].Perspective!;
            Check(before.Name == "renamed" && before.Rectangle.Width == 50 && before.PerspectiveId == perspective.Id, "Remote perspective updates relayed locally retain identity and update screens");
            remote = remote with { Perspectives = [] }; await client.ApplyAsync(Catalog());
            var retained = ExperienceOptions.Parse(File.ReadAllText(path)).ScreensFor("display1")[0].Perspective!;
            Check(!retained.Available && retained.Rectangle == before.Rectangle, "Deleted perspectives retain their last rectangle and become unavailable");
            var cached = new ControllerDefinitionsCache(_ => Task.CompletedTask);
            cached.Seed(ExperienceOptions.Parse(File.ReadAllText(path)).ControllerDefinitions.Values);
            Check(cached.Perspectives.Count == 1 && cached.LocalController is null, "Cached perspectives survive restart without claiming a live local Controller");
            foreach (var rectangle in new[] { new PerspectiveRectangle { X = -1 }, new() { X = 90, Width = 11 }, new() { Height = 0 }, new() { X = double.NaN } })
            { try { rectangle.Validate(); throw new Exception("Invalid crop accepted."); } catch (ArgumentException) { } }
            Check(true, "Invalid percentage rectangles are rejected");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string text) { if (!condition) throw new Exception(text); Console.WriteLine("PASS " + text); }
}
