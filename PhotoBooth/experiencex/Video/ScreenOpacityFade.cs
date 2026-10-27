using System.Diagnostics;

namespace ExperienceX;

// Independent of the decoder's playback/fade timeline. A completed value holds until another fade command.
internal sealed class ScreenOpacityFade(Func<long>? clock = null)
{
    private readonly Func<long> _clock = clock ?? Stopwatch.GetTimestamp;
    private long _started;
    private double _from = 1, _target = 1, _seconds;
    private double Elapsed => Math.Max(0, Stopwatch.GetElapsedTime(_started, _clock()).TotalSeconds);
    internal double Value => _seconds == 0 ? _target : _from + (_target - _from) * Math.Clamp(Elapsed / _seconds, 0, 1);
    internal bool Animating => _seconds > 0 && Elapsed < _seconds;
    internal void Start(double target, double seconds)
    {
        if (!double.IsFinite(target) || target is < 0 or > 1 || !double.IsFinite(seconds) || seconds is < 0 or > 86400)
            throw new ArgumentException("Invalid screen opacity fade.");
        _from = Value; _target = target; _seconds = seconds; _started = _clock();
    }
}
