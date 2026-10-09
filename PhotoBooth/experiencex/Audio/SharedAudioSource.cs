using NAudio.Wave;

namespace ExperienceX;

// One decoder per request, with a bounded PCM history shared by its output cursors.
// Owned entirely by the group's audio thread; no locks or file reads on the UI thread.
internal sealed class SharedAudioSource : IDisposable
{
    private readonly AudioFileReader _reader;
    private readonly float[] _ring;
    private readonly float[] _decode;
    private readonly int _capacity;
    private long _decodedFrames;
    private bool _ended;
    public int SampleRate => _reader.WaveFormat.SampleRate;
    public int Channels => _reader.WaveFormat.Channels;
    public double DurationSeconds
    {
        get; private set;
    }

    public SharedAudioSource(string path, double historySeconds)
    {
        _reader = new(path);
        try
        {
            DurationSeconds = _reader.TotalTime.TotalSeconds;
            _capacity = checked((int)Math.Ceiling(historySeconds * SampleRate) + 4096);
            _ring = new float[checked(_capacity * Channels)];
            _decode = new float[4096 * Channels];
            Ensure(Math.Min(4095, (long)(DurationSeconds * SampleRate)));
        }
        catch { _reader.Dispose(); throw; }
    }
    public void Ensure(long frame)
    {
        while (!_ended && _decodedFrames <= frame)
        {
            var count = _reader.Read(_decode, 0, _decode.Length);
            var frames = count / Channels;
            if (frames == 0)
            {
                _ended = true;
                DurationSeconds = Math.Min(DurationSeconds, _decodedFrames / (double)SampleRate);
                break;
            }
            for (var index = 0; index < frames; index++)
                _decode.AsSpan(index * Channels, Channels).CopyTo(_ring.AsSpan((int)((_decodedFrames + index) % _capacity) * Channels, Channels));
            _decodedFrames += frames;
        }
    }
    public float Sample(long frame, int channel)
    {
        if (frame < 0 || frame >= _decodedFrames)
            return 0;
        if (frame < _decodedFrames - _capacity)
            throw new InvalidOperationException("Audio output fell outside the shared decoder history.");
        return _ring[(int)(frame % _capacity) * Channels + channel];
    }
    public void Dispose() => _reader.Dispose();
}

// Polyphase windowed-sinc interpolation keeps drift correction band-limited, including
// endpoints with different sample rates. Tables are prepared once per output.
internal sealed class AudioResampler
{
    public const int Taps = 32;
    private const int Phases = 1024;
    private readonly float[] _coefficients = new float[Taps * Phases];

    public AudioResampler(int sourceRate, int outputRate)
    {
        var cutoff = Math.Min(1.0, outputRate / (double)sourceRate) * 0.97;
        for (var phase = 0; phase < Phases; phase++)
        {
            double sum = 0;
            for (var tap = 0; tap < Taps; tap++)
            {
                var x = tap - (Taps / 2 - 1) - phase / (double)Phases;
                var sinc = Math.Abs(x) < 1e-12 ? cutoff : Math.Sin(Math.PI * cutoff * x) / (Math.PI * x);
                var window = 0.5 + 0.5 * Math.Cos(Math.PI * x / (Taps / 2));
                var value = Math.Abs(x) <= Taps / 2 ? sinc * window : 0;
                _coefficients[phase * Taps + tap] = (float)value;
                sum += value;
            }
            for (var tap = 0; tap < Taps; tap++)
                _coefficients[phase * Taps + tap] /= (float)sum;
        }
    }
    public void Read(SharedAudioSource source, double position, Span<float> channels)
    {
        channels.Clear();
        if (position < 0 || position >= source.DurationSeconds * source.SampleRate)
            return;
        var whole = (long)Math.Floor(position);
        var phase = Math.Min(Phases - 1, (int)((position - whole) * Phases));
        var first = whole - (Taps / 2 - 1);
        for (var tap = 0; tap < Taps; tap++)
        {
            var weight = _coefficients[phase * Taps + tap];
            for (var channel = 0; channel < channels.Length; channel++)
                channels[channel] += source.Sample(first + tap, channel) * weight;
        }
    }
}
