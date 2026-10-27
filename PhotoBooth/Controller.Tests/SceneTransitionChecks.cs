using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Controller.Application;
using Controller.Configuration;
using Controller.Diagnostics;
using Controller.Scheduling;
using PhotoBooth.Networking;
using PhotoBooth.Diagnostics;
using System.Net;
using System.Net.Sockets;

internal static class SceneTransitionChecks
{
    private static PlaylistCommand Media(string file, bool loop = false, string kind = "video") => new()
    { Device = "*", Payload = JsonSerializer.SerializeToElement(new { experience = kind, value = file, loop }) };
    private static ControllerOptions Options(double minimum = .08) => new()
    {
        DeviceName = "transition-test", Scenes = [new() { Name = "idle", ActivatingPlaylist = "entry", Transition = new() { Scene = "active" } },
            new() { Name = "active", AmbientPlaylist = "ambient" }],
        Playlists = [new() { Name = "entry", MinimumDurationMilliseconds = (int)(minimum * 1000), Items = [new() { Name = "play", Commands = [Media("video.mp4")] }] }, new() { Name = "ambient", Type = "ambient" }]
    };
    internal static async Task Run()
    {
        var options = Options(5);
        var source = options.Scenes[0];
        var reads = new ConcurrentDictionary<string, int>();
        double? Duration(string file)
        {
            reads.AddOrUpdate(file, 1, (_, n) => n + 1);
            return file switch { "a.mp4" => 2000, "b.mp4" => 5000, "c.mp4" => 4000, "d.wav" => 6000, _ => null };
        }
        options = options with { Playlists = [
            new() { Name = "entry", MinimumDurationMilliseconds = 5000, Items = [
                new() { Name = "parallel", DelayMilliseconds = 100, Commands = [Media("a.mp4"), Media("b.mp4"), Media("ignored-loop.mp4", true)] },
                new() { Name = "nested", DelayMilliseconds = 200, Commands = [new() { Playlist = "child" }] },
                new() { Name = "audio", DelayMilliseconds = 100, Commands = [Media("d.wav", kind: "audio")] },
                new() { Name = "disabled", Enabled = false, DelayMilliseconds = 90000, Commands = [Media("disabled.mp4")] }] },
            new() { Name = "child", MinimumDurationMilliseconds = 0, Items = [new() { Name = "nested cue", DelayMilliseconds = 350, Commands = [Media("c.mp4")] }] },
            new() { Name = "ambient", Type = "ambient" }] };
        var estimate = await SceneTransitionTiming.EstimateAsync(source, options, Duration, default);
        Check(estimate.DurationMilliseconds == 6750 && !estimate.HasUnknownMedia && reads.Count == 4,
            "Automatic duration uses cumulative/nested cue delays and latest media end, excluding loops, disabled items and playlist hold times");
        options = options with { Playlists = options.Playlists.Select(p => p.Name == "entry" ? p with { Items = p.Items.Select(i => i.Name == "audio" ? i with { DelayMilliseconds = 1100 } : i).ToArray() } : p).ToArray() };
        Check((await SceneTransitionTiming.EstimateAsync(source, options, Duration, default)).DurationMilliseconds == 7750,
            "Changing activation items automatically changes the estimated scene duration");
        var empty = Options(5);
        empty = empty with { Playlists = empty.Playlists.Select(p => p.Name == "entry" ? p with { Items = [new() { Name = "loop", Commands = [Media("loop.mp4", true)] }] } : p).ToArray() };
        Check((await SceneTransitionTiming.EstimateAsync(empty.Scenes[0], empty, _ => throw new Exception("Loops must not read metadata"), default)).DurationMilliseconds == 5000,
            "Loop-only activation uses the configurable five-second minimum without reading loop durations");
        Check((await SceneTransitionTiming.EstimateAsync(source with { ActivatingPlaylist = null }, empty, Duration, default)).DurationMilliseconds == 5000,
            "Missing or empty activation uses the scene minimum");
        var missing = await SceneTransitionTiming.EstimateAsync(Options(5).Scenes[0], Options(5), _ => throw new IOException("unavailable"), default);
        Check(missing.DurationMilliseconds == 5000 && missing.HasUnknownMedia, "Unavailable metadata retains the minimum and reports an incomplete estimate");
        var valid = Options();
        void Invalid(SceneOptions scene)
        {
            try { ControllerOptions.Parse(JsonSerializer.Serialize(valid with { Scenes = [scene, valid.Scenes[1]] }, ControllerOptions.Json), valid.Build); throw new Exception("Invalid scene accepted"); }
            catch (ArgumentException) { }
        }
        Invalid(valid.Scenes[0] with { AmbientPlaylist = "ambient" });
        Invalid(valid.Scenes[0] with { Transition = null });
        Invalid(valid.Scenes[0] with { Transition = new() { Scene = "idle" } });
        Check(true, "Validation enforces exactly one scene behavior, a different local next scene");
        var session = new ShowEditSession(valid);
        session.SetScene(1, valid.Scenes[1] with { Name = "renamed" });
        Check(session.Current.Scenes[0].Transition?.Scene == "renamed", "Renaming a scene updates next-scene references");
        try { session.DeleteScene(1); throw new Exception("Referenced scene removed"); } catch (ArgumentException) { }
        Check(true, "A referenced next scene must be reassigned before deletion");

        var log = new ActivityLog(); var recorder = new Recorder(); options = Options(.05);
        options = options with { Playlists = options.Playlists.Select(p => p.Name == "entry" ? p with { MinimumDurationMilliseconds = 50,
            Items = [p.Items[0] with { DelayMilliseconds = 30 }] } : p).ToArray() };
        await using (var engine = new PlaylistEngine(options, recorder, log) { ReadMediaDurationMilliseconds = _ => { Thread.Sleep(70); return 170; } })
        await using (var scenes = new SceneManager(options, engine, log))
        {
            recorder.SceneCommand = (name, chain) => scenes.Request(name, chain);
            var clock = Stopwatch.StartNew(); scenes.Start();
            await Until(() => scenes.Snapshot() is { Active: "active", Phase: "Active" });
            var sent = recorder.Sent.Single(s => ControllerOptions.Text(s.Command.Payload, "command") == "scene");
            Check(clock.ElapsedMilliseconds >= 170 && clock.ElapsedMilliseconds < 450 && sent.Command.Device == "*" && sent.Command.Priority == CommandPriority.Low,
                "Scene timer starts at entry, estimates playback without confirmation, and sends the ordinary broadcast scene command");
            Check(sent.Command.Context?.Scene == "idle" && log.Snapshot().Any(e => e.Message.StartsWith("Scene command: idle")),
                "Scene transition commands retain origin context and readable activity");
        }
        recorder = new(); options = Options(.18);
        await using (var engine = new PlaylistEngine(options, recorder, new()) { ReadMediaDurationMilliseconds = _ => 1 })
        await using (var scenes = new SceneManager(options, engine, new()))
        {
            recorder.SceneCommand = (name, chain) => scenes.Request(name, chain);
            scenes.Start(); await Until(() => recorder.Sent.Count == 1);
            scenes.Request("active"); await Until(() => scenes.Snapshot() is { Active: "active", Phase: "Active" });
            await Task.Delay(230);
            Check(recorder.Sent.All(s => ControllerOptions.Text(s.Command.Payload, "command") != "scene"),
                "A newer scene immediately cancels the old countdown without sending stops or stale transitions");
        }
        recorder = new(); options = Options(.1);
        options = options with { Playlists = options.Playlists.Select(p => p.Name == "entry" ? p with { Items = [p.Items[0] with { Commands = [Media("loop.mp4", true)] },
            new() { Name = "late", DelayMilliseconds = 500, Commands = [Media("must-not-send.mp4", true)] }] } : p).ToArray() };
        await using (var engine = new PlaylistEngine(options, recorder, new()))
        await using (var scenes = new SceneManager(options, engine, new()))
        {
            recorder.SceneCommand = (name, chain) => scenes.Request(name, chain);
            scenes.Start(); await Until(() => scenes.Snapshot() is { Active: "active", Phase: "Active" });
            await Task.Delay(60);
            Check(recorder.Sent.Count == 2 && !recorder.Sent.Any(s => ControllerOptions.Text(s.Command.Payload, "command") == "stop"),
                "The minimum expires for loop-only activation and cancels unsent future cues without stopping playback");
        }
        recorder = new(); options = Options(20);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var engine = new PlaylistEngine(options, recorder, new()) { ReadMediaDurationMilliseconds = _ =>
            { reading.TrySetResult(); releaseRead.Task.GetAwaiter().GetResult(); return 60000; } })
        await using (var scenes = new SceneManager(options, engine, new()))
        {
            try
            {
                scenes.Start(); await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var clock = Stopwatch.StartNew(); scenes.Request("active");
                await Until(() => scenes.Snapshot() is { Active: "active", Phase: "Active" });
                Check(clock.ElapsedMilliseconds < 300, "An explicit scene command also bypasses a blocked duration-metadata read");
            }
            finally { releaseRead.TrySetResult(); }
        }
        recorder = new(); options = Options(.05);
        options = options with { Scenes = [options.Scenes[0] with { Transition = new() { Scene = "remote-scene", Device = "other-controller" } }, options.Scenes[1]] };
        await using (var engine = new PlaylistEngine(options, recorder, new()) { ReadMediaDurationMilliseconds = _ => null })
        await using (var scenes = new SceneManager(options, engine, new()))
        {
            scenes.Start(); await Until(() => recorder.Sent.Any(s => s.Command.Device == "other-controller")); await Task.Delay(80);
            Check(recorder.Sent.Count == 2 && scenes.Snapshot().Active == "idle", "A named destination receives one scene command without changing the untargeted local Controller");
        }
        await NetworkAsync();
    }
    private static async Task NetworkAsync()
    {
        var previous = Environment.GetEnvironmentVariable(LocalTestNetwork.Variable);
        var folder = Path.Combine(Path.GetTempPath(), "Controller-scene-network-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        Environment.SetEnvironmentVariable(LocalTestNetwork.Variable, "127.0.0.2");
        try
        {
            int UdpPort() { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)socket.Client.LocalEndPoint!).Port; }
            int TcpPort() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return ((IPEndPoint)socket.LocalEndpoint).Port; }
            var options = Options(.12) with { ConfigurationMode = true, ExperienceSupervisionEnabled = false, PeerDiscovery = new() { Enabled = false },
                ArduinoDiscovery = new() { Enabled = false }, DefinitionsPort = TcpPort(), UdpCommandsPort = UdpPort(), UdpEventsPort = UdpPort(), UdpSendAttempts = 1,
                Telemetry = JsonSerializer.SerializeToElement(new { Directory = Path.Combine(folder, "logs") }) };
            options = options with { Playlists = [options.Playlists[0] with { Items = [new() { Name = "loop", Commands = [Media("loop.mp4", true)] }] },
                options.Playlists[1], new() { Name = "scare", MinimumDurationMilliseconds = 1000, Items = [new() { Name = "switch", Commands = [new()
                    { Device = "*", Payload = JsonSerializer.SerializeToElement(new { command = "scene", value = "active" }) }] }] }],
                Triggers = [new() { Name = "beam", SourceDevice = "sensor", Input = "beam1", Event = "beam.broken", Playlist = "scare", CooldownMilliseconds = 0 }] };
            var path = Path.Combine(folder, "appsettings.json"); File.WriteAllText(path, JsonSerializer.Serialize(options, ControllerOptions.Json));
            using var packets = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.3"), options.UdpCommandsPort));
            await using var host = new ControllerHost(path);
            BroadcastEnvelope? transition = null;
            using (var timeout = new CancellationTokenSource(4000))
                while (transition is null)
                {
                    var message = BroadcastEnvelope.Parse((await packets.ReceiveAsync(timeout.Token)).Buffer);
                    if (message.Context?.Item == "Next scene") transition = message;
                }
            await Until(() => host.Scenes.Snapshot() is { Active: "active", Phase: "Active" });
            Check(transition.Type == "command" && transition.Device == "*" && ControllerOptions.Text(transition.Body, "command") == "scene" &&
                ControllerOptions.Text(transition.Body, "value") == "active", "Automatic handoff is delivered over real UDP and applies locally using the same envelope");
            var old = ShowConfiguration.From(host.Options);
            await host.SaveShowAsync(old, old with { Playlists = old.Playlists.Select(p => p.Name == "entry" ? p with { MinimumDurationMilliseconds = 20000 } : p).ToArray() });
            await Until(() => host.Options.Playlist("entry").MinimumDurationMilliseconds == 20000);
            await host.SendManualAsync(new() { Device = "*", Payload = JsonSerializer.SerializeToElement(new { command = "scene", value = "idle" }) });
            await Until(() => host.Scenes.Snapshot() is { Active: "idle", Phase: "Waiting" });
            var clock = Stopwatch.StartNew();
            using (var sender = new UdpClient())
                await sender.SendAsync(BroadcastEnvelope.Create(options with { DeviceName = "sensor" }, "event", "*", new { input = "beam1", @event = "beam.broken" }),
                    new IPEndPoint(IPAddress.Parse("127.0.0.2"), options.UdpEventsPort));
            await Until(() => host.Scenes.Snapshot() is { Active: "active", Phase: "Active" });
            Check(clock.ElapsedMilliseconds < 500, "A real UDP sensor trigger changes scene immediately despite a twenty-second automatic minimum");
            await Task.Delay(70);
            Check(!host.Log.Snapshot().Any(e => e.Message.Contains("SceneTransitionFailed")), "Cancelling the automatic countdown does not report a failed transition");
            host.ClearRunningPlaylists();
        }
        finally { Telemetry.Stop(); Environment.SetEnvironmentVariable(LocalTestNetwork.Variable, previous); Directory.Delete(folder, true); }
    }
    private sealed record Sent(PlaylistCommand Command, BroadcastChain Chain);
    private sealed class Recorder : ICommandDispatcher
    {
        internal readonly ConcurrentQueue<Sent> Sent = new();
        internal Action<string, BroadcastChain>? SceneCommand;
        public Task<DispatchReceipt> SendAsync(PlaylistCommand command, ControllerOptions options, BroadcastChain chain, string id, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Sent.Enqueue(new(command, chain));
            if (ControllerOptions.Text(command.Payload, "command") == "scene" && (command.Device == "*" || ControllerOptions.Same(command.Device, options.EffectiveDeviceName)))
                SceneCommand?.Invoke(ControllerOptions.Text(command.Payload, "value")!, chain);
            return Task.FromResult(new DispatchReceipt(Stopwatch.GetTimestamp()));
        }
    }
    private static async Task Until(Func<bool> ready)
    { var end = Environment.TickCount64 + 3000; while (!ready()) { if (Environment.TickCount64 > end) throw new TimeoutException("Scene transition check timed out"); await Task.Delay(5); } }
    private static void Check(bool okay, string text) { if (!okay) throw new Exception(text); Console.WriteLine("PASS " + text); }
}
