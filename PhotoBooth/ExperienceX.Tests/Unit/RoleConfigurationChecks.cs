using System.Text.Json.Nodes;
using ExperienceX;
using PhotoBooth.Networking;

internal static class RoleConfigurationChecks
{
    public static async Task Run()
    {
        Check(RoleList.Parse("  Front, Scary, FRONT,, ").SequenceEqual(["Front", "Scary"]) && RoleList.Parse("").Length == 0, "Role lists trim, deduplicate and allow zero roles.");
        Check(RoleList.Matches("Front, Scary", "scary") && !RoleList.Matches("Front", "Rear"), "Role matching ignores case.");
        Check(RoleList.Matches("", "^subwoofer,*") && RoleList.Matches("Front", "^subwoofer,*") &&
            !RoleList.Matches("Front,SUBWOOFER", "^subwoofer,*") && RoleList.Matches("Subwoofer", "*,^subwoofer"),
            "The first matching rule decides: an initial wildcard includes all, while an earlier exclusion denies its role.");
        Check(RoleList.Matches("Front", "^subwoofer,Front") && !RoleList.Matches("Rear", "^subwoofer,Front") &&
            RoleList.Matches("Front,Subwoofer", "Front,^subwoofer,Subwoofer") &&
            !RoleList.Matches("Front,Subwoofer", "^subwoofer,Front") && !RoleList.Matches("Rear", "Front,^subwoofer"),
            "Earlier positive and negative rules take precedence; unmatched outputs stay unselected.");
        Check(RoleList.RandomChoices("Front,^subwoofer,Rear").SequenceEqual(["Front,^subwoofer", "^subwoofer,Rear"]), "Random role choices preserve the original order of the selected positive rule and every exclusion.");
        Check(!RoleList.Matches("Front", "^subwoofer") && !RoleList.Matches("Front", "^*,*") && RoleList.Matches("Front", "Front,^*"),
            "Negative-only rules default to no selection, and a negative wildcard respects earlier positive rules.");
        foreach (var invalid in new[] { "^", "^^subwoofer,*", "^ subwoofer,*", "Front*" }) Reject(() => RoleList.ParseTargets(invalid));
        foreach (var invalid in new[] { "*", "front\nrear", new string('x', 65), string.Join(",", Enumerable.Range(0, 257)) })
            Reject(() => RoleList.Parse(invalid));
        Reject(() => RoleList.Parse("^subwoofer"));
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
        var excludedOptions = options with { AudioDevices = new(options.AudioDevices) { ["id-b"] = new() { Roles = "Subwoofer" } } };
        var excludedRequest = ExperienceRequest.Parse("{\"experience\":\"audio\",\"value\":\"Bell.wav\",\"roles\":\"^subwoofer,*\"}"u8);
        Check(AudioDeviceSelection.Resolve(excludedRequest, excludedOptions, outputs, "id-b").Devices.Select(d => d.Id).SequenceEqual(["id-a", "id-c"]),
            "Actual audio routing excludes subwoofers and includes unassigned endpoints without a default fallback.");
        Check(AudioDeviceSelection.Resolve(excludedRequest with { Roles = "*" }, excludedOptions, outputs, "id-b").Devices.Length == 3,
            "A wildcard includes every speaker, including Subwoofer outputs.");
        Check(AudioDeviceSelection.Resolve(excludedRequest with { Roles = "Subwoofer" }, excludedOptions, outputs, "id-b").Devices.Select(d => d.Id).SequenceEqual(["id-b"]),
            "A Subwoofer role request selects only assigned subwoofers.");
        Check(AudioDeviceSelection.Resolve(excludedRequest with { Roles = "Ambient,Subwoofer,ambient" }, excludedOptions, outputs, "id-b").Devices.Select(d => d.Id).SequenceEqual(["id-a", "id-b"]),
            "Combined roles select each endpoint once even when it matches multiple roles.");
        Check(AudioDeviceSelection.Resolve(excludedRequest with { Roles = "Subwoofer" }, excludedOptions, [outputs[0], outputs[2]], "id-a").Devices.Length == 0,
            "An unavailable Subwoofer role never falls back to another output.");
        var videoOptions = options with { DisplayScreens = new() { ["DISPLAY1"] = [new() { Id = "a", Roles = "Front" }, new() { Id = "b", Roles = "Subwoofer" }, new() { Id = "c" }] } };
        Check(ScreenSelection.VideoOutputs(request with { Roles = "^subwoofer,*" }, videoOptions, ["DISPLAY1"], "DISPLAY1").Count == 2,
            "Video role targeting applies the same wildcard and exclusion rules.");
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
            await store.EnsureAudioDevicesAsync(devices, "ID-A");
            var seeded = ExperienceOptions.Parse(await File.ReadAllTextAsync(file));
            Check(seeded.AudioDevices["id-a"].Name == "Front" && seeded.AudioDevices["id-a"].Roles == "Subwoofer" && seeded.AudioDevices["id-b"].Roles == "" && seeded.ScreensFor("DISPLAY1")[0].Roles == "",
                "New default audio output receives the Subwoofer role; other hardware keeps empty roles and descriptive names.");
            var root = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
            root["AudioDevices"]!["id-a"]!["Roles"] = "";
            root["AudioDevices"]!["id-b"]!["Roles"] = "Subwoofer";
            await File.WriteAllTextAsync(file, root.ToJsonString());
            await store.EnsureAudioDevicesAsync([devices[0], new("id-c", "Side", 2)], "id-a");
            var before = await File.ReadAllTextAsync(file);
            await store.EnsureAudioDevicesAsync([devices[0], new("id-c", "Side", 2)], "id-c");
            var retained = ExperienceOptions.Parse(before);
            Check(await File.ReadAllTextAsync(file) == before && retained.AudioDevices["id-a"].Roles == "" && retained.AudioDevices["id-b"].Roles == "Subwoofer" && retained.AudioDevices["id-c"].Roles == "" && JsonNode.Parse(before)!["Custom"]!.GetValue<int>() == 17,
                "Clearing or moving the Subwoofer role persists, including disconnected outputs and default-output changes, without repeated rewrites.");
            await store.EnsureAudioDevicesAsync([new("id-d", "New default", 2)], "id-d");
            Check(ExperienceOptions.Parse(await File.ReadAllTextAsync(file)).AudioDevices["id-d"].Roles == "Subwoofer", "A newly detected default endpoint receives its initial role.");
            await store.EnsureAudioDevicesAsync([new("id-e", "No default", 2)], null);
            Check(ExperienceOptions.Parse(await File.ReadAllTextAsync(file)).AudioDevices["id-e"].Roles == "", "No Subwoofer role is guessed when the default endpoint is unavailable.");
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("Passed role configuration, validation, physical/virtual speaker membership and atomic hardware population.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid roles were accepted."); }
}
