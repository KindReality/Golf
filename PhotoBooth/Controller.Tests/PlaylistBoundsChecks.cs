using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Controller.Configuration;
using Controller.Diagnostics;
using Controller.Scheduling;
using PhotoBooth.Networking;

internal static class PlaylistBoundsChecks
{
    private static PlaylistCommand Video() => new() { Device = "*", Payload = JsonSerializer.SerializeToElement(new { experience = "video", value = "long.mp4" }) };
    private static ControllerOptions Options(int minimum, int? maximum) => new()
    {
        DeviceName = "bounds-test", Scenes = [new() { Name = "scare", ActivatingPlaylist = "entry", Transition = new() { Scene = "idle" } }, new() { Name = "idle", AmbientPlaylist = "ambient" }],
        Playlists = [new() { Name = "entry", MinimumDurationMilliseconds = minimum, MaximumDurationMilliseconds = maximum,
            Items = [new() { Name = "video", Commands = [Video()] }] }, new() { Name = "ambient", Type = "ambient" }]
    };
    internal static async Task Run()
    {
        var options = Options(50, 150);
        var estimate = await SceneTransitionTiming.EstimateAsync(options.Scenes[0], options, _ => 100000, default);
        Check(estimate.DurationMilliseconds == 150, "A long video is capped by its activation playlist maximum");
        estimate = await SceneTransitionTiming.EstimateAsync(options.Scenes[0], options, _ => 10, default);
        Check(estimate.DurationMilliseconds == 50, "A short video retains the playlist minimum");
        options = Options(125, 125);
        estimate = await SceneTransitionTiming.EstimateAsync(options.Scenes[0], options, _ => throw new Exception("Fixed durations must not read metadata"), default);
        Check(estimate.DurationMilliseconds == 125, "Matching minimum and maximum give a fixed duration without media reads");

        var legacy = """
            {"Scenes":[{"Name":"scare","ActivatingPlaylist":"entry","Transition":{"Scene":"idle","MinimumSceneDurationSeconds":5}},
                       {"Name":"idle","AmbientPlaylist":"ambient"}],
             "Playlists":[{"Name":"entry","DurationMilliseconds":3000,"Items":[]},{"Name":"ambient","Type":"ambient","DurationMilliseconds":20000,"Items":[]}]}
            """;
        var build = new BuildEnvironment("Development", 1000);
        var normalized = ConfigurationDefaults.Populate(legacy, build);
        var parsed = ControllerOptions.Parse(normalized, build);
        Check(parsed.Playlist("entry").MinimumDurationMilliseconds == 3000 && parsed.Playlist("entry").DurationMilliseconds is null &&
            parsed.Playlist("ambient").DurationMilliseconds == 20000 && !normalized.Contains("MinimumSceneDurationSeconds"),
            "Startup moves prior storyboard duration to its minimum, removes scene timing, and preserves ambient scheduling duration");
        Check(ConfigurationDefaults.Populate(normalized, build) == normalized, "Duration migration is idempotent");
        var priorSceneOnly = legacy.Replace("\"DurationMilliseconds\":3000,", "");
        Check(ControllerOptions.Parse(ConfigurationDefaults.Populate(priorSceneOnly, build), build).Playlist("entry").MinimumDurationMilliseconds == 5000,
            "A prior scene minimum moves into its activation playlist when no playlist minimum exists");
        try { ControllerOptions.Parse(JsonSerializer.Serialize(Options(200, 100), ControllerOptions.Json), build); throw new Exception("Invalid bounds accepted"); }
        catch (ArgumentException) { Check(true, "Maximum below minimum is rejected before saving"); }

        var recorder = new Recorder(); var log = new ActivityLog(); options = Options(40, 120);
        options = options with { Playlists = options.Playlists.Select(p => p.Name == "entry" ? p with { Items = [p.Items[0], new()
            { Name = "late", DelayMilliseconds = 1000, Commands = [Video()] }] } : p).ToArray() };
        await using (var engine = new PlaylistEngine(options, recorder, log) { ReadMediaDurationMilliseconds = _ => 100000 })
        {
            var clock = Stopwatch.StartNew(); var run = engine.Run("entry"); await run.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Check(await run.Outcome == PlaylistOutcome.Completed && clock.ElapsedMilliseconds < 500 && recorder.Commands.Count == 1 &&
                log.Snapshot().Any(e => e.Message.StartsWith("Playlist maximum reached:")),
                "Maximum completes a standalone playlist promptly and suppresses unsent late commands");
        }
        recorder = new(); options = Options(100, 100);
        await using (var engine = new PlaylistEngine(options, recorder, new()) { ReadMediaDurationMilliseconds = _ => throw new Exception("No metadata needed") })
        await using (var scenes = new SceneManager(options, engine, new()))
        {
            recorder.Scene = scenes.Request;
            var clock = Stopwatch.StartNew(); scenes.Start(); await Until(() => scenes.Snapshot() is { Active: "idle", Phase: "Active" });
            Check(clock.ElapsedMilliseconds >= 80 && clock.ElapsedMilliseconds < 450 && recorder.Commands.Count == 2 &&
                ControllerOptions.Text(recorder.Commands.Last().Payload, "command") == "scene" && recorder.Commands.Last().Device == "*" &&
                recorder.Commands.All(c => ControllerOptions.Text(c.Payload, "command") != "stop"),
                "Fixed duration broadcasts the next-scene command and never stops existing media");
        }
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder = new(); options = Options(30, 130);
        await using (var engine = new PlaylistEngine(options, recorder, new()) { ReadMediaDurationMilliseconds = _ => { release.Task.GetAwaiter().GetResult(); return 100000; } })
        await using (var scenes = new SceneManager(options, engine, new()))
        {
            recorder.Scene = scenes.Request;
            try
            {
                var clock = Stopwatch.StartNew(); scenes.Start(); await Until(() => scenes.Snapshot() is { Active: "idle", Phase: "Active" });
                Check(clock.ElapsedMilliseconds < 500, "A stalled metadata read cannot delay the maximum scene handoff");
            }
            finally { release.TrySetResult(); }
        }
    }
    private sealed class Recorder : ICommandDispatcher
    {
        internal readonly ConcurrentQueue<PlaylistCommand> Commands = new();
        internal Func<string, BroadcastChain?, bool>? Scene;
        public Task<DispatchReceipt> SendAsync(PlaylistCommand command, ControllerOptions options, BroadcastChain chain, string requestId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Commands.Enqueue(command);
            if (ControllerOptions.Text(command.Payload, "command") == "scene") Scene?.Invoke(ControllerOptions.Text(command.Payload, "value")!, chain);
            return Task.FromResult(new DispatchReceipt(Stopwatch.GetTimestamp()));
        }
    }
    private static async Task Until(Func<bool> ready)
    { var end = Environment.TickCount64 + 2000; while (!ready()) { if (Environment.TickCount64 > end) throw new TimeoutException("Playlist bounds did not complete"); await Task.Delay(5); } }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
