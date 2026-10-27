namespace ExperienceX;

public sealed record PlaybackPlan(double RemainingSeconds, bool ShouldFade, double FadeSeconds, string StopReason);

public static class PlaybackTiming
{
    public static PlaybackPlan Evaluate(double? duration, double position, double fadeOut, double playbackSpeed = 1)
    {
        PhotoBooth.Networking.VideoPlaybackSpeed.Validate(playbackSpeed);
        var videoRemaining = duration.HasValue ? (duration.Value - position) / playbackSpeed : double.PositiveInfinity;
        var remaining = Math.Max(0, videoRemaining);
        return new(remaining, remaining <= fadeOut, Math.Min(fadeOut, remaining),
            "MediaEnded");
    }
}
