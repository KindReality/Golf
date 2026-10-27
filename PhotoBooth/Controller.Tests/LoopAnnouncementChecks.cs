using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Controller.Configuration;
using Controller.Diagnostics;
using Controller.Scheduling;
using PhotoBooth.Networking;

internal static class LoopAnnouncementChecks
{
    private static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(40);
    private static PlaylistCommand Loop(string file) => new() { Device = "*", Payload = JsonSerializer.SerializeToElement(new { experience = "video", value = file, screen = "*", loop = true, mute = true, playbackSpeed = .5 }) };
    private static PlaylistOptions Board(string name, int duration = 0) => new() { Name = name, MinimumDurationMilliseconds = duration, Items = [new() { Name = "loop", Commands = [Loop(name + ".mp4")] }] };
    private static ControllerOptions Options() => new() { DeviceName = "loop-controller", Scenes = [new() { Name = "idle", ActivatingPlaylist = "idle-entry", AmbientPlaylist = "empty" },
        new() { Name = "active", ActivatingPlaylist = "active-entry", AmbientPlaylist = "empty" }], Playlists = [new() { Name = "empty", Type = "ambient" }, Board("idle-entry"), Board("active-entry"), Board("manual", 250), Board("cancel", 10000)] };
    internal static async Task Run()
    {
        var recorder = new Recorder(); var options = Options();
        await using (var engine = new PlaylistEngine(options, recorder, new ActivityLog()))
            Check(engine.LoopAnnouncementInterval == TimeSpan.FromSeconds(15), "Production loop announcements use a fifteen-second interval.");
        await using (var engine = new PlaylistEngine(options, recorder, new ActivityLog()) { LoopAnnouncementInterval = FastInterval })
        await using (var scenes = new SceneManager(options, engine, new ActivityLog()))
        {
            scenes.Start(); await Until(() => scenes.Snapshot().Phase == "Active" && recorder.Sent.Count >= 3);
            Check(engine.Snapshot().Length == 0 && engine.LoopAnnouncementCount == 1,
                "Scene loop announcements survive completed activation without keeping its playlist run alive.");
            var initial = recorder.Sent.ToArray();
            Check(initial.All(s => s.Command.Priority == CommandPriority.Low && s.Command.Context?.Scene == "idle" && s.Command.Context.Playlist == "idle-entry" &&
                s.Command.Payload.GetProperty("playbackSpeed").GetDouble() == .5) && initial.Select(s => s.Id).Distinct().Count() == initial.Length,
                "Repeated loops retain context, settings and low priority, with a new request ID each time.");
            Check(initial.Zip(initial.Skip(1)).All(pair => pair.Second.At - pair.First.At >= Stopwatch.Frequency * .025),
                "Repeats wait for their interval instead of sending a catch-up burst.");
            recorder.ReceiverOnline = true;
            await Until(() => recorder.Delivered.Any());
            Check(recorder.Delivered.Last().Command.Context?.Scene == "idle", "A receiver that appears after activation receives a later loop announcement.");
            scenes.Request("active"); await Until(() => recorder.Sent.Count(s => s.Command.Context?.Scene == "active") >= 2);
            var idle = recorder.Sent.Count(s => s.Command.Context?.Scene == "idle"); await Task.Delay(110);
            Check(recorder.Sent.Count(s => s.Command.Context?.Scene == "idle") == idle && engine.LoopAnnouncementCount == 1,
                "Changing scenes retires old announcements immediately and retains only the new scene's loop.");
            scenes.StopImmediately(); var count = recorder.Sent.Count; await Task.Delay(100);
            Check(recorder.Sent.Count == count && engine.LoopAnnouncementCount == 0 && recorder.Sent.All(s => ControllerOptions.Text(s.Command.Payload, "command") != "stop"),
                "Clear cancels all future loop commands without sending playback stops.");
        }
        recorder = new();
        await using (var engine = new PlaylistEngine(options, recorder, new ActivityLog()) { LoopAnnouncementInterval = FastInterval })
        {
            var run = engine.Run("manual"); await Until(() => recorder.Sent.Count >= 3); await run.Completion;
            var count = recorder.Sent.Count; await Task.Delay(100);
            Check(count >= 3 && recorder.Sent.Count == count && engine.LoopAnnouncementCount == 0, "Standalone playlist announcements stop at normal completion.");
            run = engine.Run("cancel"); await Until(() => recorder.Sent.Count > count + 1); engine.Cancel(run.Id); await run.Completion;
            count = recorder.Sent.Count; await Task.Delay(100);
            Check(recorder.Sent.Count == count && engine.LoopAnnouncementCount == 0, "Cancelling a standalone playlist stops its announcements.");
        }
        recorder = new();
        var childOptions = options with { Playlists = [.. options.Playlists, Board("child"), new() { Name = "parent", MinimumDurationMilliseconds = 0,
            Items = [new() { Name = "child", Commands = [new() { Playlist = "child" }] }, new() { Name = "later", DelayMilliseconds = 250 }] }] };
        await using (var engine = new PlaylistEngine(childOptions, recorder, new ActivityLog()) { LoopAnnouncementInterval = FastInterval })
        {
            var parent = engine.Run("parent"); await Until(() => recorder.Sent.Count >= 3); await parent.Completion;
            var count = recorder.Sent.Count; await Task.Delay(100);
            Check(count >= 3 && count == recorder.Sent.Count && engine.LoopAnnouncementCount == 0,
                "Nested playlist loops retain their parent run's lifetime and end when that parent completes.");
        }
        recorder = new() { FailFirst = true };
        await using (var engine = new PlaylistEngine(options, recorder, new ActivityLog()) { LoopAnnouncementInterval = FastInterval })
        {
            engine.SetSceneContext(new("idle"));
            await engine.Run("idle-entry", commandsOnly: true).Completion;
            await Until(() => recorder.Sent.Count >= 3);
            Check(engine.LoopAnnouncementCount == 1, "A failed initial send does not prevent later scene loop announcements.");
            var replacement = options.Playlist("idle-entry") with { Name = "replace", Items = [new() { Name = "first", Commands = [Loop("first.mp4")] }, new() { Name = "last", DelayMilliseconds = 10, Commands = [Loop("last.mp4")] }] };
            engine.Apply(options with { Playlists = [..options.Playlists, replacement] });
            await engine.Run("replace", commandsOnly: true).Completion;
            await Until(() => recorder.Sent.Count(s => ControllerOptions.Text(s.Command.Payload, "value") == "last.mp4") >= 3);
            var old = recorder.Sent.Count(s => ControllerOptions.Text(s.Command.Payload, "value") != "last.mp4"); await Task.Delay(100);
            Check(engine.LoopAnnouncementCount == 1 && recorder.Sent.Count(s => ControllerOptions.Text(s.Command.Payload, "value") != "last.mp4") == old,
                "The newest loop replaces older loop announcements for the identical target.");
        }
        recorder = new(); var choices = 0;
        var random = options.Playlist("idle-entry") with { Items = [new() { Name = "random", Roles = "Front,Rear", BroadcastMode = "Random", Commands = [Loop("random.mp4")] }] };
        var randomOptions = options with { Playlists = options.Playlists.Select(p => p.Name == random.Name ? random : p).ToArray() };
        await using (var engine = new PlaylistEngine(randomOptions, recorder, new ActivityLog()) { LoopAnnouncementInterval = FastInterval, ChooseRole = _ => { choices++; return 1; } })
        {
            engine.SetSceneContext(new("idle")); await engine.Run("idle-entry", commandsOnly: true).Completion;
            await Until(() => recorder.Sent.Count >= 3);
            Check(choices == 1 && recorder.Sent.All(s => ControllerOptions.Text(s.Command.Payload, "roles") == "Rear"),
                "Announcements retain the original random-role selection rather than selecting new destinations.");
        }
        recorder = new() { BlockSecond = true };
        await using (var engine = new PlaylistEngine(options, recorder, new ActivityLog()) { LoopAnnouncementInterval = FastInterval })
        {
            engine.SetSceneContext(new("idle")); await engine.Run("idle-entry", commandsOnly: true).Completion;
            await recorder.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            engine.SetSceneContext(new("active")); await engine.Run("active-entry", commandsOnly: true).Completion;
            await Until(() => recorder.Sent.Count(s => s.Command.Context?.Scene == "active") >= 2);
            Check(engine.LoopAnnouncementCount == 1, "Scene replacement cancels a blocked old resend without delaying new announcements.");
            recorder.Pending.TrySetException(new IOException("Late abandoned send failed."));
        }
    }
    private sealed record Sent(PlaylistCommand Command, string Id, long At);
    private sealed class Recorder : ICommandDispatcher
    {
        internal readonly ConcurrentQueue<Sent> Sent = new(), Delivered = new();
        internal volatile bool ReceiverOnline;
        internal bool FailFirst;
        internal bool BlockSecond;
        internal readonly TaskCompletionSource Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<DispatchReceipt> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DispatchReceipt> SendAsync(PlaylistCommand command, ControllerOptions options, BroadcastChain chain, string requestId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var packet = BroadcastEnvelope.Parse(BroadcastEnvelope.Create(options, command.Type, command.Device!, command.Payload, requestId, chain, command.Context, command.Priority));
            var sent = new Sent(command, packet.RequestId, Stopwatch.GetTimestamp()); Sent.Enqueue(sent); if (ReceiverOnline) Delivered.Enqueue(sent);
            if (FailFirst) { FailFirst = false; throw new IOException("Initial send failed."); }
            if (BlockSecond && Sent.Count == 2) { BlockSecond = false; Blocked.TrySetResult(); return Pending.Task; }
            return Task.FromResult(new DispatchReceipt(sent.At));
        }
    }
    private static async Task Until(Func<bool> ready)
    { var deadline = Environment.TickCount64 + 3000; while (!ready()) { if (Environment.TickCount64 > deadline) throw new TimeoutException("Loop announcement state did not arrive."); await Task.Delay(5); } }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
