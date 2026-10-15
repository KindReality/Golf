using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Controller.Application;
using Controller.Diagnostics;
using Controller.Networking;
using PhotoBooth.Networking;

internal static class LiveChecks
{
    private static void Check(bool condition, string text) { if (!condition) throw new Exception(text); Console.WriteLine("PASS " + text); }
    private static ExperienceInventory Inventory() => new()
    {
        InstanceId = Guid.NewGuid().ToString("N"), Hostname = Environment.MachineName, DeviceName = "live-peer",
        Screens = [new() { Id = "one", Name = "screen1", Display = @"\\.\DISPLAY1", DisplayLabel = "Projector", Available = true, Roles = "Scary,Front" }, new() { Id = "locked", Name = "Eye", Display = @"\\.\DISPLAY1", DisplayLabel = "Projector", Available = true, MediaBound = true, Roles = "Locked" }],
        Speakers = [new() { Id = "speaker-a", Name = "Left", Channels = 2, Roles = "Subwoofer,Front" }, new() { Id = "speaker-b", Name = "Right", Channels = 2, Roles = "subwoofer" }],
        VideoFiles = ["Eye.mp4"], AudioFiles = ["Bell.wav"]
    };
    public static async Task Run()
    {
        foreach (var name in new[] { "Tablet.png", "Speaker.png" })
        {
            using var stream = typeof(LivePlayback).Assembly.GetManifestResourceStream("Controller.Assets." + name)!;
            using var bitmap = new System.Drawing.Bitmap(stream);
            Check(bitmap.GetPixel(0,0).A == 0, "Live " + name + " has a transparent background");
        }
        var inventory = Inventory(); var options = new BroadcastOptions { DeviceName = "live-controller", UdpSendAttempts = 1 };
        var peers = new[] { new ExperiencePeer(inventory, true, false) };
        LiveTarget screen = new(inventory.Hostname, "one", @"\\.\DISPLAY1"); LiveTarget speaker = new(inventory.Hostname, "speaker-a");
        var video = LivePlayback.Build(options, peers, [screen, speaker], "Eye.mp4", 100, true);
        var message = BroadcastEnvelope.Parse(video.Commands.Single());
        Check(message.Context?.Item == "Live" && message.Device == "live-peer" && message.Body.GetProperty("screen").GetString() == @"\\.\DISPLAY1/screen1" && !message.Body.GetProperty("loop").GetBoolean() && message.Body.GetProperty("mute").GetBoolean(), "Live video routes once to the selected device/display/screen");
        var renamed = inventory with { Screens = [inventory.Screens[0] with { Name = "renamed" }] };
        Check(BroadcastEnvelope.Parse(LivePlayback.Build(options, [new(renamed, true, false)], [screen], "Eye.mp4", 100, true).Commands.Single()).Body.GetProperty("screen").GetString()!.EndsWith("/renamed"), "Stable IDs follow the current screen name");
        var audio = LivePlayback.Build(options, peers, [screen, speaker], "Bell.wav", 35, true);
        var categorized=inventory with{VideoFiles=["Ghosts/Eye.mp4","Witches/Eye.mp4"],AudioFiles=["Ghosts/Bell.wav"]};
        categorized.Validate();
        var nestedVideo=LivePlayback.Build(options,[new(categorized,true,false)],[screen],"Witches/Eye.mp4",100,true);
        Check(BroadcastEnvelope.Parse(nestedVideo.Commands.Single()).Body.GetProperty("value").GetString()=="Witches/Eye.mp4","Live playback retains category paths when filenames repeat");
        var nestedAudio=LivePlayback.BuildRoles(options,[new(categorized,true,false)],["subwoofer"],"Ghosts/Bell.wav",35,true);
        Check(BroadcastEnvelope.Parse(nestedAudio.Commands.Single()).Body.GetProperty("value").GetString()=="Ghosts/Bell.wav","Live role audio retains its category path");
        message = BroadcastEnvelope.Parse(audio.Commands.Single());
        Check(message.Body.GetProperty("audioDevices")[0].GetString() == "Left" && message.Body.GetProperty("volume").GetInt32() == 35, "Audio routes independently with requested volume");
        message = BroadcastEnvelope.Parse(LivePlayback.Build(options, peers, [speaker, new(inventory.Hostname, "speaker-b"), speaker], "Bell.wav", 100, true).Commands.Single());
        Check(message.Body.GetProperty("audioDevice").GetString() == "*", "All selected outputs share one coordinated wildcard request without duplicate outputs");
        Check(LivePlayback.Build(options, [new(inventory, false, false)], [screen], "Eye.mp4", 100, true).Commands.Length == 0 && LivePlayback.Build(options, [new(inventory, true, true)], [screen], "Eye.mp4", 100, true).Commands.Length == 0, "Offline and ambiguous aliases cannot receive Live commands");
        Check(LivePlayback.Build(options, peers, [new(inventory.Hostname, "locked", @"\\.\DISPLAY1")], "Eye.mp4", 100, true).Commands.Length == 0, "Media-bound screens cannot be overridden");
        Check(LivePlayback.Build(options, peers, [screen], "Missing.mp4", 100, true).Skipped.Length == 1, "Playback requires media already local to its target");
        var roles = LivePlayback.Roles(peers);
        Check(roles.Single(r => r.Name.Equals("subwoofer", StringComparison.OrdinalIgnoreCase)) is { Screens: 0, Speakers: 2 } && roles.Count(r => r.Name.Equals("subwoofer", StringComparison.OrdinalIgnoreCase)) == 1, "Discovered roles combine duplicate casing and count both subwoofer speakers");
        Check(roles.Single(r => r.Name == "Front") is { Screens: 1, Speakers: 1 } && roles.Single(r => r.Name == "Locked").Screens == 0, "Role counts distinguish playable video/audio and exclude media-bound screens");
        var second = inventory with { Hostname = "other-experience", DeviceName = "other-peer", InstanceId = Guid.NewGuid().ToString("N") };
        var rolePeers = new[] { peers[0], new ExperiencePeer(second, true, false) };
        var roleAudio = LivePlayback.BuildRoles(options, rolePeers, ["subwoofer", "Front", "SUBWOOFER"], "Bell.wav", 42, true);
        var roleMessages = roleAudio.Commands.Select(bytes => BroadcastEnvelope.Parse(bytes)).ToArray();
        Check(roleMessages.Length == 2 && roleMessages.Select(m => m.Device).ToHashSet().SetEquals(["live-peer", "other-peer"]) && roleMessages.All(m => m.Body.GetProperty("roles").GetString() == "subwoofer,Front" && m.Body.GetProperty("volume").GetInt32() == 42 && !m.Body.GetProperty("loop").GetBoolean() && !m.Body.GetProperty("mute").GetBoolean() && m.Context?.Item == "Live"), "Live role audio reaches both Experience PCs once with requested volume, normal priority and no looping");
        Check(roleMessages.All(m => !m.Body.TryGetProperty("audioDevices", out _) && !m.Body.TryGetProperty("audioDevice", out _) && !m.Body.TryGetProperty("screen", out _)), "Role requests leave output selection and overlapping-role deduplication to Experience");
        message = BroadcastEnvelope.Parse(LivePlayback.BuildRoles(options, peers, ["Front"], "Eye.mp4", 100, false).Commands.Single());
        Check(message.Body.GetProperty("experience").GetString() == "video" && !message.Body.GetProperty("mute").GetBoolean() && !message.Body.TryGetProperty("screen", out _), "Live role video preserves mute choice and targets screens by role");
        Check(LivePlayback.BuildRoles(options, peers, ["subwoofer"], "Eye.mp4", 100, true).Commands.Length == 0 && LivePlayback.BuildRoles(options, peers, ["Locked"], "Eye.mp4", 100, true).Commands.Length == 0, "Audio-only roles never select video defaults and locked screens remain protected");
        Check(LivePlayback.BuildRoles(options, peers, [], "Bell.wav", 100, true).Commands.Length == 0 && LivePlayback.BuildRoles(options, peers, ["missing"], "Bell.wav", 100, true).Skipped.Length == 1, "Empty or unknown roles do not fall back to all outputs");
        var missingMediaPeer = second with { AudioFiles = [] };
        var partial = LivePlayback.BuildRoles(options, [peers[0], new(missingMediaPeer, true, false), new(second with { DeviceName = "offline" }, false, false)], ["subwoofer"], "Bell.wav", 100, true);
        Check(partial.Commands.Length == 1 && partial.Skipped.Length == 2, "Live roles skip offline or unsynchronized peers while still sending to eligible peers");
        Check(LivePlayback.BuildRoles(options, [new(inventory, true, true)], ["subwoofer"], "Bell.wav", 100, true).Commands.Length == 0 && LivePlayback.Roles([new(inventory, false, false)]).All(r => r.Screens + r.Speakers == 0), "Ambiguous identities are skipped and offline roles have zero selectable outputs");
        try { (inventory with { AudioFiles = ["../Bell.wav"] }).Serialize(); throw new Exception("Unsafe inventory accepted"); } catch (ArgumentException) { }
        try { ExperienceDiscovery.Read(BroadcastEnvelope.Parse(BroadcastEnvelope.Create(options, "event", "*", new { @event = ExperienceDiscovery.Event, input = "experience", hostname = Environment.MachineName }))); throw new Exception("Malformed announcement accepted"); } catch (ArgumentException) { }
        await using var server = new SnapshotHttpServer(); var bytes = inventory.Serialize(); var revision = ExperienceInventory.Revision(bytes); server.Publish(bytes, revision); server.Bind(0);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
        using var response = await http.GetAsync("http://127.0.0.1:" + server.Port + "/inventory");
        Check(response.IsSuccessStatusCode && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes), "TCP inventory returns the exact published snapshot");
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:" + server.Port + "/inventory"); conditional.Headers.IfNoneMatch.Add(response.Headers.ETag!);
        using var unchanged = await http.SendAsync(conditional); Check(unchanged.StatusCode == HttpStatusCode.NotModified, "Inventory ETag supports unchanged snapshots");
        using var unknown = await http.GetAsync("http://127.0.0.1:" + server.Port + "/anything"); Check(unknown.StatusCode == HttpStatusCode.NotFound, "Inventory endpoint exposes no arbitrary files");
        await using var registry = new ExperienceRegistry(new ActivityLog());
        var announcement = BroadcastEnvelope.Parse(ExperienceDiscovery.Announce(new() { DeviceName = inventory.DeviceName }, inventory.InstanceId, revision, server.Port)) with { SenderAddress = "127.0.0.1" };
        registry.Receive(announcement);
        await Until(() => registry.Snapshot().Length == 1);
        Check(registry.Snapshot()[0].Usable && registry.Snapshot()[0].Inventory.Speakers.Length == 2 && LivePlayback.Roles(registry.Snapshot()).Any(r => r.Name.Equals("subwoofer", StringComparison.OrdinalIgnoreCase) && r.Speakers == 2), "Registry downloads announced inventory including shared roles");
        var updated = renamed with { Speakers = [inventory.Speakers[0] with { Roles = "Rear" }, inventory.Speakers[1] with { Roles = "Rear" }] };
        var nextBytes = updated.Serialize(); var nextRevision = ExperienceInventory.Revision(nextBytes); server.Publish(nextBytes, nextRevision);
        registry.Receive(BroadcastEnvelope.Parse(ExperienceDiscovery.Announce(new() { DeviceName = inventory.DeviceName }, inventory.InstanceId, nextRevision, server.Port)) with { SenderAddress = "127.0.0.1" });
        await Until(() => registry.Snapshot()[0].Inventory.Screens[0].Name == "renamed"); Check(true, "Changed announcement refreshes target metadata");
        Check(LivePlayback.Roles(registry.Snapshot()).Any(r => r.Name == "Rear" && r.Speakers == 2) && !LivePlayback.Roles(registry.Snapshot()).Any(r => r.Name.Equals("subwoofer", StringComparison.OrdinalIgnoreCase)), "Changed Experience roles immediately replace the old discovered list");
        using var slow = new TcpClient(); await slow.ConnectAsync(IPAddress.Loopback, server.Port); await slow.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /inventory HTTP/1.1\r\n"));
        await server.DisposeAsync(); Check(true, "Shutdown cancels incomplete inventory clients");
    }
    private static async Task Until(Func<bool> ready) { using var timeout = new CancellationTokenSource(5000); while (!ready()) await Task.Delay(20, timeout.Token); }
}
