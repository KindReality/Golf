using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Controller.Configuration;
using Controller.Diagnostics;
using Controller.Scheduling;
using PhotoBooth.Networking;

internal static class RolePlaylistChecks
{
    public static async Task Run()
    {
        var video = new PlaylistCommand { Payload = JsonSerializer.SerializeToElement(new { experience = "video", value = "Eye.mp4", screen = "Projector/screen1", loop = false, mute = true }) };
        var audio = new PlaylistCommand { Payload = JsonSerializer.SerializeToElement(new { experience = "audio", value = "Bell.wav", audioDevice = "Room", volume = 35 }) };
        var item = new PlaylistItem { Name = "scare", Roles = "Scary, Entry", Commands = [video, audio] };
        var scene = new SceneOptions { Name = "idle", AmbientPlaylist = "background" };
        var options = new ControllerOptions
        {
            DeviceName = "role-controller", Scenes = [scene],
            Playlists = [new() { Name = "background", Type = "ambient" }, new() { Name = "show", Items = [item] }]
        };
        static ControllerOptions Valid(ControllerOptions settings) => ControllerOptions.Parse(JsonSerializer.Serialize(settings, ControllerOptions.Json), new("Development", 1000));
        options = Valid(options);
        var recorder = new Recorder();
        await using (var engine = new PlaylistEngine(options, recorder, new ActivityLog()))
        {
            await engine.Run("show").Completion;
            Check(recorder.Commands.Count == 2 && recorder.Commands.All(command => command.Device == "*" && ControllerOptions.Text(command.Payload, "roles") == "Scary,Entry" && command.Context?.IsAmbient == false), "Role-only items broadcast video and audio commands with normal priority.");
            Check(!recorder.Commands.First().Payload.TryGetProperty("screen", out _) && !recorder.Commands.Last().Payload.TryGetProperty("audioDevice", out _) && recorder.Commands.Last().Payload.GetProperty("volume").GetInt32() == 35, "Role targeting replaces direct selectors while preserving media parameters.");
        }
        var scoped = PlaylistRoleTarget.Apply(video with { Device = "projection-pc" }, "Scary");
        Check(scoped.Device == "projection-pc", "An explicit device limits role targeting to that computer.");
        Check(ControllerOptions.Text(PlaylistRoleTarget.Apply(audio, "^subwoofer,*").Payload, "roles") == "^subwoofer,*" &&
            !RoleList.Matches("subwoofer", ControllerOptions.Text(PlaylistRoleTarget.Apply(audio, "^subwoofer,*").Payload, "roles")),
            "Audio exclusion rules are sent unchanged and take precedence over the wildcard.");
        Check(PlaylistRoleTarget.Select(item with { Roles = "^subwoofer,Left,Right", BroadcastMode = "Random" }, null, count => { Check(count == 2, "Exclusions are not random choices."); return 1; }) == "^subwoofer,Right",
            "Random role targeting retains every exclusion with the selected positive role.");
        var loop = video with { Payload = JsonSerializer.SerializeToElement(new { experience = "video", value = "Eye.mp4", loop = true, mute = true }) };
        var loopOptions = Valid(options with { Playlists = [new() { Name = "background", Type = "ambient" }, new() { Name = "loop", Items = [item with { Commands = [loop] }] }] });
        recorder = new();
        await using (var engine = new PlaylistEngine(loopOptions, recorder, new ActivityLog()))
        {
            engine.SetSceneContext(new("idle"));
            await engine.Run("loop").Completion;
            engine.SetSceneContext(new("active"));
            Check(recorder.Commands.Count == 1 && recorder.Commands.Single().Device == "*" && VideoCommand.IsLoop(recorder.Commands.Single()),
                "Role-bound loops remain low-priority broadcasts; changing scene context sends no playback stop.");
        }
        var child = new PlaylistOptions { Name = "child", Items = [new() { Name = "inherited", Commands = [video with { Device = "*" }] }, item with { Name = "override", Roles = "Welcome", Commands = [audio] }] };
        var ambientItem = new PlaylistItem { Name = "nested", Roles = "Ambient", MinIntervalMilliseconds = 20, MaxIntervalMilliseconds = 20, Commands = [new() { Playlist = "child" }] };
        options = Valid(options with { Playlists = [new() { Name = "background", Type = "ambient", Items = [ambientItem] }, child] });
        recorder = new();
        await using (var engine = new PlaylistEngine(options, recorder, new ActivityLog()))
        {
            await engine.RunAmbientItem(options.Playlist("background"), ambientItem, options, "idle", CancellationToken.None).Completion;
            Check(recorder.Commands.Select(command => ControllerOptions.Text(command.Payload, "roles")).SequenceEqual(["Ambient", "Welcome"]) && recorder.Commands.All(command => command.Context?.IsAmbient == true), "Nested items inherit roles, can override them, and retain ambient priority.");
        }
        var explicitRole = video with { Payload = JsonSerializer.SerializeToElement(new { experience = "video", value = "Eye.mp4", roles = "Scary" }) };
        Check(PlaylistRoleTarget.Apply(explicitRole, "").Device == "*", "Individual role playback commands also support omitted device.");
        Reject(() => PlaylistRoleTarget.Apply(video with { Payload = JsonSerializer.SerializeToElement(new { experience = "video", value = "Eye.mp4", roles = "Scary", screen = "*" }) }, ""));
        Reject(() => PlaylistRoleTarget.Apply(video with { Payload = JsonSerializer.SerializeToElement(new { experience = "video", value = "Eye.mp4", roles = 42 }) }, ""));
        Reject(() => Valid(options with { Playlists = [new() { Name = "background", Type = "ambient" }, new() { Name = "invalid", Items = [item with { Roles = "^" }] }] }));
        var randomItem = new PlaylistItem { Name = "random", Roles = "Left,Right,left", BroadcastMode = "Random", MinIntervalMilliseconds = 20, MaxIntervalMilliseconds = 20, Commands = [video, audio] };
        var randomOptions = Valid(options with { Playlists = [new() { Name = "background", Type = "ambient", Items = [randomItem] }] });
        recorder = new(); var choices = 0;
        await using (var engine = new PlaylistEngine(randomOptions, recorder, new ActivityLog()) { ChooseRole = count => { Check(count == 2, "Random role candidates must be distinct ignoring case."); return choices++ == 0 ? 1 : 0; } })
        {
            await engine.RunAmbientItem(randomOptions.Playlist("background"), randomItem, randomOptions, "idle", CancellationToken.None).Completion;
            await engine.RunAmbientItem(randomOptions.Playlist("background"), randomItem, randomOptions, "idle", CancellationToken.None).Completion;
            Check(choices == 2 && recorder.Commands.Select(c => ControllerOptions.Text(c.Payload, "roles")).SequenceEqual(["Right", "Right", "Left", "Left"]), "Random chooses once per execution and retains the same roles for video and audio commands.");
            Check(recorder.Commands.All(c => c.Device == "*" && c.Context?.IsAmbient == true) && recorder.Commands.Where(c => ControllerOptions.Text(c.Payload, "experience") == "audio").All(c => c.Payload.GetProperty("volume").GetInt32() == 35), "Random role targeting preserves ambient priority, broadcast destinations and audio volume.");
        }
        var broadcastItem = randomItem with { BroadcastMode = "Broadcast", Roles = "Left,Right,Subwoofer,SUBWOOFER" };
        var broadcastOptions = Valid(randomOptions with { Playlists = [new() { Name = "background", Type = "ambient", Items = [broadcastItem] }] });
        recorder = new();
        await using (var engine = new PlaylistEngine(broadcastOptions, recorder, new ActivityLog()) { ChooseRole = _ => throw new Exception("Broadcast must not choose a role.") })
        {
            await engine.RunAmbientItem(broadcastOptions.Playlist("background"), broadcastItem, broadcastOptions, "idle", CancellationToken.None).Completion;
            Check(recorder.Commands.Count == 2 && recorder.Commands.All(c => ControllerOptions.Text(c.Payload, "roles") == "Left,Right,Subwoofer"), "Broadcast targets all distinct listed roles and never duplicates an existing subwoofer role.");
        }
        var nestedItem = randomItem with { Commands = [new() { Playlist = "child" }] };
        var nestedOptions = Valid(randomOptions with { Playlists = [new() { Name = "background", Type = "ambient", Items = [nestedItem] }, child] });
        recorder = new(); choices = 0;
        await using (var engine = new PlaylistEngine(nestedOptions, recorder, new ActivityLog()) { ChooseRole = _ => { ++choices; return 0; } })
        {
            await engine.RunAmbientItem(nestedOptions.Playlist("background"), nestedItem, nestedOptions, "idle", CancellationToken.None).Completion;
            Check(choices == 1 && recorder.Commands.Select(c => ControllerOptions.Text(c.Payload, "roles")).SequenceEqual(["Left", "Welcome"]), "Nested storyboards inherit the chosen role; explicit child roles override the inherited target.");
        }
        Check(ControllerOptions.Text(PlaylistRoleTarget.Apply(audio, "Subwoofer").Payload, "roles") == "Subwoofer" && PlaylistRoleTarget.Apply(audio, "") == audio,
            "Subwoofer is an ordinary role; empty roles preserve existing direct audio targeting.");
        Reject(() => Valid(randomOptions with { Playlists = [new() { Name = "background", Type = "ambient", Items = [randomItem with { Roles = "" }] }] }));
        Reject(() => Valid(randomOptions with { Playlists = [new() { Name = "background", Type = "ambient", Items = [randomItem with { BroadcastMode = "Unknown" }] }] }));
        _ = Valid(randomOptions with { Playlists = [new() { Name = "background", Type = "ambient", Items = [randomItem with { BroadcastMode = "Broadcast", Roles = "", Commands = [audio with { Device = "*" }] }] }] });
        var populated = ConfigurationDefaults.Populate(JsonSerializer.Serialize(options, ControllerOptions.Json), new("Development", 1000));
        using (var defaultsDocument = JsonDocument.Parse(populated))
            Check(defaultsDocument.RootElement.GetProperty("Playlists").EnumerateArray().SelectMany(p => p.GetProperty("Items").EnumerateArray()).All(i => i.TryGetProperty("BroadcastMode", out _) && i.TryGetProperty("Roles", out _)), "Configuration includes editable BroadcastMode and Roles defaults.");
        Console.WriteLine("Passed role routing, Random/Broadcast selection, ordered exclusions, nested inheritance, loop continuity and configuration validation.");
    }
    private sealed class Recorder : ICommandDispatcher
    {
        internal readonly ConcurrentQueue<PlaylistCommand> Commands = new();
        public Task<DispatchReceipt> SendAsync(PlaylistCommand command, ControllerOptions options, BroadcastChain chain, string requestId, CancellationToken token)
        {
            BroadcastEnvelope.Parse(BroadcastEnvelope.Create(options, command.Type, command.Device!, command.Payload, requestId, chain, command.Context));
            Commands.Enqueue(command);
            return Task.FromResult(new DispatchReceipt(Stopwatch.GetTimestamp()));
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid role target was accepted."); }
}
