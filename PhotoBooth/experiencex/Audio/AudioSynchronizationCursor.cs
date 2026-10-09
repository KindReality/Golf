namespace ExperienceX;

// A continuous media cursor follows a shared clock without periodically seeking or dropping samples.
internal sealed class AudioSynchronizationCursor
{
    private bool _initialized;
    public double Position
    {
        get; private set;
    }
    public double ErrorSeconds
    {
        get; private set;
    }
    public double Correction
    {
        get; private set;
    }

    public double Plan(double desiredPosition, int sourceRate, double nominalStep)
    {
        if (!_initialized || Position < 0)
        {
            Position = desiredPosition;
            _initialized = true;
        }
        ErrorSeconds = (desiredPosition - Position) / sourceRate;
        // Limit pitch change to 0.2%; a 250 ms servo corrects normal clock drift gently.
        Correction = Math.Clamp(ErrorSeconds / 0.25, -0.002, 0.002);
        return nominalStep * (1 + Correction);
    }
    public void Advance(double step) => Position += step;
    public void Resynchronize(double desiredPosition)
    {
        Position = desiredPosition;
        _initialized = true;
    }
}

internal sealed class AudioClockMapping(int sampleRate, ulong frequency)
{
    private double _anchorFrame, _anchorTime;
    private bool _initialized;
    public double Frame
    {
        get; private set;
    }
    public double Time
    {
        get; private set;
    }
    public double Rate { get; private set; } = sampleRate;

    public bool Observe(ulong position, ulong qpc100Nanoseconds)
    {
        if (position == 0 || qpc100Nanoseconds == 0 || frequency == 0)
            return false;
        var frame = position / (double)frequency * sampleRate;
        var time = qpc100Nanoseconds / 10_000_000.0;
        if (_initialized && (frame < Frame || time <= Time))
            return false;
        Frame = frame;
        Time = time;
        if (!_initialized)
        {
            _anchorFrame = frame;
            _anchorTime = time;
            _initialized = true;
        }
        else if (time - _anchorTime >= 0.5)
        {
            var observedRate = (frame - _anchorFrame) / (time - _anchorTime);
            if (Math.Abs(observedRate / sampleRate - 1) < 0.01)
                Rate += (observedRate - Rate) * 0.1;
            _anchorFrame = frame;
            _anchorTime = time;
        }
        return true;
    }
    public double PresentationTime(long submittedFrame) => Time + (submittedFrame - Frame) / Rate;
}
