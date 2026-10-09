using NAudio.Wave;

namespace ExperienceX;

internal sealed class RoutedAudioProvider : ISampleProvider, IWaveProvider
{
    private readonly ISampleProvider _source;
    private readonly int? _channel;
    private readonly bool _mute;
    private readonly float _volumeGain;
    private readonly double _duration;
    private ExperienceOptions _options;
    private float[] _input = [];
    private float[] _outputSamples = [];
    private long _frames;

    public RoutedAudioProvider(ISampleProvider source, int outputChannels, int? channel, bool mute,
        double duration, ExperienceOptions options, double volume = 100)
    {
        if (!double.IsFinite(volume) || volume is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be a percentage from 0 to 100.");
        if (channel is <= 0 || channel > outputChannels)
            throw new ArgumentOutOfRangeException(nameof(channel));
        if (!channel.HasValue && source.WaveFormat.Channels > outputChannels)
            throw new ArgumentException("Source layout exceeds the output layout; select a channel explicitly.");
        _source = source;
        _channel = channel;
        _mute = mute;
        _volumeGain = (float)(volume / 100);
        _duration = duration;
        _options = options;
        WaveFormat = outputChannels > 2 ? new WaveFormatExtensible(source.WaveFormat.SampleRate, 32, outputChannels)
            : WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, outputChannels);
    }

    public WaveFormat WaveFormat
    {
        get;
    }
    public double PositionSeconds => Interlocked.Read(ref _frames) / (double)WaveFormat.SampleRate;
    public string EndReason => _duration <= Volatile.Read(ref _options).MaxAudioPlaybackLength ? "MediaEnded" : "MaximumPlaybackLength";
    public void UpdateOptions(ExperienceOptions options) => Volatile.Write(ref _options, options);

    public int Read(float[] buffer, int offset, int count)
    {
        var options = Volatile.Read(ref _options);
        var end = Math.Min(_duration, options.MaxAudioPlaybackLength);
        var allowed = Math.Max(0L, (long)Math.Floor(end * WaveFormat.SampleRate) - _frames);
        var requested = (int)Math.Min(count / WaveFormat.Channels, allowed);
        if (requested == 0)
            return 0;
        var inputCount = requested * _source.WaveFormat.Channels;
        if (_input.Length < inputCount)
            _input = new float[inputCount];
        var read = _source.Read(_input, 0, inputCount);
        var frames = read / _source.WaveFormat.Channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var output = buffer.AsSpan(offset + frame * WaveFormat.Channels, WaveFormat.Channels);
            AudioSamples.Route(_input.AsSpan(frame * _source.WaveFormat.Channels, _source.WaveFormat.Channels), output, _channel);
            var gain = _mute ? 0 : _volumeGain * (float)AudioSamples.Gain((_frames + frame) / (double)WaveFormat.SampleRate,
                end, options.AudioTransitionInSeconds, options.AudioTransitionOutSeconds);
            for (var channel = 0; channel < output.Length; channel++)
                output[channel] *= gain;
        }
        Interlocked.Add(ref _frames, frames);
        return frames * WaveFormat.Channels;
    }

    int IWaveProvider.Read(byte[] buffer, int offset, int count)
    {
        var samples = count / sizeof(float);
        if (_outputSamples.Length < samples)
            _outputSamples = new float[samples];
        var read = Read(_outputSamples, 0, samples);
        Buffer.BlockCopy(_outputSamples, 0, buffer, offset, read * sizeof(float));
        return read * sizeof(float);
    }
}
