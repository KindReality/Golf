using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Controller.Configuration;
using Controller.Networking;
using Controller.Scheduling;
using PhotoBooth.Networking;

internal static class RandomAudioChecks
{
    internal static async Task Run()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
        var audio = new PlaylistCommand { Device = "*", Payload = JsonSerializer.SerializeToElement(new { experience = "audio", value = "Whispers/First.wav", roles = "^subwoofer,*", volume = 65, mute = false }) };
        var a = new ExperiencePeer(new() { Hostname = "A", DeviceName = "alpha", AudioFiles = ["Whispers/First.wav"], Speakers = [new() { Id = "a", Name = "Left", Roles = "Whispers" }, new() { Id = "woofer", Name = "Bass", Roles = "Whispers,SUBWOOFER" }] }, true, false);
        var b = new ExperiencePeer(new() { Hostname = "B", DeviceName = "beta", AudioFiles = ["Whispers/First.wav"], Speakers = [new() { Id = "b", Name = "Right", Roles = "Whispers" }] }, true, false);
        var offline = b with { Online = false, Inventory = b.Inventory with { Hostname = "Offline", DeviceName = "offline" } };
        var ambiguous = b with { Ambiguous = true, Inventory = b.Inventory with { Hostname = "Duplicate" } };
        var missing = b with { Inventory = b.Inventory with { Hostname = "Missing", AudioFiles = [] } };
        ExperiencePeer[] peers = [a, b, offline, ambiguous, missing];
        var selected = PlaylistAudioTarget.Random(audio, peers, count => { Check(count == 2, "Only online non-subwoofer speakers with local media are eligible."); return 1; });
        Check(selected.Command.Device == "beta" && ControllerOptions.Text(selected.Command.Payload, "audioDevice") == "b" && selected.Command.Payload.GetProperty("volume").GetInt32() == 65, "One concrete output is selected across the network, preserving audio settings.");
        var first = PlaylistAudioTarget.Random(audio, peers, _ => 0);
        var repeat = PlaylistAudioTarget.Random(audio, peers, _ => 0);
        Check(first.Command.Device == "alpha" && repeat.Command.Device == "alpha", "Each file is selected independently; repeating a speaker is allowed.");
        var scoped = PlaylistAudioTarget.Random(audio with { Device = "beta" }, peers, count => { Check(count == 1, "Explicit device scoping limits the candidate pool."); return 0; });
        Check(scoped.SpeakerName == "Right", "Device-scoped requests retain the requested computer.");
        try { PlaylistAudioTarget.Random(audio, [a with { Online = false }], _ => 0); throw new Exception("An unavailable output was selected."); }
        catch (InvalidOperationException) { Console.WriteLine("PASS Missing eligible outputs are reported without broadcasting or fallback."); }
        var board = new PlaylistOptions { Name = "sequence", AudioOutputMode = "Random", DurationMilliseconds = 80, Items = [new() { Name = "First", Commands = [audio] }, new() { Name = "Second", DelayMilliseconds = 40, Commands = [audio] }] };
        var ambient = new PlaylistOptions { Name = "idle-ambient", Type = "ambient", Items = [new() { Name = "whispers", MinIntervalMilliseconds = 1, MaxIntervalMilliseconds = 1, Commands = [new() { Playlist = "sequence" }] }] };
        var options = new ControllerOptions { DeviceName = "test", Scenes = [new() { Name = "idle", AmbientPlaylist = "idle-ambient" }], Playlists = [ambient, board] };
        options = ControllerOptions.Parse(JsonSerializer.Serialize(options, ControllerOptions.Json), new("Development", 1000));
        var recorder = new Recorder(); var choose = 0;
        await using (var engine = new PlaylistEngine(options, recorder, new()) { ReadExperiences = () => peers, ChooseAudioOutput = count => choose++ % count })
        {
            var clock = Stopwatch.StartNew();
            await engine.RunAmbientItem(ambient, ambient.Items[0], options, "idle", CancellationToken.None).Completion;
            var sent = recorder.Commands.ToArray();
            Check(sent.Length == 2 && sent.Select(c => c.Command.Device).SequenceEqual(["alpha", "beta"]) && sent.All(c => c.Command.Priority == CommandPriority.Low), "Ambient nesting chooses separately for each file and retains low priority.");
            Check(Stopwatch.GetElapsedTime(sent[0].At, sent[1].At).TotalMilliseconds >= 38 && clock.ElapsedMilliseconds >= 78, "The sequence respects configured file-duration spacing and waits through its last file.");
        }
    }
    private sealed class Recorder : ICommandDispatcher
    {
        internal ConcurrentQueue<(PlaylistCommand Command, long At)> Commands { get; } = new();
        public Task<DispatchReceipt> SendAsync(PlaylistCommand command, ControllerOptions options, BroadcastChain chain, string requestId, CancellationToken token)
        { var at = Stopwatch.GetTimestamp(); Commands.Enqueue((command, at)); return Task.FromResult(new DispatchReceipt(at)); }
    }
}
