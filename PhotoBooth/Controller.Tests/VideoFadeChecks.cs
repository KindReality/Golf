using System.Diagnostics;
using System.Text.Json;
using Controller.Application;
using Controller.Configuration;
using Controller.Diagnostics;
using Controller.Scheduling;
using Controller.Windows;
using PhotoBooth.Networking;

internal static class VideoFadeChecks
{
    internal static async Task Run()
    {
        static PlaylistCommand Fade(string action, double duration) => new() { Device = "*", Payload = JsonSerializer.SerializeToElement(new { experience = "video", command = action, durationSeconds = duration, screen = "Projector/screen1" }) };
        var commands = new[] { Fade("fadeOut", .375), Fade("fadeIn", 0) };
        var options = new ControllerOptions
        {
            DeviceName = "fade-controller", Build = new("Development", 1000),
            Scenes = [new() { Name = "idle", AmbientPlaylist = "ambient", ActivatingPlaylist = "fades" }],
            Playlists = [new() { Name = "ambient", Type = "ambient" }, new() { Name = "fades", MinimumDurationMilliseconds = 0,
                Items = [new() { Name = "visibility", Roles = "^Hidden,*", Commands = commands }] }]
        };
        ControllerOptions Valid(ControllerOptions value) => ControllerOptions.Parse(JsonSerializer.Serialize(value, ControllerOptions.Json), value.Build);
        options = Valid(options);
        foreach (var command in commands)
        {
            PinnedActionOptions.ValidateCommand(command, options);
            using var editor = new PlaylistCommandEditor(command, ShowConfiguration.From(options), options, [], [], () => { });
            var body = editor.Read().Payload;
            Check(body.GetProperty("durationSeconds").GetDouble() == command.Payload.GetProperty("durationSeconds").GetDouble() && !body.TryGetProperty("value", out _),
                "Fade editor preserves fractional/zero durations without requiring a media filename.");
            var kind = Descendants(editor).OfType<ComboBox>().Single(c => c.AccessibleName == "Command type");
            kind.SelectedItem = "Video"; kind.SelectedItem = "Fade out";
            var duration = Descendants(editor).OfType<NumericUpDown>().Single(n => n.AccessibleName == "Fade duration (seconds)");
            duration.Value = .625m;
            Check(ControllerOptions.Text(editor.Read().Payload, "command") == "fadeOut" && editor.Read().Payload.GetProperty("durationSeconds").GetDouble() == .625,
                "The command picker can create fade commands with an editable duration.");
        }
        foreach (var body in new object[] { new { experience = "video", command = "fadeOut" }, new { experience = "audio", command = "fadeIn", durationSeconds = 1 },
            new { experience = "video", command = "fadeOut", durationSeconds = -1 }, new { experience = "video", command = "fadeIn", durationSeconds = 1, loop = true },
            new { experience = "video", command = "fadeOut", durationSeconds = 1, loop = "false" } })
        {
            var invalid = new PlaylistCommand { Device = "*", Payload = JsonSerializer.SerializeToElement(body) };
            Reject(() => PinnedActionOptions.ValidateCommand(invalid, options));
            Reject(() => Valid(options with { Playlists = [options.Playlists[0], options.Playlists[1] with { Items = [new() { Name = "invalid", Commands = [invalid] }] }] }));
        }
        var routed = PlaylistRoleTarget.Apply(commands[0], "^Hidden,*");
        Check(!routed.Payload.TryGetProperty("screen", out _) && ControllerOptions.Text(routed.Payload, "roles") == "^Hidden,*",
            "Fade role targeting uses the same ordered exclusions and replaces direct screen selectors.");
        var recorder = new Recorder();
        await using (var engine = new PlaylistEngine(options, recorder, new ActivityLog()) { ReadMediaDurationMilliseconds = _ => throw new Exception("Fades are not media requests.") })
            await engine.Run("fades").Completion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Check(recorder.Sent.Count == 2 && recorder.Sent.All(c => c.Context?.Playlist == "fades" && c.Priority == CommandPriority.Medium && ControllerOptions.Text(c.Payload, "roles") == "^Hidden,*"),
            "Storyboards dispatch fades with normal context, roles and priority without waiting for media completion.");
        var estimate = await SceneTransitionTiming.EstimateAsync(options.Scenes[0], options, _ => throw new Exception("Fade must not open media."), CancellationToken.None);
        Check(estimate.DurationMilliseconds == 0 && !estimate.HasUnknownMedia, "Scene media-duration estimation excludes fades.");
        Check(ShowEditSession.CommandSummary(commands[0]).Contains("fadeOut"), "Playlist summaries identify the fade action.");
    }
    private sealed class Recorder : ICommandDispatcher
    {
        internal List<PlaylistCommand> Sent = [];
        public Task<DispatchReceipt> SendAsync(PlaylistCommand command, ControllerOptions options, BroadcastChain chain, string requestId, CancellationToken token)
        {
            var packet = BroadcastEnvelope.Parse(BroadcastEnvelope.Create(options, command.Type, command.Device!, command.Payload, requestId, chain, command.Context, priority: command.Priority));
            Check(packet.Body.GetProperty("durationSeconds").GetDouble() >= 0, "Fade duration survives the network envelope.");
            Sent.Add(command); return Task.FromResult(new DispatchReceipt(Stopwatch.GetTimestamp()));
        }
    }
    private static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid fade was accepted."); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
