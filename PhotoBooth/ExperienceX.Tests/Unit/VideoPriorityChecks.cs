using ExperienceX;
using PhotoBooth.Networking;

internal static class VideoPriorityChecks
{
    public static void Run()
    {
        var priority = new VideoPlaybackPriority();
        var a = VideoPlaybackIdentity.For(@"C:\Media\Eye.mp4", "DISPLAY1", new() { ScreenId = "a" });
        var b = a with { Screen = "B" };
        var other = a with { Path = @"C:\MEDIA\OTHER.MP4" };
        using var backgroundA = priority.TryBegin(CommandPriority.Low, a)!;
        using var backgroundB = priority.TryBegin(CommandPriority.Low, b)!;
        var wakes = 0;
        backgroundA.WakeWhenInterrupted(() => Interlocked.Increment(ref wakes));
        using var foreground = priority.TryBegin(CommandPriority.Medium, other)!;
        Check(backgroundA.IsInterrupted && !backgroundB.IsInterrupted && wakes == 1, "Priority replaces only the targeted screen, including a queued open.");
        Check(priority.TryBegin(CommandPriority.Low, a) is null, "Lower priority cannot replace a foreground video.");
        using var otherScreen = priority.TryBegin(CommandPriority.Low, b);
        Check(otherScreen is not null, "Another screen remains eligible for background playback.");
        using var equal = priority.TryBegin(CommandPriority.Medium, a)!;
        Check(foreground.IsInterrupted, "Equal priority replaces a different video on the same screen.");
        using var high = priority.TryBegin(CommandPriority.High, a)!;
        Check(!equal.IsInterrupted && priority.TryBegin(CommandPriority.Medium, other) is null, "Same-video promotion preserves playback while reserving higher priority.");
        high.Dispose(); equal.Dispose();
        using var after = priority.TryBegin(CommandPriority.Low, a);
        Check(after is not null, "Completion releases the reservation.");
        backgroundA.WakeWhenInterrupted(() => Interlocked.Increment(ref wakes));
        Check(wakes == 2, "A worker attaching late is still notified of interruption.");

        var batch = new VideoPlaybackPriority();
        using var held = batch.TryBegin(CommandPriority.High, other)!;
        var results = batch.TryBeginBatch(CommandPriority.Low, [a, b]);
        Check(results[0] is null && results[1] is not null, "Broadcast eligibility is evaluated per screen.");
        foreach (var lease in results) lease?.Dispose();
        var mailbox = new RendererMailbox();
        using var queued = batch.TryBegin(CommandPriority.High, b)!;
        mailbox.Post("Playback", () => { }, queued.Dispose); mailbox.Close();
        using var released = batch.TryBegin(CommandPriority.Low, b);
        Check(released is not null, "Discarding pending playback releases its reservation.");

        var plain = ExperienceRequest.Parse("{\"experience\":\"video\",\"value\":\"Eye.mp4\"}"u8);
        var ambient = ExperienceRequest.Parse("{\"experience\":\"video\",\"value\":\"Eye.mp4\",\"context\":{\"isAmbient\":true}}"u8);
        var loop = ExperienceRequest.Parse("{\"experience\":\"video\",\"value\":\"Eye.mp4\",\"priority\":\"high\",\"loop\":true}"u8);
        Check(plain.EffectivePriority == CommandPriority.Medium && ambient.EffectivePriority == CommandPriority.Low && loop.EffectivePriority == CommandPriority.Low, "Wire defaults, ambient compatibility and loop priority are explicit.");
        foreach (var invalid in new[] { "0", "\"urgent\"" })
        {
            bool rejected = false;
            try { ExperienceRequest.Parse(System.Text.Encoding.UTF8.GetBytes("{\"experience\":\"video\",\"value\":\"Eye.mp4\",\"priority\":" + invalid + "}")); }
            catch (System.Text.Json.JsonException) { rejected = true; }
            Check(rejected, "Unknown and numeric priorities are rejected.");
        }
        Check(a == VideoPlaybackIdentity.For(@"c:\media\eye.mp4", "display1", new() { ScreenId = "A", Loop = true, Mute = true }), "Video identity ignores loop and mute flags.");
        Console.WriteLine("Passed per-screen priority reservations, batch isolation, same-media promotion, release and wire validation.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
