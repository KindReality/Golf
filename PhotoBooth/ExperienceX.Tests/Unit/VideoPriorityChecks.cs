using ExperienceX;

internal static class VideoPriorityChecks
{
    public static void Run()
    {
        var priority = new VideoPlaybackPriority();
        using var ambientA = priority.TryBegin(true)!;
        using var ambientB = priority.TryBegin(true)!;
        var wakes = 0;
        ambientA.WakeWhenInterrupted(() => Interlocked.Increment(ref wakes));
        using var normalA = priority.TryBegin(false)!;
        Check(ambientA.IsInterrupted && ambientB.IsInterrupted && wakes == 1, "Normal video interrupts ambient leases across every output, including pending opens.");
        ambientB.WakeWhenInterrupted(() => Interlocked.Increment(ref wakes));
        Check(wakes == 2, "An interrupted video still wakes when its worker attaches late.");
        Check(priority.TryBegin(true) is null, "Ambient video is skipped while normal video is active.");
        using var normalB = priority.TryBegin(false)!;
        Check(!normalA.IsInterrupted && !normalB.IsInterrupted, "Normal videos can coexist across screens.");
        normalA.Dispose();
        Check(priority.TryBegin(true) is null, "Every normal output must finish before ambient becomes eligible.");
        normalB.Dispose();
        using var resumed = priority.TryBegin(true);
        Check(resumed is not null && !resumed.IsInterrupted, "A fresh ambient request is eligible after the last normal video completes.");

        var mailbox = new RendererMailbox();
        using var pending = priority.TryBegin(false)!;
        mailbox.Post("Playback", () => { }, pending.Dispose);
        mailbox.Close();
        using var afterDiscard = priority.TryBegin(true);
        Check(afterDiscard is not null, "Discarding a queued normal request releases its priority reservation.");
        var plain = ExperienceRequest.Parse("{\"experience\":\"video\",\"value\":\"Eye.mp4\"}"u8);
        var ambient = ExperienceRequest.Parse("{\"experience\":\"video\",\"value\":\"Eye.mp4\",\"context\":{\"isAmbient\":true}}"u8);
        Check(!plain.IsAmbient && ambient.IsAmbient, "Commands without ambient metadata default to normal.");
        var promotion = new VideoPlaybackPriority();
        var identity = VideoLoopIdentity.For("C:\\Media\\Eye.mp4", "DISPLAY1", new() { Loop = true, ScreenId = "a", Mute = true })!;
        using var continuing = promotion.TryBegin(true, identity)!;
        using var other = promotion.TryBegin(true, identity with { Screen = "B" })!;
        using var promoted = promotion.TryBegin(false, identity)!;
        Check(!continuing.IsInterrupted && other.IsInterrupted && promotion.TryBegin(true) is null, "Loop promotion preserves its decoder while interrupting other ambient outputs and blocking new ambient requests.");
        var batch = new VideoPlaybackPriority();
        var identities = new[] { identity, identity with { Screen = "B" } };
        var ambientOutputs = batch.TryBeginBatch(true, identities)!;
        var normalOutputs = batch.TryBeginBatch(false, identities)!;
        Check(ambientOutputs.All(item => !item.IsInterrupted), "A broadcast promotion preserves all matching loops atomically.");
        foreach (var lease in ambientOutputs.Concat(normalOutputs)) lease.Dispose();
        using var afterBatch = batch.TryBegin(true);
        Check(afterBatch is not null, "All batch reservations are released after their outputs finish or fail.");
        Console.WriteLine("Passed device-wide video priority, pending-request interruption, overlapping normal playback, completion/discard cleanup and wire defaults.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
