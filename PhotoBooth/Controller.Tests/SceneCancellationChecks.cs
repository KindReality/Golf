using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Controller.Configuration;
using Controller.Diagnostics;
using Controller.Scheduling;
using PhotoBooth.Networking;

internal static class SceneCancellationChecks
{
    private static PlaylistCommand Video(string value) => new() { Device = "*", Payload = JsonSerializer.SerializeToElement(new { experience = "video", value, screen = "*", loop = false }) };
    private static ControllerOptions Options() => new()
    {
        DeviceName = "scene-test",
        Scenes = [new() { Name = "idle", ActivatingPlaylist = "idle-entry", AmbientPlaylist = "empty" }, new() { Name = "active", ActivatingPlaylist = "active-entry", AmbientPlaylist = "empty" }],
        Playlists = [new() { Name = "empty", Type = "ambient" }, Entry("idle"), Entry("active")]
    };
    private static PlaylistOptions Entry(string name) => new() { Name = name + "-entry", Items = [new() { Name = "video", Commands = [Video(name + ".mp4")] }] };
    private static ControllerOptions Delayed() => Options() with
    {
        Playlists = Options().Playlists.Select(p => p.Type == "ambient" ? p : p with { MinimumDurationMilliseconds = 600000,
            Items = [p.Items[0], new() { Name = "late", DelayMilliseconds = 60000, Commands = [Video("must-not-send.mp4")] }] }).ToArray()
    };
    internal static async Task Run()
    {
        var log = new ActivityLog(); var recorder = new Recorder(); var options = Delayed(); var metadataReads = 0;
        await using (var engine = new PlaylistEngine(options, recorder, log) { ReadMediaDurationMilliseconds = _ => { Interlocked.Increment(ref metadataReads); return 7405000; } })
        await using (var scenes = new SceneManager(options, engine, log))
        {
            scenes.Start(); await Until(() => recorder.Sent.Count == 1 && scenes.Snapshot().Phase == "Activating");
            Check(!scenes.Request("idle"), "Requesting the selected scene during activation is a no-op");
            var clock = Stopwatch.StartNew(); scenes.Request("active");
            await Until(() => recorder.Sent.Any(s => s.Value == "active.mp4"));
            Check(clock.ElapsedMilliseconds < 500 && scenes.Snapshot().Active == "active", "Active starts promptly while Idle's long activation is unfinished");
            clock.Restart(); scenes.Request("idle"); await Until(() => recorder.Sent.Count(s => s.Value == "idle.mp4") == 2);
            Check(clock.ElapsedMilliseconds < 500, "Idle starts promptly while Active's activation is unfinished");
            scenes.Request("active"); scenes.Request("idle");
            await Until(() => recorder.Sent.Count(s => s.Value == "idle.mp4") >= 3);
            await Task.Delay(80);
            Check(scenes.Snapshot().Active == "idle" && recorder.Sent.Last().Value == "idle.mp4", "Rapid Idle–Active–Idle requests leave the final scene authoritative");
            Check(!recorder.Sent.Any(s => s.Value == "must-not-send.mp4") && recorder.Sent.All(s => s.Command != "stop"), "Cancellation suppresses old delayed cues and never sends playback stops");
            Check(metadataReads == 0 && recorder.Sent.All(s => s.Priority == CommandPriority.Low), "Scene phases do not read playback-end estimates and retain Low priority");
            Check(log.Snapshot().Any(e => e.Message.StartsWith("Activation cancelled:")), "Interrupted activation is recorded in activity/logs");
        }
        // No Experience completion, healthy renderer or local media metadata is required to select a scene.
        recorder = new(); options = Options();
        await using (var engine = new PlaylistEngine(options, recorder, log) { ReadMediaDurationMilliseconds = _ => throw new IOException("No local media") })
        await using (var scenes = new SceneManager(options, engine, log))
        {
            scenes.Start(); await Until(() => scenes.Snapshot().Phase == "Active");
            scenes.Request("active"); await Until(() => scenes.Snapshot() is { Active: "active", Phase: "Active" });
            Check(recorder.Sent.Count == 2, "Missing media metadata, absent Experience and missing completion events cannot hold scene activation open");
        }
        recorder = new() { BlockIdle = true }; options = Delayed(); log = new();
        await using (var engine = new PlaylistEngine(options, recorder, log))
        await using (var scenes = new SceneManager(options, engine, log))
        {
            scenes.Start(); await Until(() => recorder.Sent.Count == 1);
            scenes.Request("active"); await Until(() => recorder.Sent.Any(s => s.Value == "active.mp4"));
            recorder.Blocked.TrySetResult(new(Stopwatch.GetTimestamp())); await Task.Delay(60);
            Check(scenes.Snapshot().Active == "active" && !log.Snapshot().Any(e => e.Message == "Scene active: idle"), "A late uncancellable send completion cannot mark the old scene active");
            scenes.Request("idle"); await Until(() => recorder.Sent.Count(s => s.Value == "idle.mp4") == 2);
            Check(true, "Cancelling an uncooperative dispatch releases shared item scheduling for later visits");
        }
        recorder = new() { FailIdle = true }; options = Options(); log = new();
        await using (var engine = new PlaylistEngine(options, recorder, log))
        await using (var scenes = new SceneManager(options, engine, log))
        {
            scenes.Start(); await Until(() => scenes.Snapshot().Phase == "Failed");
            Check(!log.Snapshot().Any(e => e.Message == "Scene active: idle"), "A failed activation is not reported as successfully activated");
            scenes.Request("active"); await Until(() => scenes.Snapshot() is { Active: "active", Phase: "Active" });
            Check(true, "A disconnected/restarted receiver does not prevent the next scene request");
        }
        recorder = new(); options = Delayed();
        await using (var engine = new PlaylistEngine(options, recorder, log))
        await using (var scenes = new SceneManager(options, engine, log))
        {
            scenes.Start();
            for (var i = 0; i < 100; i++) { scenes.Request(i % 2 == 0 ? "active" : "idle"); await Task.Delay(1); }
            await Until(() => engine.Snapshot().Length <= 1 && scenes.OutstandingWork <= 2);
            Check(scenes.Snapshot().Active == "idle" && recorder.Sent.All(s => s.Command != "stop"), "One hundred replacements retire obsolete work without accumulating playback ownership or sending stops");
            scenes.StopImmediately(); await Until(() => engine.Snapshot().Length == 0 && scenes.OutstandingWork <= 1);
            Check(true, "Clearing running playlists cancels pending commands promptly");
        }
    }
    private sealed record Sent(string? Value, string? Command, CommandPriority Priority);
    private sealed class Recorder : ICommandDispatcher
    {
        public readonly ConcurrentQueue<Sent> Sent = new();
        public readonly TaskCompletionSource<DispatchReceipt> Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockIdle, FailIdle;
        public Task<DispatchReceipt> SendAsync(PlaylistCommand command, ControllerOptions options, BroadcastChain chain, string requestId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var value = ControllerOptions.Text(command.Payload, "value");
            Sent.Enqueue(new(value, ControllerOptions.Text(command.Payload, "command"), command.Priority));
            if (value == "idle.mp4" && FailIdle) throw new IOException("Experience unavailable");
            if (value == "idle.mp4" && BlockIdle) { BlockIdle = false; return Blocked.Task; }
            return Task.FromResult(new DispatchReceipt(Stopwatch.GetTimestamp()));
        }
    }
    private static async Task Until(Func<bool> ready)
    { var end = Environment.TickCount64 + 2000; while (!ready()) { if (Environment.TickCount64 > end) throw new TimeoutException("Scene cancellation state did not arrive."); await Task.Delay(2); } }
    private static void Check(bool condition, string text) { if (!condition) throw new Exception(text); Console.WriteLine("PASS " + text); }
}
