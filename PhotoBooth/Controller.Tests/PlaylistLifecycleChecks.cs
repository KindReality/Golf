using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Controller.Configuration;
using Controller.Diagnostics;
using Controller.Scheduling;
using PhotoBooth.Networking;

internal static class PlaylistLifecycleChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static async Task Until(Func<bool> ready)
    { var clock = Stopwatch.StartNew(); while (!ready()) { if (clock.ElapsedMilliseconds > 5000) throw new TimeoutException("Playlist lifecycle state did not arrive."); await Task.Delay(5); } }
    private static PlaylistCommand Cmd(object body) => new() { Device = "*", Payload = JsonSerializer.SerializeToElement(body) };
    private static PlaylistCommand Video(bool loop = false) => Cmd(new { experience = "video", value = "test.mp4", screen = "*", loop });
    private static PlaylistItem Item(string name, params PlaylistCommand[] commands) => new() { Name = name, Commands = commands };
    private static PlaylistOptions Board(string name, int? duration, params PlaylistItem[] items) => new() { Name = name, DurationMilliseconds = duration, Items = items };
    private static ControllerOptions Options(params PlaylistOptions[] lists) => new()
    {
        DeviceName = "lifecycle-test", Build = new("Development", 1000),
        Scenes = [new() { Name = "idle", AmbientPlaylist = "empty" }, new() { Name = "active", AmbientPlaylist = "empty" }],
        Playlists = [new() { Name = "empty", Type = "ambient" }, ..lists]
    };
    public static async Task Run()
    {
        var recording = new Recorder();
        var options = Options(Board("short", 20, Item("video", Video())), Board("long", 240, Item("audio", Cmd(new { experience = "audio", value = "test.wav" }))), Board("loop", 120, Item("video", Video(true))), Board("bad-loop", null, Item("video", Video(true))));
        var log = new ActivityLog();
        await using (var engine = new PlaylistEngine(options, recording, log) { ReadMediaDurationMilliseconds = _ => 130 })
        {
            var clock = Stopwatch.StartNew(); await engine.Run("short").Completion;
            Check(clock.ElapsedMilliseconds >= 125, "A short configured duration never cuts off a non-looping video.");
            clock.Restart(); await engine.Run("long").Completion;
            Check(clock.ElapsedMilliseconds >= 235, "Configured duration can hold a playlist open after media finishes.");
            clock.Restart(); await engine.Run("loop").Completion;
            Check(clock.ElapsedMilliseconds >= 115 && clock.ElapsedMilliseconds < 1000, "Loop playback does not extend automatic media duration.");
            await engine.Run("bad-loop").Completion;
            Check(log.Snapshot().Any(e => e.Message.Contains("only looping media needs an explicit duration")), "Loop-only triggered playlists require an explicit duration.");
            Check(recording.Commands.Take(2).All(c => c.Priority == CommandPriority.Medium) && recording.Commands.Skip(2).All(c => c.Priority == CommandPriority.Low), "Triggered one-shots are medium priority; loops are always low.");
        }
        options = Options(Board("zone1", 170, Item("activate", Cmd(new { command = "scene", value = "active" })), Item("one", Video())), Board("zone2", 370, Item("activate", Cmd(new { command = "scene", value = "active" })), Item("two", Cmd(new { experience = "audio", value = "test.wav" }))));
        recording = new();
        await using (var engine = new PlaylistEngine(options, recording, new()) { ReadMediaDurationMilliseconds = _ => 60 })
        await using (var scenes = new SceneManager(options, engine, new()))
        {
            recording.Scene = scenes.Request;
            scenes.Start(); await Until(() => scenes.Snapshot() is { Active: "idle", Phase: "Active" });
            var first = engine.Run("zone1"); var second = engine.Run("zone2");
            await Until(() => scenes.Snapshot() is { Active: "active", Phase: "Active", TriggeredPlaylists: 2 });
            await first.Completion;
            Check(scenes.Snapshot() is { Active: "active", TriggeredPlaylists: 1 }, "Overlapping zones keep Active until all triggered playlists finish.");
            await second.Completion; await Until(() => scenes.Snapshot() is { Active: "idle", Phase: "Active", TriggeredPlaylists: 0 });
            Check(true, "The final triggered playlist returns the scene to Idle.");
        }
        recording = new();
        var entry = Board("entry", null, Item("first", Cmd(new { command = "test", value = "first" })), Item("last", Cmd(new { command = "test", value = "last" })) with { DelayMilliseconds = 150 });
        var ambient = new PlaylistOptions { Name = "active-ambient", Type = "ambient", Items = [Item("tick", Cmd(new { command = "test", value = "tick" })) with { MinIntervalMilliseconds = 5, MaxIntervalMilliseconds = 5 }] };
        options = Options(entry, ambient) with { Scenes = [new() { Name = "idle", AmbientPlaylist = "empty" }, new() { Name = "active", AmbientPlaylist = "active-ambient", ActivatingPlaylist = "entry" }] };
        await using (var engine = new PlaylistEngine(options, recording, new()))
        await using (var scenes = new SceneManager(options, engine, new()))
        {
            scenes.Start(); await Until(() => scenes.Snapshot().Phase == "Active"); scenes.Request("active");
            await Until(() => recording.Commands.Any()); scenes.Request("idle");
            await Until(() => scenes.Snapshot() is { Active: "idle", Phase: "Active" });
            Check(recording.Commands.Select(c => ControllerOptions.Text(c.Payload, "value")).SequenceEqual(["first"]), "Activation cancellation suppresses later commands and obsolete ambient scheduling.");
            Check(recording.Commands.All(c => c.Priority == CommandPriority.Low), "Scene activation uses low priority.");
            scenes.Request("active"); await Until(() => scenes.Snapshot().Phase == "Activating"); scenes.Request("idle");
            Check(scenes.Request("active"), "The latest Active request replaces Idle even during activation.");
            await Until(() => scenes.Snapshot() is { Active: "active", Phase: "Active" });
        }
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.defaults.json"));
        var defaults = ControllerOptions.Parse(source, new("Development", 1000));
        Check(defaults.Scenes.Select(s => s.Name).SequenceEqual(["idle", "active"]) && Enumerable.Range(1, 3).All(i => defaults.Playlist("active-zone-" + i).Items[0].Commands[0].Payload.GetProperty("value").GetString() == "active"), "Defaults provide Idle, Active and three independently editable zone storyboards.");
        foreach (var duration in new[] { -1, 86400001 })
        {
            bool rejected = false;
            try { ControllerOptions.Parse(JsonSerializer.Serialize(Options(Board("bad", duration)), ControllerOptions.Json), new("Development", 1000)); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid playlist durations are rejected.");
        }
        // Exercise the actual Windows duration reader without touching the user's media library.
        var media = Path.Combine(Path.GetTempPath(), "Controller-duration-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            using (var stream = File.Create(media)) using (var writer = new BinaryWriter(stream))
            { writer.Write("RIFF"u8); writer.Write(36 + 16000); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(16000); writer.Write(new byte[16000]); }
            var duration = await MediaDurations.ReadAsync(media, MediaDurations.ReadPathMilliseconds, CancellationToken.None);
            Check(duration is > 990 and < 1010, "Windows media metadata reports the actual duration of a one-second local WAV.");
        }
        finally { File.Delete(media); }
    }
    private sealed class Recorder : ICommandDispatcher
    {
        public ConcurrentQueue<PlaylistCommand> Commands { get; } = new();
        public Func<string, BroadcastChain?, bool>? Scene;
        public Task<DispatchReceipt> SendAsync(PlaylistCommand command, ControllerOptions options, BroadcastChain chain, string requestId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Commands.Enqueue(command);
            if (ControllerOptions.Text(command.Payload, "command") == "scene") Scene?.Invoke(ControllerOptions.Text(command.Payload, "value")!, chain);
            return Task.FromResult(new DispatchReceipt(Stopwatch.GetTimestamp()));
        }
    }
}
