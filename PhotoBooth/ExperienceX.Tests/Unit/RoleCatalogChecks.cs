using System.Text.Json;
using ExperienceX;
using PhotoBooth.Networking;
using Forms = System.Windows.Forms;

internal static class RoleCatalogChecks
{
    internal static async Task Run()
    {
        Check(RoleCatalog.Names(["^Subwoofer,*,Front", "front,Other", "^*"]).SequenceEqual(["Front", "Other", "Subwoofer"]),
            "Suggestions strip exclusion operators, omit wildcards, sort and deduplicate names.");
        var own = new ControllerSharedDefinitions { ControllerId = Guid.NewGuid().ToString("N"), ControllerName = "local", Hostname = Environment.MachineName, Roles = ["SceneOnly", "Subwoofer"] };
        var remote = new ControllerSharedDefinitions { ControllerId = Guid.NewGuid().ToString("N"), ControllerName = "remote", Hostname = "OTHERPC", Roles = ["RemoteOnly", "sceneonly"] };
        var applied = 0; var cache = new ControllerDefinitionsCache(_ => { applied++; return Task.CompletedTask; });
        var catalog = new ControllerCatalog { LocalControllerId = own.ControllerId, Controllers = [own, remote] };
        await cache.ApplyAsync(JsonSerializer.Deserialize<ControllerCatalog>(catalog.Serialize(), ControllerSharedDefinitions.Json)!);
        await RoundTrip(catalog);
        Check(cache.Roles.SequenceEqual(["RemoteOnly", "SceneOnly", "Subwoofer"]) && applied == 2,
            "Experience receives a deduplicated union of local and remote Controller roles through the existing catalog.");
        await cache.ApplyAsync(catalog with { Controllers = [own with { Roles = ["NewSceneRole"] }, remote] });
        Check(cache.Roles.Contains("NewSceneRole") && !cache.Roles.Contains("Subwoofer") && applied == 3,
            "A changed definition updates role suggestions without changing unchanged Controllers.");
        foreach (var invalid in new[] { new[] { "*" }, ["^Front"], ["Front,Rear"], ["Front", "front"], [""] })
        {
            try { (own with { Roles = invalid }).Validate(); throw new Exception("Invalid shared role accepted."); } catch (ArgumentException) { }
        }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var screen = new ScreenOptions { Id = "a", Name = "screen1", Roles = "KeepMe" };
                var options = new ExperienceOptions { DisplayScreens = new() { ["display"] = [screen] }, AudioDevices = new() { ["speaker"] = new() { Roles = "Subwoofer" } } };
                var known = ScreenPropertiesDialog.KnownRoles(options, cache.Roles);
                using var dialog = new ScreenPropertiesDialog([screen], [], known, "display", screen);
                var field = Descendants(dialog).OfType<Forms.CheckedListBox>().Single(c => c.AccessibleName == "Roles");
                Check(field.Items.Cast<string>().Contains("RemoteOnly") && field.Items.Cast<string>().Contains("NewSceneRole") && field.Items.Cast<string>().Contains("Subwoofer"),
                    "Screen properties offers roles from Controllers and local audio assignments before those roles are assigned to a screen.");
                Check(field.CheckedItems.Cast<string>().SequenceEqual(["KeepMe"]) && options.DisplayScreens["display"][0].Roles == "KeepMe",
                    "Showing shared suggestions does not assign roles or alter existing selections.");
                field.SetItemChecked(field.Items.IndexOf("NewSceneRole"), true);
                Check(RoleList.Parse(dialog.Roles).Contains("NewSceneRole"), "A predetermined role can be selected normally.");
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task; thread.Join();
    }
    private static async Task RoundTrip(ControllerCatalog catalog)
    {
        var root = Path.Combine(Path.GetTempPath(), "RoleCatalog-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "appsettings.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new ExperienceOptions { DisplayScreens = new() { ["display"] = [new() { Roles = "KeepMe" }] } }));
            var store = new PointConfigurationStore(path); var client = new ControllerDefinitionsCache(store.ApplyControllerDefinitionsAsync);
            await client.ApplyAsync(catalog);
            var saved = ExperienceOptions.Parse(await File.ReadAllTextAsync(path));
            var restarted = new ControllerDefinitionsCache(_ => Task.CompletedTask); restarted.Seed(saved.ControllerDefinitions.Values);
            Check(restarted.Roles.SequenceEqual(client.Roles) && saved.DisplayScreens["display"][0].Roles == "KeepMe",
                "Cached role suggestions survive restart without altering screen assignments.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static IEnumerable<Forms.Control> Descendants(Forms.Control control) => control.Controls.Cast<Forms.Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
