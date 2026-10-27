using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Controller.Application;
using Controller.Configuration;
using Controller.Diagnostics;
using Controller.Hardware;
using Controller.Scheduling;
using Controller.Windows;
using PhotoBooth.Diagnostics;
using PhotoBooth.Networking;
using PhotoBooth.Configuration;

internal static class Program
{
    private static readonly BuildEnvironment Development = new("Development", 1000);
    private static readonly BuildEnvironment Production = new("Production", 10000);
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if(args.SequenceEqual(["--loop-announcements"])) LoopAnnouncementChecks.Run().GetAwaiter().GetResult();
            else if(args.SequenceEqual(["--role-catalog"])) RoleCatalogChecks.Run().GetAwaiter().GetResult();
            else if(args.SequenceEqual(["--video-speed"])) VideoSpeedChecks.Run().GetAwaiter().GetResult();
            else if(args.SequenceEqual(["--video-fade"])) VideoFadeChecks.Run().GetAwaiter().GetResult();
            else if(args.SequenceEqual(["--scene-cancellation"])) SceneCancellationChecks.Run().GetAwaiter().GetResult();
            else if(args.SequenceEqual(["--scene-next"])) SceneTransitionChecks.Run().GetAwaiter().GetResult();
            else if(args.SequenceEqual(["--playlist-bounds"])) PlaylistBoundsChecks.Run().GetAwaiter().GetResult();
            else if(args.SequenceEqual(["--scene-transitions"])) { SceneTestsAsync().GetAwaiter().GetResult(); VideoLoopTestsAsync().GetAwaiter().GetResult(); }
            else if(args.SequenceEqual(["--controller-peers"])) ControllerPeerChecks.Run().GetAwaiter().GetResult();
            else if(args.SequenceEqual(["--media-sync"])) { PlaylistMediaValidationChecks.Run(); ControllerPeerChecks.Run().GetAwaiter().GetResult(); }
            else if(args.SequenceEqual(["--manual-actions"])) ManualActionChecks.Run().GetAwaiter().GetResult();
            else if(args.SequenceEqual(["--scene-defaults"])) SceneDefaultsChecks.Run();
            else if(args.SequenceEqual(["--scene-playlists"])) ScenePlaylistChecks.Run().GetAwaiter().GetResult();
            else if(args.SequenceEqual(["--dashboard-clear"])) DashboardClearChecks.Run().GetAwaiter().GetResult();
            else if(args.Length == 2 && args[0] == "--serial-firmware") SerialDiscoveryChecks.CheckFirmware(args[1]);
            else if(args.SequenceEqual(["--serial-discovery"])) SerialDiscoveryChecks.Run().GetAwaiter().GetResult();
            else if(args.SequenceEqual(["--random-audio"])) RandomAudioChecks.Run().GetAwaiter().GetResult();
            else if(args.SequenceEqual(["--playlist-lifecycle"])) PlaylistLifecycleChecks.Run().GetAwaiter().GetResult();
            else if(args.SequenceEqual(["--toolbar"])) ToolbarChecks.Run();
            else if(args.SequenceEqual(["--restart-scope"])) RestartScopeChecks.Run().GetAwaiter().GetResult();
            else if(args.SequenceEqual(["--show-editor"])) { ShowEditorChecks.Run().GetAwaiter().GetResult(); ShowEditorPageChecks.Run().GetAwaiter().GetResult(); }
            else if(args.SequenceEqual(["--experience-process"])) ExperienceProcessChecks.Run().GetAwaiter().GetResult();
            else if(args.SequenceEqual(["--perspective-create"])) PerspectiveCreationChecks.Run().GetAwaiter().GetResult();
            else if(args.SequenceEqual(["--roles"])) RolePlaylistChecks.Run().GetAwaiter().GetResult();
            else if(args.SequenceEqual(["--perspective-shortcuts"])) PerspectiveShortcutChecks.Run().GetAwaiter().GetResult();
            else if(args.SequenceEqual(["--live"])) LiveChecks.Run().GetAwaiter().GetResult();
            else if(args.SequenceEqual(["--live-ui"])) LivePageChecks.Run().GetAwaiter().GetResult();
            else if(args.SequenceEqual(["--led-editor"])) LedEditorChecks.Run().GetAwaiter().GetResult();
            else if(args.SequenceEqual(["--wled-discovery"]))
            {
                foreach(var device in WledDiscovery.FindAsync().GetAwaiter().GetResult()) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(device));
            }
            else if (args.SequenceEqual(["--preview-playback"])) PerspectivePreviewPlaybackChecks.Run().GetAwaiter().GetResult();
            else if (args.SequenceEqual(["--perspectives"])) PerspectiveEditorChecks.Run().GetAwaiter().GetResult();
            else if (args.SequenceEqual(["--updates"])) BuildUpdateChecks.Run("Controller").GetAwaiter().GetResult();
            else if (args.SequenceEqual(["--window-startup"])) StartupWindowChecks.Run();
            else if (args.Contains("--window")) Window();
            else if (args.Contains("--host")) HostAsync().GetAwaiter().GetResult();
            else UnitAsync().GetAwaiter().GetResult();
            Console.WriteLine($"PASS: {_checks} Controller checks."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Telemetry.Stop(); }
    }
    private static void Check(bool condition, string description)
    { if (!condition) throw new InvalidOperationException(description); ++_checks; Console.WriteLine("PASS " + description); }
    private static async Task UntilAsync(Func<bool> ready, int timeout = 5000)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready()) { if (elapsed.ElapsedMilliseconds >= timeout) throw new TimeoutException("Expected state did not arrive."); await Task.Delay(5); }
    }
    private static JsonElement Body(object value) => JsonSerializer.SerializeToElement(value);
    private static PlaylistCommand Command(string device = "target") => new() { Device = device, Payload = Body(new { command = "test" }) };
    private static PlaylistItem Item(string name, int delay = 0, string? device = null) => new() { Name = name, DelayMilliseconds = delay, Commands = [Command(device ?? name)] };
    private static PlaylistOptions Board(string name, params PlaylistItem[] items) => new() { Name = name, MinimumDurationMilliseconds = 0, Items = items };
    private static PlaylistOptions Ambient(string name, params PlaylistItem[] items) => new() { Name = name, Type = "ambient", Items = items };
    private static PlaylistItem RandomItem(string name, int min, int max, int cooldown = 0) => Item(name) with { MinIntervalMilliseconds = min, MaxIntervalMilliseconds = max, CooldownMilliseconds = cooldown };
    private static ControllerOptions Options(params PlaylistOptions[] boards) => new()
    {
        DeviceName = "controller-test",
        UdpSendAttempts = 1,
        Build = Development,
        Scenes = [new() { Name = "idle", AmbientPlaylist = "idle-ambient" }],
        Playlists = [Ambient("idle-ambient"), .. boards]
    };
    private static ControllerOptions Valid(ControllerOptions options) => ControllerOptions.Parse(JsonSerializer.Serialize(options, ControllerOptions.Json), options.Build);
    private static BroadcastEnvelope Event(string id, BroadcastChain? chain = null) => BroadcastEnvelope.Parse(BroadcastEnvelope.Create(new() { DeviceName = "sensor" }, "event", "*", new { input = "front", @event = "proximity.enter" }, id, chain));
    private static TriggerOptions Trigger(string playlist = "demo") => new() { Name = "entrance", SourceDevice = "sensor", Input = "front", Event = "proximity.enter", Playlist = playlist, CooldownMilliseconds = 0 };

    private static async Task UnitAsync()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.defaults.json"));
        var defaults = ControllerOptions.Parse(source, Development);
        var displays = new[] { @"\\.\DISPLAY1", @"\\.\DISPLAY2" };
        var displayOptions = defaults with { DefaultDisplayName = " presentation ", DisplayAliases = new() { ["Presentation"] = displays[1] } };
        Check(ControllerDisplaySelection.Select(displayOptions, displays, displays[0]) == displays[1], "Controller selects display aliases with case/whitespace normalization");
        Check(ControllerDisplaySelection.Select(displayOptions with { DefaultDisplayName = displays[1].ToLowerInvariant() }, displays, displays[0]) == displays[1], "Controller accepts raw Windows display notation");
        Check(ControllerDisplaySelection.Select(displayOptions with { DefaultDisplayName = " " }, displays, displays[0]) == displays[0] &&
            ControllerDisplaySelection.Select(displayOptions, [displays[0]], displays[0]) == displays[0], "Empty or disconnected Controller display uses the primary display");
        foreach (var invalidDisplay in new[] {
            defaults with { DefaultDisplayName = "*" }, defaults with { DisplayAliases = null! },
            defaults with { DisplayAliases = new() { ["Left"] = "" } }, defaults with { DisplayAliases = new() { ["Left"] = displays[0], [" left "] = displays[1] } } })
        {
            var rejected = false;
            try { Valid(invalidDisplay); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid Controller display configuration is rejected");
        }
        SceneDefaultsChecks.CheckScenePlaylists(defaults, "Shipped defaults", activationVideos: true);
        var probe = BroadcastEnvelope.Parse(ControllerDiscovery.Request(new() { DeviceName = "experience-alias" }, "probe-id"));
        var reply = BroadcastEnvelope.Parse(ControllerDiscovery.Response(new() { DeviceName = "controller-alias" }, probe));
        Check(ControllerDiscovery.IsRequest(probe) && reply.Device == "*" &&
            ControllerDiscovery.Hostname(reply, new HashSet<string> { "probe-id" }) == Environment.MachineName &&
            ControllerDiscovery.MediaShare(Environment.MachineName) == @"\\" + Environment.MachineName + @"\media", "Controller discovery broadcasts the real SMB hostname");
        Check(ControllerOptions.Parse(source, Development).Build.DefaultTriggerCooldownMilliseconds == 1000 && ControllerOptions.Parse(source, Production).Build.DefaultTriggerCooldownMilliseconds == 10000, "Development and Production transforms");
        Check(ControllerOptions.Parse("{}", Development).UdpSendAttempts == 1, "One best-effort send is the Controller default");
        var populated = ConfigurationDefaults.Populate("{}", Development);
        var seed = ControllerOptions.Parse(populated, Development);
        SceneDefaultsChecks.CheckScenePlaylists(seed, "Missing-scene startup");
        var seededSource = ConfigurationDefaults.Populate(source, Development);
        Check(ConfigurationDefaults.Populate(populated, Development) == populated && ConfigurationDefaults.Populate(seededSource, Development) == seededSource &&
            ControllerOptions.Parse(seededSource, Development).Scenes.SequenceEqual(defaults.Scenes) && Guid.TryParse(seed.ControllerId,out _), "Default population creates a permanent controller ID, is idempotent and preserves configured scenes");
        var missingDisplayFields = System.Text.Json.Nodes.JsonNode.Parse(source)!.AsObject();
        missingDisplayFields.Remove("DefaultDisplayName"); missingDisplayFields.Remove("DisplayAliases");
        var displayFieldsFilled = ConfigurationDefaults.Populate(missingDisplayFields.ToJsonString(), Development);
        var displayFields = System.Text.Json.Nodes.JsonNode.Parse(displayFieldsFilled)!.AsObject();
        Check(displayFields.ContainsKey("DefaultDisplayName") && displayFields.ContainsKey("DisplayAliases") &&
            ControllerOptions.Parse(displayFieldsFilled, Development).Scenes[0] == defaults.Scenes[0] &&
            ConfigurationDefaults.Populate(displayFieldsFilled, Development) == displayFieldsFilled, "Startup adds missing display settings without changing configured scenes");
        var directory = Path.Combine(Path.GetTempPath(), "Controller.Seed-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "appsettings.json"); File.WriteAllText(path, "{}");
            await using (var service = new ConfigurationService(path, Development, new(), _ => Task.CompletedTask))
                Check(service.Initial.Scenes.Length == 2 && ControllerOptions.Parse(File.ReadAllText(path), Development).Scenes.Length == 2, "Startup saves synthesized configuration");

            var application = Path.GetDirectoryName(MachineConfigurationFiles.Resolve("Controller", directory, "ALPHA"))!;
            Directory.CreateDirectory(application);
            var template = Path.Combine(application, "appsettings.defaults.json"); File.WriteAllText(template, source);
            File.WriteAllText(Path.Combine(application, "appsettings.json"), "{\"DeviceName\":\"wrong-shared-profile\"}");
            var local = MachineConfigurationFiles.Resolve("Controller", directory, "ALPHA");
            Check(local == Path.Combine(application, "appsettings.ALPHA.json"), "Hostname configuration is beside OneDrive defaults");
            Check(MachineConfigurationFiles.Resolve("Controller", directory).EndsWith($"appsettings.{Environment.MachineName}.json"), "Controller uses Windows hostname instead of device alias");
            var log = new ActivityLog();
            await using (var service = new ConfigurationService(local, Development, log, _ => Task.CompletedTask, application))
                Check(service.Initial.Scenes.SequenceEqual(defaults.Scenes) && Guid.TryParse(service.Initial.ControllerId,out _) && service.Initial.DeviceName != "wrong-shared-profile", "First launch seeds shipped defaults and machine identity into the OneDrive hostname file");
            var preservedMachineConfiguration = File.ReadAllText(local);
            Check(log.Snapshot().Any(entry => entry.Message.Contains("shipped defaults")), "First-run template source is logged");
            File.WriteAllText(template, "{\"DeviceName\":\"new-default\"}");
            await using (var service = new ConfigurationService(local, Development, new(), _ => Task.CompletedTask, application))
                Check(File.ReadAllText(local) == preservedMachineConfiguration, "Template updates preserve existing hostname Controller settings");
            var other = MachineConfigurationFiles.Resolve("Controller", directory, "LAPTOPMSPIN");
            await using (var service = new ConfigurationService(other, Production, new(), _ => Task.CompletedTask, application))
                Check(service.Initial.DeviceName == "new-default" && service.Initial.Scenes.Length == 2, "Another hostname independently receives the current defaults and startup population");
            Check(File.ReadAllText(template) == "{\"DeviceName\":\"new-default\"}" && File.ReadAllText(local) == preservedMachineConfiguration, "Startup population changes neither the template nor another hostname file");
            File.Delete(template);
            await using (var service = new ConfigurationService(local, Development, new(), _ => Task.CompletedTask, application))
                Check(service.Initial.Playlists.Length == defaults.Playlists.Length, "Existing hostname Controller settings do not require a template");
            var invalidPath = Path.Combine(directory, "invalid", "appsettings.json");
            File.WriteAllText(template, "{incomplete");
            bool rejectedTemplate = false;
            try { await using var service = new ConfigurationService(invalidPath, Development, new(), _ => Task.CompletedTask, application); }
            catch (JsonException) { rejectedTemplate = true; }
            Check(rejectedTemplate && !File.Exists(invalidPath), "Invalid template never creates a partial hostname Controller configuration");
        }
        finally { Directory.Delete(directory, true); }
        var invalid = new List<string> { "{\"Storyboards\":[]}", "{\"DeviceName\":\"a\",\"deviceName\":\"b\"}" };
        void Bad(ControllerOptions value) => invalid.Add(JsonSerializer.Serialize(value, ControllerOptions.Json));
        Bad(Options(Board("a", Item("x") with { DelayMilliseconds = -1 })));
        Bad(Options(Ambient("a", RandomItem("x", 20, 60, 61))));
        Bad(Options(Ambient("a", RandomItem("x", 60, 20))));
        Bad(Options(Board("a", Item("x") with { MinIntervalMilliseconds = 1 })));
        Bad(Options(Board("a", Item("x") with { Commands = [new() { Playlist = "idle-ambient" }] })));
        Bad(Options(Board("a", Item("x") with { Commands = [new() { Playlist = "b" }] }), Board("b", Item("x") with { Commands = [new() { Playlist = "a" }] })));
        Bad(Options(Board("a", Item("x"), Item("X"))));
        Bad(Options() with { Scenes = [new() { Name = "bad", AmbientPlaylist = "missing" }] });
        Bad(Options(Board("a", Item("x") with { Commands = [Command() with { Transport = "arduino", Device = "*" }] })));
        foreach (var text in invalid)
        {
            bool rejected = false; try { ControllerOptions.Parse(text, Development); } catch (Exception ex) when (ex is ArgumentException or JsonException) { rejected = true; }
            Check(rejected, "Invalid reference, timing, cycle or legacy configuration rejected");
        }
        var ancestry = BroadcastChain.From(Event("root")).Enter("controller-test/trigger:entrance");
        var message = BroadcastEnvelope.Parse(BroadcastEnvelope.Create(new() { DeviceName = "controller-test" }, "event", "*", new { input = "front", @event = "proximity.enter" }, "next", ancestry));
        var chain = BroadcastChain.From(message);
        Check(chain.CorrelationId == "root" && chain.ActionHistory[0] == "controller-test/trigger:entrance" && chain.HopCount == 1 && message.Body.GetProperty("causationId").GetString() == "root", "Network ancestry preserves source and causal identity");
        var limit = BroadcastChain.Root("origin"); for (int i = 0; i < BroadcastEnvelope.MaximumHops; i++) limit = limit.Enter("device/action" + i);
        bool limited = false; try { limit.Enter("ninth"); } catch (InvalidOperationException) { limited = true; }
        Check(limited, "Network action chains are bounded");
        var framer = new SerialLineFramer(5); byte[]? line = null;
        foreach (var value in Encoding.UTF8.GetBytes("oversize\n12345\r\n")) { var result = framer.Push(value, out _); if (result is not null) line = result; }
        Check(line is not null && Encoding.UTF8.GetString(line) == "12345", "Serial framing recovers after oversize lines and accepts CRLF");

        var recorder = new Recorder { SlowDevice = "slow", FailDevice = "fail" };
        var options = Valid(Options(Board("demo", Item("first") with { Commands = [Command("slow"), Command("fail"), Command("same-item")] }, Item("last", 40))));
        await using (var engine = new PlaylistEngine(options, recorder, new()))
        {
            await engine.Run("demo").Completion;
            var sends = recorder.Sends.ToArray();
            Check(sends.Select(s => s.Command.Device).SequenceEqual(["slow", "fail", "same-item", "last"]), "Item commands remain ordered; failure continues");
            Check(Stopwatch.GetElapsedTime(sends[2].At, sends[3].At).TotalMilliseconds >= 37, "Item delay begins after previous item finishes sending");
            Check(sends.Select(s => s.RequestId).Distinct().Count() == 4 && sends.Select(s => s.Chain.CorrelationId).Distinct().Count() == 1, "Sends have unique IDs and share one chain");
            Check(sends.All(send => send.Command.Context is { Playlist: "demo", IsAmbient: false } && send.Command.Context.Item is "first" or "last"), "Regular playlists send normal commands with playlist and item context");
        }
        recorder = new();
        options = Valid(Options(Board("child", Item("child1"), Item("child2", 55)), Board("parent", Item("nested") with { Commands = [new() { Playlist = "child" }, Command("after-child")] }, Item("parent2", 25))));
        await using (var engine = new PlaylistEngine(options, recorder, new()))
        {
            await engine.Run("parent").Completion;
            Check(recorder.Sends.Select(s => s.Command.Device).SequenceEqual(["child1", "child2", "after-child", "parent2"]), "Nested storyboards complete before the parent continues");
            Check(recorder.Sends.First().Chain.ActionHistory.Length == 2, "Nested playlist ancestry is retained");
            Check(recorder.Sends.Select(send => send.Command.Context!.Playlist).SequenceEqual(["child", "child", "parent", "parent"]), "Nested commands report the playlist that actually sent them");
            Check(recorder.Sends.All(send => send.Command.Context?.IsAmbient == false), "Nested regular playlists retain normal priority");
        }
        recorder = new();
        var ambientItem = RandomItem("background", 20, 20) with { Commands = [new() { Playlist = "child" }, Command("ambient-direct")] };
        options = Valid(Options(Board("child", Item("nested-command")), Ambient("background", ambientItem)));
        await using (var engine = new PlaylistEngine(options, recorder, new()))
        {
            await engine.RunAmbientItem(options.Playlist("background", "ambient"), ambientItem, options, "idle", CancellationToken.None).Completion;
            Check(recorder.Sends.Count == 2 && recorder.Sends.All(send => send.Command.Context?.IsAmbient == true), "Ambient items and their nested storyboards send only ambient commands");
            var command = recorder.Sends.First().Command;
            var ambientMessage = BroadcastEnvelope.Parse(BroadcastEnvelope.Create(options, command.Type, command.Device!, command.Payload, context: command.Context));
            Check(ambientMessage.Context?.IsAmbient == true, "Ambient priority survives the UDP envelope");
        }
        recorder = new();
        options = Valid(Options(Board("demo", Item("first", device: "old"), Item("later", 130, "old-later"))));
        await using (var engine = new PlaylistEngine(options, recorder, new()))
        {
            var first = engine.Run("demo"); await UntilAsync(() => recorder.Sends.Count == 1);
            engine.Apply(Valid(Options(Board("demo", Item("first", device: "new"))))); await engine.Run("demo").Completion; await first.Completion;
            Check(recorder.Sends.Select(s => s.Command.Device).SequenceEqual(["old", "new", "old-later"]), "Running playlists keep snapshots across reload");
            engine.Apply(options); var cancel = engine.Run("demo"); await UntilAsync(() => recorder.Sends.Count == 4); engine.Cancel(cancel.Id); await cancel.Completion; await Task.Delay(150);
            Check(recorder.Sends.Count == 4, "Cancel removes unsent items");
        }
        recorder = new();
        options = Valid(Options(Board("demo", Item("cool", device: "shared") with { CooldownMilliseconds = 110 }), Board("other", Item("free"))));
        await using (var engine = new PlaylistEngine(options, recorder, new()))
        {
            await engine.Run("demo").Completion; var waiting = engine.Run("demo"); await engine.Run("other").Completion; await waiting.Completion;
            var sends = recorder.Sends.ToArray();
            Check(sends.Select(s => s.Command.Device).SequenceEqual(["shared", "free", "shared"]) && Stopwatch.GetElapsedTime(sends[0].At, sends[2].At).TotalMilliseconds >= 107, "Cooling storyboard items wait; unrelated items run freely");
        }
        await TriggerTestsAsync(); await SceneTestsAsync(); await VideoLoopTestsAsync(); await PlaylistLifecycleChecks.Run();
    }
    private static async Task VideoLoopTestsAsync()
    {
        var loop = new PlaylistCommand { Device = "*", Payload = Body(new { experience = "video", value = "ping.mp4", screen = "*", loop = true, mute = true }) };
        var options = Valid(Options(
            Board("loops", Item("video") with { Commands = [loop, Command("lighting-after-loop")] }, Item("next-item", 25)),
            Board("entry", Item("nested") with { Commands = [new() { Playlist = "loops" }] }),
            Board("late", Item("video") with { Commands = [loop] }, Item("late-loop", 180) with { Commands = [loop] })) with
            { Scenes = [new() { Name = "idle", AmbientPlaylist = "idle-ambient", ActivatingPlaylist = "entry" }, new() { Name = "other", AmbientPlaylist = "idle-ambient" }] });
        var recorder = new Recorder();
        await using (var engine = new PlaylistEngine(options, recorder, new()))
        await using (var scenes = new SceneManager(options, engine, new()))
        {
            scenes.Start(); await UntilAsync(() => scenes.Snapshot().Phase == "Active");
            Check(recorder.Sends.Count == 3 && recorder.Sends.Skip(1).Select(s => s.Command.Device).SequenceEqual(["lighting-after-loop", "next-item"]), "A nested looping video sends once and does not block later commands or playlist items");
            await engine.Run("loops").Completion;
            var original = recorder.Sends.Where(s => VideoCommand.IsLoop(s.Command)).ToArray();
            Check(original.Length == 2 && !recorder.Sends.Any(s => ControllerOptions.Text(s.Command.Payload, "command") == "stop"), "Completed and manual playlists do not send automatic playback stops");
            scenes.Request("other"); await UntilAsync(() => scenes.Snapshot() is { Active: "other", Phase: "Active" });
            Check(!recorder.Sends.Any(s => ControllerOptions.Text(s.Command.Payload, "command") == "stop"), "Scene changes send no playback stops for existing loops");
            var run = engine.Run("late"); await UntilAsync(() => recorder.Sends.Count >= 7);
            scenes.Request("idle"); await UntilAsync(() => scenes.Snapshot() is { Active: "idle", Phase: "Active" }); await run.Completion;
            Check(recorder.Sends.Count(s => VideoCommand.IsLoop(s.Command)) >= 4, "Unrelated manual playlists keep running across scene changes");
        }
        recorder = new() { SlowDevice = "*" };
        options = options with { Scenes = [new() { Name = "idle", AmbientPlaylist = "idle-ambient", ActivatingPlaylist = "late" }, new() { Name = "other", AmbientPlaylist = "idle-ambient" }] };
        await using (var engine = new PlaylistEngine(options, recorder, new()))
        await using (var scenes = new SceneManager(options, engine, new()))
        {
            scenes.Start(); await UntilAsync(() => recorder.Sends.Count == 1); scenes.Request("other");
            await UntilAsync(() => scenes.Snapshot() is { Active: "other", Phase: "Active" });
            await Task.Delay(200);
            Check(recorder.Sends.Count == 1 && VideoCommand.IsLoop(recorder.Sends.Single().Command),
                "An interrupted activation stops sending further loop commands and leaves the first playing");
        }
    }
    private static async Task TriggerTestsAsync()
    {
        var recorder = new Recorder(); var options = Valid(Options(Board("demo", Item("first"), Item("last", 60))) with { Triggers = [Trigger()] });
        await using (var engine = new PlaylistEngine(options, recorder, new()))
        {
            engine.Start(); engine.Receive(Event("root")); engine.Receive(Event("busy"));
            await UntilAsync(() => recorder.Sends.Count == 2 && engine.Snapshot().Length == 0);
            engine.Receive(Event("root")); engine.Receive(Event("return", BroadcastChain.Root("sensor").Enter("controller-test/trigger:entrance"))); await Task.Delay(80);
            Check(recorder.Sends.Count == 2, "Busy triggers, duplicate causal roots and returning loops are suppressed");
        }
        recorder = new(); options = Valid(Options(Board("demo", Item("first"))) with { Triggers = [Trigger() with { CooldownMilliseconds = 90 }] });
        await using (var engine = new PlaylistEngine(options, recorder, new()))
        {
            engine.Receive(Event("one")); await UntilAsync(() => recorder.Sends.Count == 1 && engine.Snapshot().Length == 0); engine.Receive(Event("cool")); await Task.Delay(105); engine.Receive(Event("two")); await UntilAsync(() => recorder.Sends.Count == 2);
            Check(recorder.Sends.Count == 2, "Trigger cooldown suppresses arrivals until expiry");
        }
        recorder = new(); options = Valid(Options(Board("demo", Item("first"), Item("last", 55))) with { Triggers = [new() { Name = "timer", Playlist = "demo", IntervalMilliseconds = 35, CooldownMilliseconds = 60 }] });
        await using (var engine = new PlaylistEngine(options, recorder, new()))
        {
            engine.Start(); await UntilAsync(() => recorder.Sends.Count >= 3);
            var sends = recorder.Sends.ToArray(); Check(Stopwatch.GetElapsedTime(sends[0].At, sends[2].At).TotalMilliseconds >= 113, "Timed triggers skip busy and cooldown ticks");
            engine.Apply(options with { Triggers = [] }); await UntilAsync(() => engine.Snapshot().Length == 0); var count = recorder.Sends.Count; await Task.Delay(100); Check(count == recorder.Sends.Count, "Removing a timed trigger stops its schedule");
        }
        recorder = new(); options = Valid(Options(Board("demo", Item("first"))) with { Triggers = [new() { Name = "timer", Playlist = "demo", IntervalMilliseconds = 160, CooldownMilliseconds = 0 }] });
        await using (var engine = new PlaylistEngine(options, recorder, new()))
        {
            var started = Stopwatch.GetTimestamp(); engine.Start(); await Task.Delay(100); engine.Apply(options); await UntilAsync(() => recorder.Sends.Count == 1);
            Check(Stopwatch.GetElapsedTime(started, recorder.Sends.First().At).TotalMilliseconds < 235, "Unchanged timer keeps its anchor across reload");
        }
    }
    private static ControllerOptions SceneOptions() => Valid(Options(
        Board("enter-a", Item("a-entry")), Board("enter-b", Item("b-entry")), Board("enter-c", Item("c-entry")),
        Board("manual", Item("manual-first"), Item("manual-last", 200)), Ambient("a-ambient", RandomItem("a-tick", 40, 40)), Ambient("b-ambient", RandomItem("b-tick", 35, 35)), Ambient("c-ambient")) with
    {
        Scenes = [new() { Name = "a", AmbientPlaylist = "a-ambient", ActivatingPlaylist = "enter-a" }, new() { Name = "b", AmbientPlaylist = "b-ambient", ActivatingPlaylist = "enter-b" }, new() { Name = "c", AmbientPlaylist = "c-ambient", ActivatingPlaylist = "enter-c" }]
    });
    private static async Task SceneTestsAsync()
    {
        var recorder = new Recorder(); var options = SceneOptions();
        await using (var engine = new PlaylistEngine(options, recorder, new()))
        await using (var scenes = new SceneManager(options, engine, new()))
        {
            scenes.Start(); await UntilAsync(() => recorder.Sends.Any(s => s.Command.Device == "a-tick"));
            var before = recorder.Sends.Count; Check(!scenes.Request("a") && recorder.Sends.Count == before, "Requesting the active scene does not restart it");
            var manual = engine.Run("manual"); scenes.Request("b"); await UntilAsync(() => recorder.Sends.Any(s => s.Command.Device == "b-tick")); await manual.Completion;
            var devices = recorder.Sends.Select(s => s.Command.Device).ToArray();
            Check(Array.IndexOf(devices, "b-entry") < Array.IndexOf(devices, "b-tick"), "Scene transition sends activation, then ambient");
            Check(recorder.Sends.Single(s => s.Command.Device == "b-entry").Command.Context?.Scene == "b" &&
                recorder.Sends.First(s => s.Command.Device == "b-tick").Command.Context?.Scene == "b" &&
                recorder.Sends.Single(s => s.Command.Device == "manual-last").Command.Context?.Scene == "a", "Scene context follows activation, ambient and the manual run's original scene");
            Check(devices.Contains("manual-last"), "Scene changes leave unrelated playlists running");
            var firstEntry = Array.IndexOf(devices, "b-entry"); Check(!devices.Skip(firstEntry + 1).Contains("a-tick"), "Scene exit cancels the previous ambient schedule");
        }
        recorder = new(); options = SceneOptions(); options = Valid(options with { Playlists = options.Playlists.Select(p => p.Name == "enter-b" ? Board("enter-b", Item("b-entry"), Item("b-late", 160)) : p).ToArray() });
        await using (var engine = new PlaylistEngine(options, recorder, new()))
        await using (var scenes = new SceneManager(options, engine, new()))
        {
            scenes.Start(); await UntilAsync(() => scenes.Snapshot().Phase == "Active"); scenes.Request("b"); await UntilAsync(() => recorder.Sends.Any(s => s.Command.Device == "b-entry"));
            Check(!scenes.Request("b"), "Repeat transition destinations are ignored"); scenes.Request("c"); await UntilAsync(() => scenes.Snapshot() is { Active: "c", Phase: "Active" }); await Task.Delay(180);
            var devices = recorder.Sends.Select(s => s.Command.Device).ToArray(); Check(!devices.Contains("b-late") && devices.Contains("c-entry"), "New scene requests cancel unfinished activation commands");
        }
        recorder = new();
        var random = RandomItem("same", 20, 60, 45); options = Valid(Options(Ambient("shared", random), Ambient("empty")) with { Scenes = [new() { Name = "one", AmbientPlaylist = "shared" }, new() { Name = "two", AmbientPlaylist = "empty" }, new() { Name = "three", AmbientPlaylist = "shared" }] });
        var ranges = new ConcurrentQueue<(int Min, int Max)>();
        await using (var engine = new PlaylistEngine(options, recorder, new()) { ChooseInterval = (min, max) => { ranges.Enqueue((min, max)); return min; } })
        await using (var scenes = new SceneManager(options, engine, new()))
        {
            scenes.Start(); await UntilAsync(() => recorder.Sends.Count >= 1 && ranges.Count >= 2); scenes.Request("two"); await UntilAsync(() => scenes.Snapshot() is { Active: "two", Phase: "Active" }); scenes.Request("three"); await UntilAsync(() => recorder.Sends.Count >= 2);
            var sends = recorder.Sends.ToArray(); Check(ranges.ElementAt(1) == (45, 61), "Ambient frequency uses cooldown as the lower random bound, not an added delay");
            Check(Stopwatch.GetElapsedTime(sends[0].At, sends[1].At).TotalMilliseconds >= 42, "Item cooldown survives leaving and re-entering a scene");
        }
        recorder = new() { SlowDevice = "slow" };
        options = Valid(Options(Ambient("parallel", RandomItem("slow", 20, 20) with { Commands = [Command("slow"), Command("snapshot-last")] }, RandomItem("fast", 25, 25))) with { Scenes = [new() { Name = "one", AmbientPlaylist = "parallel" }] });
        await using (var engine = new PlaylistEngine(options, recorder, new()))
        await using (var scenes = new SceneManager(options, engine, new()))
        {
            scenes.Start(); await UntilAsync(() => recorder.Sends.Count >= 3);
            Check(recorder.Sends.ElementAt(1).Command.Device == "fast", "Independent ambient items overlap without waiting for other items");
            var next = Valid(options with { Playlists = [Ambient("idle-ambient"), Ambient("parallel")] }); engine.Apply(next); scenes.Apply(next);
            await Task.Delay(120); var count = recorder.Sends.Count; await Task.Delay(100);
            Check(recorder.Sends.Count == count, "Hot reload stops removed ambient schedules without replaying scene activation");
            Check(recorder.Sends.Any(s => s.Command.Device == "snapshot-last"), "In-flight ambient items retain their snapshot after hot reload");
        }
        recorder = new(); options = SceneOptions();
        await using (var engine = new PlaylistEngine(options, recorder, new()))
        await using (var scenes = new SceneManager(options, engine, new()))
        {
            scenes.Start(); await UntilAsync(() => scenes.Snapshot().Phase == "Active"); var next = options with { Scenes = options.Scenes.Skip(1).ToArray() }; engine.Apply(next); scenes.Apply(next);
            await UntilAsync(() => scenes.Snapshot() is { Active: "b", Phase: "Active" }); Check(true, "Removing the active scene selects the new first scene");
        }
        recorder = new(); options = SceneOptions();
        await using (var engine = new PlaylistEngine(options, recorder, new()))
        await using (var scenes = new SceneManager(options, engine, new()))
        {
            var nearLimit = BroadcastChain.Root("remote");
            for (var index = 0; index < BroadcastEnvelope.MaximumHops - 1; ++index) nearLimit = nearLimit.Enter("remote/action" + index);
            scenes.Request("a", nearLimit); await UntilAsync(() => scenes.Snapshot().Phase == "Failed");
            Check(scenes.Request("a"), "A failed transition can be retried with a fresh request");
            await UntilAsync(() => scenes.Snapshot() is { Active: "a", Phase: "Active" });
        }
    }
    private static string TestConfiguration(string directory, ControllerOptions? settings = null)
    {
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "appsettings.json");
        using var first = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); using var second = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var options = (settings ?? Options(Board("demo", Item("first"), Item("last", 120)))) with { UdpEventsPort = ((IPEndPoint)first.Client.LocalEndPoint!).Port, UdpCommandsPort = ((IPEndPoint)second.Client.LocalEndPoint!).Port };
        File.WriteAllText(path, JsonSerializer.Serialize(Valid(options), ControllerOptions.Json)); return path;
    }
    private static async Task HostAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Controller.Host-" + Guid.NewGuid().ToString("N"));
        var options = Options(Board("demo", Item("serial") with { Commands = [Command("missing-board") with { Transport = "arduino" }] }, Item("udp", 25)), Board("scene-change", Item("change") with { Commands = [new() { Device = "*", Payload = Body(new { command = "scene", value = "second" }) }] })) with
        {
            Arduinos = [new() { Name = "missing-board", Port = "COM65000" }],
            Triggers = [Trigger()],
            Scenes = [new() { Name = "idle", AmbientPlaylist = "idle-ambient" }, new() { Name = "second", AmbientPlaylist = "idle-ambient" }]
        };
        var path = TestConfiguration(directory, options);
        try
        {
            await using var host = new ControllerHost(path);
            var discoveryReplies = new ConcurrentQueue<BroadcastEnvelope>();
            var discoveryClient = new BroadcastOptions { DeviceName = "discovery-client", UdpCommandsPort = host.Options.UdpCommandsPort, UdpEventsPort = host.Options.UdpEventsPort };
            await using var discoveryListener = new UdpBroadcastListener(() => discoveryClient, (message, _) => { if (message.Event == ControllerDiscovery.ResponseEvent) discoveryReplies.Enqueue(message); return Task.CompletedTask; });
            discoveryListener.Bind(0, host.Options.UdpEventsPort);
            using (var discoverySender = new UdpClient(AddressFamily.InterNetwork))
                await discoverySender.SendAsync(ControllerDiscovery.Request(discoveryClient, "host-discovery-check"), new IPEndPoint(IPAddress.Loopback, host.Options.UdpCommandsPort));
            await UntilAsync(() => !discoveryReplies.IsEmpty);
            Check(ControllerDiscovery.Hostname(discoveryReplies.First(), new HashSet<string> { "host-discovery-check" }) == Environment.MachineName,
                "Real UDP discovery command produces a correlated broadcast reply with the machine hostname");
            await UntilAsync(() => host.Scenes.Snapshot().Phase == "Active");
            using var sender = new UdpClient(AddressFamily.InterNetwork);
            var packet = BroadcastEnvelope.Create(new() { DeviceName = "sensor" }, "event", host.Options.EffectiveDeviceName, new { input = "front", @event = "proximity.enter" }, "network-root");
            await sender.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, host.Options.UdpEventsPort)); await sender.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, host.Options.UdpEventsPort));
            await UntilAsync(() => host.Log.Snapshot().Any(e => e.Message == "Playlist finished: demo"));
            Check(host.Log.Snapshot().Count(e => e.Message == "Playlist started: demo") == 1, "Real UDP duplicate events do not restart a playlist");
            Check(host.Log.Snapshot().Any(e => e.Message.StartsWith("CommandFailed")) && host.Log.Snapshot().Any(e => e.Message == "demo/udp → udp"), "Missing serial hardware fails best effort and the following UDP command runs");
            await UntilAsync(() => host.ArduinoStatus.Any(s => !s.Connected && s.Status != "Connecting")); Check(true, "Missing serial hardware appears in monitoring status");
            await host.Engine.Run("scene-change").Completion; await UntilAsync(() => host.Scenes.Snapshot() is { Active: "second", Phase: "Active" });
            Check(host.Log.Snapshot().Count(e => e.Message == "Scene active: second") == 1, "Broadcast scene command applies locally once; UDP echo is suppressed");
            packet = BroadcastEnvelope.Create(new() { DeviceName = "remote" }, "command", host.Options.EffectiveDeviceName, new { command = "scene", value = "idle" }, "remote-scene");
            await sender.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, host.Options.UdpCommandsPort)); await UntilAsync(() => host.Scenes.Snapshot() is { Active: "idle", Phase: "Active" });
            Check(true, "Real remote scene command selects a scene");
            var old = host.Options; File.WriteAllText(path, "{ invalid"); await UntilAsync(() => host.Log.Snapshot().Any(e => e.Message.StartsWith("ConfigurationRejected")));
            Check(ReferenceEquals(old, host.Options), "Invalid hot reload retains the last valid configuration");
            File.WriteAllText(path, JsonSerializer.Serialize(old with { DeviceName = "reloaded-controller" }, ControllerOptions.Json)); await UntilAsync(() => host.Options.EffectiveDeviceName == "reloaded-controller"); Check(true, "Valid hot reload updates device identity");
            File.WriteAllText(path, JsonSerializer.Serialize(host.Options with { UdpInterfaceAddress = "192.0.2.123" }, ControllerOptions.Json)); await UntilAsync(() => host.Options.UdpInterfaceAddress == "192.0.2.123");
            await host.Engine.Run("scene-change").Completion; await UntilAsync(() => host.Scenes.Snapshot() is { Active: "second", Phase: "Active" });
            Check(host.Log.Snapshot().Any(e => e.Message.StartsWith("CommandFailed")), "Scene commands still apply locally when all network sends fail");
            var selfChanging = host.Options with
            {
                Playlists = [.. host.Options.Playlists, Board("switch-on-entry", Item("switch") with { Commands = [new() { Device = "*", Payload = Body(new { command = "scene", value = "second" }) }] }, Item("must-cancel", 150))],
                Scenes = [new() { Name = "redirect", AmbientPlaylist = "idle-ambient", ActivatingPlaylist = "switch-on-entry" }, .. host.Options.Scenes]
            };
            File.WriteAllText(path, JsonSerializer.Serialize(selfChanging, ControllerOptions.Json)); await UntilAsync(() => host.Options.Scenes[0].Name == "redirect");
            host.Scenes.Request("redirect"); await UntilAsync(() => host.Log.Snapshot().Any(e => e.Message == "Started switch-on-entry") && host.Scenes.Snapshot() is { Active: "second", Phase: "Active" }); await Task.Delay(180);
            Check(host.Log.Snapshot().Any(e => e.Message == "switch-on-entry/must-cancel → must-cancel"), "A scene-changing activation completes its remaining items and then delivers the new scene locally offline");
        }
        finally { Telemetry.Stop(); Directory.Delete(directory, true); }
    }
    private static void Window()
    {
        System.Windows.Forms.Application.EnableVisualStyles(); System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        var directory = Path.Combine(Path.GetTempPath(), "Controller.Window-" + Guid.NewGuid().ToString("N"));
        var settings = Options(Board("demo", Item("first"), Item("last", 3000))) with { ExperienceSupervisionEnabled = false, Scenes = [new() { Name = "idle", AmbientPlaylist = "idle-ambient" }, new() { Name = "second", AmbientPlaylist = "idle-ambient" }] };
        var path = TestConfiguration(directory, settings); Exception? failure = null;
        using var window = new ControlWindow(path);
        IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
        window.Shown += async (_, _) =>
        {
            try
            {
                var controls = Descendants(window).ToArray(); var run = controls.OfType<Button>().Single(b => b.Text == "Run playlist"); var cancel = controls.OfType<Button>().Single(b => b.Text == "Cancel selected"); var activate = controls.OfType<Button>().Single(b => b.Text == "Activate scene");
                await UntilAsync(() => run.Enabled && activate.Enabled); Check(window.Visible && controls.OfType<Label>().Any(l => l.Text.Contains("Development")), "Monitoring window shows identity and environment");
                Check(window.WindowState == FormWindowState.Minimized && window.ShowInTaskbar, "Controller starts minimized when configuration mode is off");
                window.RestoreFromTaskbar();
                var power = controls.OfType<PowerToolbarButton>().Single();
                Check(power.MenuButton.Enabled && power.ShutdownItem.Enabled && power.Menu.Items.Cast<ToolStripItem>().Select(i => i.Text).SequenceEqual(["Restart", "Shutdown"]), "Power menu exposes Restart and Shutdown with correct startup availability");
                controls.OfType<ToolbarIconButton>().Single(b => b.Glyph == ToolbarGlyph.Minimize).PerformClick();
                Check(window.Visible && window.ShowInTaskbar && window.WindowState == FormWindowState.Minimized && !window.IsDisposed, "Minimize keeps Controller running with its taskbar entry");
                await Task.Delay(1250);
                Check(window.WindowState == FormWindowState.Minimized, "Display polling leaves Controller minimized");
                window.RestoreFromTaskbar();
                Check(window.Visible && window.ShowInTaskbar && window.Bounds == Screen.PrimaryScreen!.Bounds && window.WindowState == FormWindowState.Normal, "Taskbar restore shows Controller full screen on the primary display");
                Check(window.FormBorderStyle == FormBorderStyle.None && window.Opacity == 1 && window.Bounds == Screen.PrimaryScreen!.Bounds, "Controller starts as a borderless full-screen workspace");
                if (Screen.AllScreens.FirstOrDefault(display => !display.Primary) is { } secondary)
                {
                    ConfigurationDefaults.Write(path, JsonSerializer.Serialize(settings with { DefaultDisplayName = "Presentation", DisplayAliases = new() { ["Presentation"] = secondary.DeviceName } }, ControllerOptions.Json));
                    await UntilAsync(() => window.Bounds == secondary.Bounds);
                    Check(window.WindowState == FormWindowState.Normal, "Live Controller display alias edits reposition the normal window");
                    window.Close();
                    Check(window.Visible && window.ShowInTaskbar && window.WindowState == FormWindowState.Minimized, "Closing Controller minimizes instead of hiding or exiting");
                    window.RestoreFromTaskbar();
                    await Task.Delay(1250);
                    Check(window.Bounds == secondary.Bounds, "Taskbar restore retains the configured display");
                    ConfigurationDefaults.Write(path, JsonSerializer.Serialize(settings, ControllerOptions.Json));
                    await UntilAsync(() => window.Bounds == Screen.PrimaryScreen!.Bounds);
                    Check(true, "Clearing Controller display selection returns the window to the primary display");
                }
                var scenes = controls.OfType<ComboBox>().Single(c => c.Items.Contains("second")); scenes.SelectedItem = "second"; activate.PerformClick(); await UntilAsync(() => controls.OfType<Label>().Any(l => l.Text == "Scene: second • Active")); Check(true, "Native scene selector activates a scene");
                run.PerformClick(); var running = controls.OfType<ListView>().Single(l => l.Columns[0].Text == "Playlist"); await UntilAsync(() => running.Items.Count == 1); running.Items[0].Selected = true; await UntilAsync(() => cancel.Enabled); cancel.PerformClick(); await UntilAsync(() => running.Items.Count == 0); Check(true, "Native Run and Cancel controls work");
                var current = ControllerOptions.Parse(File.ReadAllText(path), Development);
                ConfigurationDefaults.Write(path, JsonSerializer.Serialize(current with { ConfigurationMode = true }, ControllerOptions.Json));
                var scenesButton = controls.OfType<Button>().Single(b => b.Text == "Scenes"); await UntilAsync(() => scenesButton.Enabled);
                var liveButton = controls.OfType<Button>().Single(b => b.Text == "Live"); var perspectivesButton = controls.OfType<Button>().Single(b => b.Text == "Perspectives");
                Check(scenesButton.Parent == liveButton.Parent && scenesButton.Top == liveButton.Top && scenesButton.Top == perspectivesButton.Top, "Scenes, Live and Perspectives share horizontal navigation");
                Check(scenesButton.Size == liveButton.Size && scenesButton.Size == perspectivesButton.Size && !scenesButton.AutoSize && !liveButton.AutoSize && !perspectivesButton.AutoSize, "Live, Scenes and Perspectives have identical fixed button sizes");
                var configuration = controls.OfType<ToolbarIconButton>().Single(b => b.Glyph == ToolbarGlyph.Play);
                Check(configuration.Text.Length == 0 && configuration.AccessibleName == "Exit configuration mode", "Configuration mode presents the themed play icon to exit");
                scenesButton.PerformClick(); var page = Descendants(window).OfType<ShowEditorPage>().Single();
                Check(page.Visible && page.Parent?.TopLevelControl == window && window.OwnedForms.Length == 0, "Scenes editor is embedded in the full-screen Controller window");
                var sceneName = Descendants(page).OfType<TextBox>().Single(c => c.AccessibleName == "Scene name"); sceneName.Text = "Unsaved scene name";
                window.OpenLive(); window.OpenScenes();
                Check(Descendants(page).OfType<TextBox>().Single(c => c.AccessibleName == "Scene name").Text == "Unsaved scene name" && page.HasUnsavedChanges && ControllerOptions.Parse(File.ReadAllText(path), Development).Scenes[0].Name == "idle", "Switching pages retains a draft without saving or activating it");
                var back = controls.OfType<ToolbarIconButton>().Single(b => b.Glyph == ToolbarGlyph.Return);
                Check(back.Visible && back.AccessibleName == "Return to dashboard", "Editor navigation presents the turning return arrow");
                back.PerformClick(); Check(!page.Visible && !back.Visible, "Turning return arrow opens Dashboard");
                if (Environment.GetEnvironmentVariable("CONTROLLER_WINDOW_SNAPSHOT") is { Length: > 0 } snapshot) { using var bitmap = new Bitmap(window.Width, window.Height); window.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(snapshot); }
            }
            catch (Exception ex) { failure = ex; }
            finally { window.ExitController(); }
        };
        System.Windows.Forms.Application.Run(window); Telemetry.Stop(); Directory.Delete(directory, true); if (failure is not null) throw failure;
    }
    private sealed record Send(PlaylistCommand Command, BroadcastChain Chain, string RequestId, long At, string Source);
    private sealed class Recorder : ICommandDispatcher
    {
        public ConcurrentQueue<Send> Sends { get; } = new();
        public string? FailDevice { get; init; }
        public string? SlowDevice { get; init; }
        public async Task<DispatchReceipt> SendAsync(PlaylistCommand command, ControllerOptions options, BroadcastChain chain, string requestId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var at = Stopwatch.GetTimestamp(); Sends.Enqueue(new(command, chain, requestId, at, options.EffectiveDeviceName));
            if (command.Device == FailDevice) throw new IOException("Disconnected test Arduino");
            if (command.Device == SlowDevice) await Task.Delay(80, token);
            return new(at);
        }
    }
}
