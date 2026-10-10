using System.Diagnostics;

namespace ExperienceX;

internal readonly record struct VideoPlaybackFrame(double Opacity, bool FadeStarted, double FadeSeconds,
    bool Complete, string StopReason);

internal sealed class VideoPlaybackTimeline
{
    private readonly Stopwatch _clock = new();
    private double _fadeAt, _fadeDuration, _fadeFrom;
    private bool _fading;
    private bool _loop, _stopping, _reportFade;
    public bool Started
    {
        get; private set;
    }
    public double Opacity
    {
        get; private set;
    }
    public void Reset(bool loop = false)
    {
        _clock.Reset();
        _fading = false;
        _loop = loop;
        _stopping = _reportFade = false;
        Started = false;
        Opacity = 0;
    }
    // Start only after presenting the first advancing video frame at zero alpha.
    public void Start()
    {
        Started = true;
        _clock.Restart();
    }
    public void RequestStop(double fadeSeconds) => StopAt(_clock.Elapsed.TotalSeconds, fadeSeconds);
    internal void StopAt(double elapsed, double fadeSeconds)
    {
        if (_stopping) return; // A duplicate stop must never extend the fade.
        _stopping = _fading = _reportFade = true;
        _fadeAt = elapsed;
        _fadeDuration = fadeSeconds;
        _fadeFrom = Opacity;
    }
    public VideoPlaybackFrame Advance(ExperienceOptions options, double? duration, double position, bool ended)
        => AdvanceAt(_clock.Elapsed.TotalSeconds, options, duration, position, ended);
    internal VideoPlaybackFrame AdvanceAt(double elapsed, ExperienceOptions options, double? duration, double position, bool ended)
    {
        var timing = PlaybackTiming.Evaluate(_loop || _stopping ? null : duration, position, options.TransitionOutSeconds);
        bool fadeStarted = _reportFade || !_fading && timing.ShouldFade;
        _reportFade = false;
        if (fadeStarted && !_stopping)
        {
            _fadeAt = elapsed;
            _fadeDuration = timing.FadeSeconds;
            _fadeFrom = Opacity;
            _fading = true;
        }
        Opacity = _fading ? _fadeFrom * Math.Clamp(1 - (elapsed - _fadeAt) / Math.Max(0.000001, _fadeDuration), 0, 1)
            : Math.Clamp(elapsed / Math.Max(0.000001, options.TransitionInSeconds), 0, 1);
        return new(Opacity, fadeStarted, _fadeDuration,
            (!_loop && !_stopping && (ended || timing.RemainingSeconds <= 0)) || (_fading && elapsed - _fadeAt >= _fadeDuration),
            _stopping ? "SceneStopped" : timing.StopReason);
    }
}
