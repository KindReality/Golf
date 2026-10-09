using NAudio.CoreAudioApi;
using PhotoBooth.Diagnostics;
using System.Windows.Threading;

namespace Experience;

internal sealed class AudioManager(Dispatcher dispatcher) : IDisposable
{
    private readonly Dictionary<(string Device, int? Channel), AudioPlayback> _playing = [];
    private readonly Dictionary<(string Device, int? Channel), long> _latest = [];
    private long _sequence;
    private bool _disposed;
    private string? _inventorySignature;
    private ExperienceOptions _options = new();
    internal int Count => _playing.Count;

    internal static (AudioOutput[] Devices, string? DefaultId) ReadInventory()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<AudioOutput>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device) devices.Add(new(device.ID, device.FriendlyName, device.AudioClient.MixFormat.Channels));
        }
        string? defaultId = null;
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = device.ID;
        }
        catch (System.Runtime.InteropServices.COMException) { /* No default render endpoint. */ }
        return (devices.ToArray(), defaultId);
    }

    public async Task PlayAsync(string path, ExperienceRequest request, ExperienceOptions options)
    {
        var order = ++_sequence;
        AudioPlayback? prepared = null;
        try
        {
            var inventory = await Task.Run(ReadInventory);
            if (_disposed) return;
            var selected = OutputSelection.Audio(request.AudioDevice, options.DefaultAudioDevice, inventory.Devices, inventory.DefaultId);
            if (selected is null)
            {
                Telemetry.Warning("AudioDeviceUnavailable", new { request.AudioDevice, options.DefaultAudioDevice }, request.RequestId);
                return;
            }
            if (request.Channel > selected.Channels)
            {
                Telemetry.Warning("AudioChannelUnavailable", new { selected.Name, selected.Channels, request.Channel }, request.RequestId);
                return;
            }
            var slot = (selected.Id, request.Channel);
            if (_latest.TryGetValue(slot, out var newer) && newer > order) return;
            _latest[slot] = order;
            prepared = await Task.Run(() => new AudioPlayback(selected, path, request, options));
            if (_disposed || _latest[slot] != order) { prepared.Dispose(); return; }
            prepared.UpdateOptions(_options);
            if (_playing.Remove(slot, out var previous))
            {
                Telemetry.Info("AudioPlaybackReplaced", new { NextRequestId = request.RequestId, previous.DeviceName,
                    request.Channel, previous.PositionSeconds }, previous.Request.RequestId);
                previous.Dispose();
            }
            _playing[slot] = prepared;
            prepared.Start((playback, error) =>
            {
                if (!dispatcher.HasShutdownStarted)
                    dispatcher.BeginInvoke(new Action(() => Completed(slot, playback, error)));
            });
            Telemetry.Info("AudioPlaybackStarted", new { Path = path, DeviceId = selected.Id, DeviceName = selected.Name,
                request.Channel, request.Mute, options.MaxAudioPlaybackLength,
                options.AudioTransitionInSeconds, options.AudioTransitionOutSeconds }, request.RequestId);
        }
        catch (Exception ex)
        {
            if (prepared is not null)
            {
                var slot = (prepared.DeviceId, request.Channel);
                if (_playing.TryGetValue(slot, out var current) && ReferenceEquals(current, prepared)) _playing.Remove(slot);
                prepared.Dispose();
            }
            Telemetry.Error("AudioPlaybackFailed", ex, new { request.Value, request.AudioDevice, request.Channel }, request.RequestId);
        }
    }

    private void Completed((string Device, int? Channel) slot, AudioPlayback playback, Exception? error)
    {
        if (!_playing.TryGetValue(slot, out var current) || !ReferenceEquals(current, playback)) return;
        _playing.Remove(slot);
        if (error is not null) Telemetry.Error("AudioPlaybackFailed", error, new { playback.DeviceName, playback.Request.Channel }, playback.Request.RequestId);
        else Telemetry.Info("AudioPlaybackCompleted", new { playback.DeviceName, playback.Request.Channel,
            playback.PositionSeconds, Reason = playback.EndReason }, playback.Request.RequestId);
        playback.Dispose();
    }

    public void CheckDevices((AudioOutput[] Devices, string? DefaultId) inventory)
    {
        var signature = System.Text.Json.JsonSerializer.Serialize(inventory.Devices) + inventory.DefaultId;
        if (signature != _inventorySignature)
        {
            Telemetry.Info("AudioOutputsAvailable", new { inventory.Devices, inventory.DefaultId });
            _inventorySignature = signature;
        }
        foreach (var pair in _playing.ToArray())
        {
            if (inventory.Devices.Any(d => d.Id == pair.Key.Device)) continue;
            Telemetry.Warning("AudioDeviceDisconnected", new { pair.Value.DeviceName, pair.Key.Channel }, pair.Value.Request.RequestId);
            _playing.Remove(pair.Key);
            pair.Value.Dispose();
        }
    }

    public void UpdateOptions(ExperienceOptions options)
    {
        _options = options;
        foreach (var playback in _playing.Values) playback.UpdateOptions(options);
    }
    public void Dispose()
    {
        _disposed = true;
        foreach (var playback in _playing.Values) playback.Dispose();
        _playing.Clear();
    }
}
