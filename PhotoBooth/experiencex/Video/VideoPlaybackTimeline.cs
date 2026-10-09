using System.Diagnostics;

namespace ExperienceX;

internal readonly record struct VideoPlaybackFrame(double Opacity, bool FadeStarted, double FadeSeconds,
    bool Complete, string StopReason);

internal sealed class VideoPlaybackTimeline
{
    private readonly Stopwatch _clock = new();
    private double _fadeAt, _fadeDuration, _fadeFrom;
    private bool _fading;
    public bool Started
    {
        get; private set;
    }
    public double Opacity
    {
        get; private set;
    }
    public void Reset()
    {
        _clock.Reset();
        _fading = false;
        Started = false;
        Opacity = 0;
    }
    // Start only after presenting the first advancing video frame at zero alpha.
    public void Start()
    {
        Started = true;
        _clock.Restart();
    }
    public VideoPlaybackFrame Advance(ExperienceOptions options, double? duration, double position, bool ended)
        => AdvanceAt(_clock.Elapsed.TotalSeconds, options, duration, position, ended);
    internal VideoPlaybackFrame AdvanceAt(double elapsed, ExperienceOptions options, double? duration, double position, bool ended)
    {
        var timing = PlaybackTiming.Evaluate(options.MaxVideoPlaybackLength, elapsed, duration, position, options.TransitionOutSeconds);
        bool fadeStarted = !_fading && timing.ShouldFade;
        if (fadeStarted)
        {
            _fadeAt = elapsed;
            _fadeDuration = timing.FadeSeconds;
            _fadeFrom = Opacity;
            _fading = true;
        }
        Opacity = _fading ? _fadeFrom * Math.Clamp(1 - (elapsed - _fadeAt) / Math.Max(0.000001, _fadeDuration), 0, 1)
            : Math.Clamp(elapsed / Math.Max(0.000001, options.TransitionInSeconds), 0, 1);
        return new(Opacity, fadeStarted, _fadeDuration,
            ended || timing.RemainingSeconds <= 0 || (_fading && elapsed - _fadeAt >= _fadeDuration), timing.StopReason);
    }
}
