namespace ExperienceX;

public sealed record PlaybackPlan(double RemainingSeconds, bool ShouldFade, double FadeSeconds, string StopReason);

public static class PlaybackTiming
{
    public static PlaybackPlan Evaluate(double maximum, double elapsed, double? duration, double position, double fadeOut)
    {
        var maximumRemaining = maximum - elapsed;
        var videoRemaining = duration.HasValue ? duration.Value - position : double.PositiveInfinity;
        var remaining = Math.Max(0, Math.Min(maximumRemaining, videoRemaining));
        return new(remaining, remaining <= fadeOut, Math.Min(fadeOut, remaining),
            videoRemaining <= maximumRemaining ? "MediaEnded" : "MaximumPlaybackLength");
    }
}
