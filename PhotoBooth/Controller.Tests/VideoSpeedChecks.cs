using System.Diagnostics;
using System.Text.Json;
using Controller.Application;
using Controller.Configuration;
using Controller.Diagnostics;
using Controller.Networking;
using Controller.Scheduling;
using Controller.Windows;
using PhotoBooth.Networking;

internal static class VideoSpeedChecks
{
    internal static async Task Run()
    {
        PlaylistCommand Video(double speed, bool loop = false) => new() { Device = "*", Payload = JsonSerializer.SerializeToElement(new { experience = "video", command = "play", value = "Sample.mp4", screen = "*", playbackSpeed = speed, loop }) };
        var options = new ControllerOptions { DeviceName = "speed-controller", Build = new("Development", 1000),
            Scenes = [new() { Name = "idle", AmbientPlaylist = "ambient", ActivatingPlaylist = "slow" }],
            Playlists = [new() { Name = "ambient", Type = "ambient" }, new() { Name = "slow", MinimumDurationMilliseconds = 0, Items = [new() { Name = "video", Commands = [Video(.5)] }] }] };
        ControllerOptions Valid(ControllerOptions value) => ControllerOptions.Parse(JsonSerializer.Serialize(value, ControllerOptions.Json), value.Build);
        options = Valid(options);
        foreach (var speed in new[] { .5, 1, 1.25, 2 })
        {
            var command = Video(speed); PinnedActionOptions.ValidateCommand(command, options);
            using var editor = new PlaylistCommandEditor(command, ShowConfiguration.From(options), options, [], [], () => { });
            var field = Descendants(editor).OfType<NumericUpDown>().Single(n => n.AccessibleName == "Playback speed");
            Check(field.Value == (decimal)speed && VideoPlaybackSpeed.Read(editor.Read().Payload) == speed, $"Playlist and pinned video commands preserve {speed}× speed.");
            field.Value = 1.5m;
            Check(VideoPlaybackSpeed.Read(editor.Read().Payload) == 1.5, "Speed is editable through the typed video command form.");
        }
        foreach (var invalid in new[] { Video(0), Video(-1), Video(2.1), new PlaylistCommand { Device = "*", Payload = JsonSerializer.SerializeToElement(new { experience = "audio", value = "Sample.wav", playbackSpeed = .5 }) } })
        {
            Reject(() => PinnedActionOptions.ValidateCommand(invalid, options));
            Reject(() => Valid(options with { Playlists = [options.Playlists[0], options.Playlists[1] with { Items = [new() { Name = "invalid", Commands = [invalid] }] }] }));
        }
        var estimate = await SceneTransitionTiming.EstimateAsync(options.Scenes[0], options, _ => 10000, CancellationToken.None).ConfigureAwait(false);
        Check(estimate.DurationMilliseconds == 20000, "Scene transition estimates divide video duration by playback speed.");
        var mixed = options with { Playlists = [options.Playlists[0], options.Playlists[1] with { Items = [new() { Name = "mixed", Commands = [Video(2), Video(.5), Video(.5, true)] }] }] };
        var reads = 0;
        estimate = await SceneTransitionTiming.EstimateAsync(mixed.Scenes[0], mixed, _ => { reads++; return 10000; }, CancellationToken.None).ConfigureAwait(false);
        Check(estimate.DurationMilliseconds == 20000 && reads == 1, "Shared media metadata is cached while each speed is estimated separately and loops stay excluded.");
        var recorder = new Recorder(); var clock = Stopwatch.StartNew();
        await using (var engine = new PlaylistEngine(options, recorder, new ActivityLog()) { ReadMediaDurationMilliseconds = _ => 200 })
            await engine.Run("slow").Completion.ConfigureAwait(false);
        Check(clock.ElapsedMilliseconds >= 350 && recorder.Command is { } sent && VideoPlaybackSpeed.Read(sent.Payload) == .5,
            "Storyboard playback retains speed in dispatch and waits for its adjusted estimated duration.");
        var inventory = new ExperienceInventory { InstanceId = Guid.NewGuid().ToString("N"), Hostname = Environment.MachineName, DeviceName = "speed-peer",
            Screens = [new() { Id = "video", Name = "screen1", Display = "display", Available = true, Roles = "Front" }], VideoFiles = ["Sample.mp4"],
            Speakers = [new() { Id = "audio", Name = "Speaker", Channels = 2, Roles = "Front" }], AudioFiles = ["Sample.wav"] };
        ExperiencePeer[] peers = [new(inventory, true, false)];
        var live = LivePlayback.Build(options, peers, [new(inventory.Hostname, "video", "display")], "Sample.mp4", 100, true, .5);
        Check(VideoPlaybackSpeed.Read(BroadcastEnvelope.Parse(live.Commands.Single()).Body) == .5, "Live selected-screen playback sends the chosen speed.");
        live = LivePlayback.BuildRoles(options, peers, ["Front"], "Sample.mp4", 100, true, 2);
        Check(VideoPlaybackSpeed.Read(BroadcastEnvelope.Parse(live.Commands.Single()).Body) == 2, "Live role playback sends the chosen speed.");
        live = LivePlayback.BuildRoles(options, peers, ["Front"], "Sample.wav", 100, true, .5);
        Check(!BroadcastEnvelope.Parse(live.Commands.Single()).Body.TryGetProperty("playbackSpeed", out _), "Live audio remains unchanged by the video speed setting.");
    }
    private sealed class Recorder : ICommandDispatcher
    {
        internal PlaylistCommand? Command;
        public Task<DispatchReceipt> SendAsync(PlaylistCommand command, ControllerOptions options, BroadcastChain chain, string requestId, CancellationToken token)
        { Command = command; return Task.FromResult(new DispatchReceipt(Stopwatch.GetTimestamp())); }
    }
    private static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid playback speed accepted."); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
