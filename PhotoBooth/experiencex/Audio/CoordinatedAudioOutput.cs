using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ExperienceX;

// All COM calls and rendering for this endpoint belong to its group's dedicated thread.
internal sealed class CoordinatedAudioOutput : IDisposable
{
    private readonly MMDevice _device = null!;
    private readonly AudioClient _client;
    private readonly AudioRenderClient _render;
    private readonly AudioClockClient _clock;
    private readonly AudioClockMapping _mapping;
    private readonly AudioSynchronizationCursor _cursor = new();
    private readonly AudioResampler _resampler;
    private readonly float[] _sourceFrame;
    private readonly ExperienceRequest _request;
    private readonly double _latency;
    private readonly int _rate, _channels, _bufferFrames;
    private long _submitted, _endFrame = long.MaxValue;
    private double _lastProgress, _previousClockFrame;
    private bool _started;
    private int _recoveryFadeFrames;
    public AudioOutput Device
    {
        get;
    }
    public AutoResetEvent Ready { get; } = new(false);
    public bool Complete
    {
        get; private set;
    }
    public double PositionSeconds
    {
        get; private set;
    }
    public double ErrorMilliseconds => _cursor.ErrorSeconds * 1000;
    public double ClockRatePpm => (_mapping.Rate / _rate - 1) * 1_000_000;
    public int Underruns
    {
        get; private set;
    }
    public double StreamLatencyMilliseconds => _client.StreamLatency / 10_000.0;
    public double BufferSeconds => _bufferFrames / (double)_rate;
    public double AdditionalLatencySeconds => _latency;

    public CoordinatedAudioOutput(MMDeviceEnumerator enumerator, AudioOutput device, SharedAudioSource source,
        ExperienceRequest request, ExperienceOptions options)
    {
        Device = device;
        _request = request;
        _latency = AudioDeviceSelection.LatencySeconds(device, options);
        _sourceFrame = new float[source.Channels];
        try
        {
            _device = enumerator.GetDevice(device.Id);
            _client = _device.AudioClient;
            var mix = _client.MixFormat;
            _rate = mix.SampleRate;
            _channels = mix.Channels;
            if (request.Channel > _channels || (!request.Channel.HasValue && source.Channels > _channels))
                throw new ArgumentException("Audio source/channel does not fit the selected endpoint layout.");
            WaveFormat format = _channels > 2 ? new WaveFormatExtensible(_rate, 32, _channels)
                : WaveFormat.CreateIeeeFloatWaveFormat(_rate, _channels);
            // Preserve native surround-channel masks rather than guessing a speaker layout.
            if (mix.BitsPerSample == 32 && (mix.Encoding == WaveFormatEncoding.IeeeFloat ||
                mix is WaveFormatExtensible extensible && extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")))
                format = mix;
            _client.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.NoPersist | AudioClientStreamFlags.AutoConvertPcm,
                1_000_000, 0, format, Guid.Empty);
            _client.SetEventHandle(Ready.SafeWaitHandle.DangerousGetHandle());
            _bufferFrames = _client.BufferSize;
            _render = _client.AudioRenderClient;
            _clock = _client.AudioClockClient;
            _mapping = new(_rate, _clock.Frequency);
            _resampler = new(source.SampleRate, _rate);
            // Prime silence; content is scheduled only after every endpoint has started.
            _render.GetBuffer(_bufferFrames);
            _render.ReleaseBuffer(_bufferFrames, AudioClientBufferFlags.Silent);
            _submitted = _bufferFrames;
        }
        catch { _client?.Dispose(); _device?.Dispose(); Ready.Dispose(); throw; }
    }

    public void Start()
    {
        _client.Start();
        _started = true;
        _lastProgress = Now;
    }
    internal static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public unsafe void Pump(SharedAudioSource source, double startTime, ExperienceOptions options)
    {
        var now = Now;
        if (!_clock.GetPosition(out var position, out var qpc) || !_mapping.Observe(position, qpc))
        {
            if (now - _lastProgress > 2)
                throw new TimeoutException("Audio device clock did not become available.");
            return;
        }
        if (_mapping.Frame > _previousClockFrame)
        {
            _lastProgress = now;
            _previousClockFrame = _mapping.Frame;
        }
        else if (now - _lastProgress > 2)
            throw new TimeoutException("Audio device clock stopped advancing.");
        var end = Math.Min(source.DurationSeconds, options.MaxAudioPlaybackLength);
        PositionSeconds = Math.Clamp(now + _latency - startTime, 0, end);
        if (_mapping.Frame >= _endFrame - 1)
        {
            Complete = true;
            PositionSeconds = end;
            return;
        }
        if (_endFrame != long.MaxValue)
            return; // Let final samples drain before stopping.
        var padding = _client.CurrentPadding;
        var available = _bufferFrames - padding;
        if (available <= 0)
            return;
        if (padding == 0)
        {
            Underruns++;
            // An empty endpoint stops consuming queued frames; restart its media cursor
            // against the common clock, rather than accumulating a permanent offset.
            _submitted = Math.Max(_submitted, (long)Math.Ceiling(_mapping.Frame));
        }
        var desired = (_mapping.PresentationTime(_submitted) + _latency - startTime) * source.SampleRate;
        if (padding == 0)
        {
            _cursor.Resynchronize(desired);
            _recoveryFadeFrames = Math.Max(1, _rate / 100); // 10 ms ramp after recovering a gap.
        }
        var step = _cursor.Plan(desired, source.SampleRate, source.SampleRate / _mapping.Rate);
        source.Ensure((long)Math.Ceiling(_cursor.Position + available * step) + AudioResampler.Taps);
        end = Math.Min(source.DurationSeconds, options.MaxAudioPlaybackLength);
        var pointer = _render.GetBuffer(available);
        var released = false;
        try
        {
            var samples = new Span<float>((void*)pointer, available * _channels);
            samples.Clear();
            for (var frame = 0; frame < available; frame++)
            {
                var time = _cursor.Position / source.SampleRate;
                if (time >= end)
                {
                    _endFrame = Math.Min(_endFrame, _submitted + frame);
                    break;
                }
                if (time >= 0)
                {
                    _resampler.Read(source, _cursor.Position, _sourceFrame);
                    var output = samples.Slice(frame * _channels, _channels);
                    AudioSamples.Route(_sourceFrame, output, _request.Channel);
                    var gain = _request.Mute ? 0 : (float)(_request.VolumePercent / 100 * AudioSamples.Gain(time, end,
                        options.AudioTransitionInSeconds, options.AudioTransitionOutSeconds));
                    if (_recoveryFadeFrames > 0)
                    {
                        gain *= 1 - _recoveryFadeFrames / (float)Math.Max(1, _rate / 100);
                        _recoveryFadeFrames--;
                    }
                    for (var channel = 0; channel < _channels; channel++)
                        output[channel] *= gain;
                }
                _cursor.Advance(step);
            }
            _render.ReleaseBuffer(available, AudioClientBufferFlags.None);
            released = true;
            _submitted += available;
        }
        finally
        {
            if (!released)
                _render.ReleaseBuffer(available, AudioClientBufferFlags.Silent);
        }
    }

    public void Dispose()
    {
        try
        {
            if (_started)
                _client.Stop();
        }
        finally { _client.Dispose(); _device.Dispose(); Ready.Dispose(); }
    }
}
