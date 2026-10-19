using System.Collections.Concurrent;
using System.Diagnostics;
using ExperienceX;
using PhotoBooth.Networking;

internal static class PlaybackPriorityEventChecks
{
    internal static async Task Run()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
        var packets = new ConcurrentQueue<byte[]>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new ExperienceOptions { DeviceName = "experience-test", DisplayAliases = new() { ["Projector"] = @"\\.\DISPLAY1" } };
        await using var events = new PlaybackPriorityEvents(() => settings, async (bytes, _, token) =>
        { await release.Task.WaitAsync(token); packets.Enqueue(bytes); });
        var request = new ExperienceRequest { Experience = "video", Value = "Eyes/Eye.mp4", RequestId = "original-command", Source = "controller-test",
            Context = new() { Scene = "idle", Playlist = "media-loop", Item = "Eye1" },
            Chain = new("correlation", "controller-test", "original-command", ["controller-test/playlist:media-loop"], 1), Loop = true };
        var clock = Stopwatch.StartNew();
        events.Report(request, @"\\.\DISPLAY1", "screen1", CommandPriority.Medium);
        Check(clock.ElapsedMilliseconds < 100 && packets.IsEmpty, "Reporting never waits for network transmission");
        events.Report(request, @"\\.\DISPLAY1", "screen1", CommandPriority.Medium);
        events.Report(request, @"\\.\DISPLAY1", "screen2", CommandPriority.High);
        events.Report(request, @"\\.\DISPLAY1", "screen3", CommandPriority.Low);
        release.TrySetResult();
        var end = Environment.TickCount64 + 2000;
        while (packets.Count < 2) { if (Environment.TickCount64 > end) throw new TimeoutException("Priority events not sent."); await Task.Delay(5); }
        var messages = packets.Select(p => BroadcastEnvelope.Parse(p)).ToArray();
        Check(messages.Length == 2 && messages.Select(m => m.RequestId).Distinct().Count() == 2, "One fresh event per blocked screen; duplicate reports and non-rejections are suppressed");
        var first = messages[0];
        Check(first.Event == PlaybackPriorityEvent.EventName && first.Device == "*" && first.Source == "experience-test" &&
            first.Body.GetProperty("commandRequestId").GetString() == request.RequestId && first.Context?.Playlist == "media-loop" && first.HopCount == 2,
            "Broadcast event retains the original command reference, playlist and causal ancestry");
        Check(PlaybackPriorityEvent.TryDescribe(first, out var text) && text == "Deferred Eye.mp4 on experience-test/Projector/screen1 (low < medium)",
            "Controller gets a concise friendly-display message for remembered background loops");
        var immediate = BroadcastEnvelope.Parse(PlaybackPriorityEvent.Create(settings, "controller", "one-shot", "Ghosts/Ghost2.mp4", "display", "Left", "video1", CommandPriority.Medium, CommandPriority.High, false));
        Check(PlaybackPriorityEvent.TryDescribe(immediate, out text) && text.StartsWith("Ignored Ghost2.mp4 on experience-test/Left/video1"), "One-shot rejection is labeled ignored rather than deferred");
        var coordinator = new VideoPlaybackPriority();
        var identity = VideoPlaybackIdentity.For(@"C:\Media\Eye.mp4", "display", new() { ScreenId = "one" });
        using var active = coordinator.TryBegin(CommandPriority.High, identity);
        var leases = coordinator.TryBeginBatch(CommandPriority.Low, [identity, identity with { Screen = "two" }], out var blockers);
        Check(leases[0] is null && blockers[0] == CommandPriority.High && leases[1] is not null && blockers[1] is null,
            "Blocking priority is captured atomically per output without reporting eligible screens");
        foreach (var lease in leases) lease?.Dispose();
    }
}
