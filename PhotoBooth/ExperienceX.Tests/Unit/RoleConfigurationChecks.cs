using System.Text.Json.Nodes;
using ExperienceX;
using PhotoBooth.Networking;

internal static class RoleConfigurationChecks
{
    public static async Task Run()
    {
        Check(RoleList.Parse("  Front, Scary, FRONT,, ").SequenceEqual(["Front", "Scary"]) && RoleList.Parse("").Length == 0, "Role lists trim, deduplicate and allow zero roles.");
        Check(RoleList.Matches("Front, Scary", "scary") && !RoleList.Matches("Front", "Rear"), "Role matching ignores case.");
        foreach (var invalid in new[] { "*", "front\nrear", new string('x', 65), string.Join(",", Enumerable.Range(0, 257)) })
            Reject(() => RoleList.Parse(invalid));
        var options = new ExperienceOptions
        {
            AudioDevices = new()
            {
                ["id-a"] = new() { Name = "Front", Roles = "Scary" },
                ["Front"] = new() { Roles = "Welcome" },
                ["Room"] = new() { Roles = "Ambient" }
            },
            VirtualAudioDevices = new() { ["Room"] = ["id-a", "Rear"] },
            DisplayScreens = new() { ["DISPLAY1"] = [new() { Roles = "Scary,Welcome" }] }
        };
        options.Validate();
        var request = ExperienceRequest.Parse("{\"experience\":\"video\",\"value\":\"Eye.mp4\",\"roles\":\"welcome, scary\"}"u8);
        Check(ScreenSelection.VideoOutputs(request, options, ["DISPLAY1", "DISPLAY1", "DISPLAY2"], "DISPLAY2").Count == 1, "Role video targets all matching available screens once, without default-display restriction.");
        Check(ScreenSelection.VideoOutputs(request with { Roles = "absent" }, options, ["DISPLAY1"], "DISPLAY1").Count == 0, "Missing roles never fall back to a default screen.");
        AudioOutput[] outputs = [new("id-a", "Front", 2), new("id-b", "Rear", 2), new("id-c", "Side", 2)];
        var selection = AudioDeviceSelection.Resolve(request with { Experience = "audio", Roles = "ambient, welcome" }, options, outputs, "id-c");
        Check(selection.Devices.Select(device => device.Id).SequenceEqual(["id-a", "id-b"]), "Role audio selects matching speakers once, including group roles and excluding the default speaker.");
        Check(AudioDeviceSelection.Resolve(request with { Experience = "audio", Roles = "absent" }, options, outputs, "id-c").Devices.Length == 0, "Missing audio roles never fall back to default audio.");
        foreach (var target in new[] { "\"screen\":\"DISPLAY1/screen1\"", "\"displayName\":\"DISPLAY1\"", "\"audioDevice\":\"*\"", "\"audioDevices\":[\"id-a\"]" })
            Reject(() => ExperienceRequest.Parse(System.Text.Encoding.UTF8.GetBytes("{\"experience\":\"video\",\"value\":\"Eye.mp4\",\"roles\":\"scary\"," + target + "}")));
        Check(RoleList.Parse(AudioDeviceOptions.RolesFor(new("id-a", "Front", 2), options)).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(["Scary", "Welcome", "Ambient"]), "Physical ID, name and virtual-group roles combine without duplicates.");
        Check(AudioDeviceOptions.RolesFor(new("id-b", "Rear", 2), options) == "Ambient", "Virtual-group roles apply to member speakers.");
        Reject(() => (options with { AudioDevices = null! }).Validate());
        Reject(() => (options with { AudioDevices = new() { ["id"] = new() { Roles = "*" } } }).Validate());
        Reject(() => (options with { DisplayScreens = new() { ["DISPLAY1"] = [new() { Roles = "*" }] } }).Validate());
        var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-role-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "appsettings.json");
            await File.WriteAllTextAsync(file, "{\"Custom\":17}");
            var store = new PointConfigurationStore(file);
            store.EnsureDisplays(["DISPLAY1"]);
            AudioOutput[] devices = [new("id-a", "Front", 2), new("id-b", "Rear", 2)];
            await store.EnsureAudioDevicesAsync(devices);
            var seeded = ExperienceOptions.Parse(await File.ReadAllTextAsync(file));
            Check(seeded.AudioDevices["id-a"].Name == "Front" && seeded.AudioDevices["id-a"].Roles == "" && seeded.ScreensFor("DISPLAY1")[0].Roles == "", "New hardware stores empty configurable roles and descriptive speaker names.");
            var root = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
            root["AudioDevices"]!["id-a"]!["Roles"] = "Scary";
            await File.WriteAllTextAsync(file, root.ToJsonString());
            await store.EnsureAudioDevicesAsync([devices[0], new("id-c", "Side", 2)]);
            var before = await File.ReadAllTextAsync(file);
            await store.EnsureAudioDevicesAsync([devices[0], new("id-c", "Side", 2)]);
            Check(await File.ReadAllTextAsync(file) == before && ExperienceOptions.Parse(before).AudioDevices["id-a"].Roles == "Scary" && JsonNode.Parse(before)!["Custom"]!.GetValue<int>() == 17, "Hardware population preserves roles, disconnected outputs and unrelated configuration without repeated rewrites.");
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("Passed role configuration, validation, physical/virtual speaker membership and atomic hardware population.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid roles were accepted."); }
}
