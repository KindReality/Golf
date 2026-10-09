using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Experience;

internal sealed class AudioPlayback : IDisposable
{
    private MMDevice? _device;
    private AudioFileReader? _reader;
    private WasapiOut? _output;
    private RoutedAudioProvider? _provider;
    public string DeviceId { get; }
    public string DeviceName { get; }
    public ExperienceRequest Request { get; }
    public double PositionSeconds => _provider?.PositionSeconds ?? 0;
    public string EndReason => _provider?.EndReason ?? "MediaEnded";

    public AudioPlayback(AudioOutput device, string path, ExperienceRequest request, ExperienceOptions options)
    {
        DeviceId = device.Id;
        DeviceName = device.Name;
        Request = request;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDevice(device.Id);
            if (_device.State != DeviceState.Active) throw new InvalidOperationException("Audio device is unavailable.");
            _reader = new AudioFileReader(path);
            var mix = _device.AudioClient.MixFormat;
            ISampleProvider source = _reader;
            if (source.WaveFormat.SampleRate != mix.SampleRate)
                source = new WdlResamplingSampleProvider(source, mix.SampleRate);
            _provider = new RoutedAudioProvider(source, mix.Channels, request.Channel, request.Mute, _reader.TotalTime.TotalSeconds, options);
            _output = new WasapiOut(_device, AudioClientShareMode.Shared, true, 100);
            _output.Init((IWaveProvider)_provider);
        }
        catch { Dispose(); throw; }
    }

    public void Start(Action<AudioPlayback, Exception?> completed)
    {
        _output!.PlaybackStopped += (_, args) => completed(this, args.Exception);
        _output.Play();
    }
    public void UpdateOptions(ExperienceOptions options) => _provider?.UpdateOptions(options);
    public void Dispose()
    {
        _output?.Dispose();
        _output = null;
        _reader?.Dispose();
        _reader = null;
        _device?.Dispose();
        _device = null;
    }
}
