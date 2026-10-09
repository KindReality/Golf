using NAudio.CoreAudioApi;
using PhotoBooth.Diagnostics;
using System.Windows.Threading;

namespace ExperienceX;

internal sealed class AudioManager(Dispatcher dispatcher) : IDisposable
{
    // Each request owns a shared-mode WASAPI stream. Windows mixes overlapping tracks,
    // including requests routed to the same endpoint and channel.
    private readonly HashSet<AudioPlayback> _playing = [];
    private readonly HashSet<CoordinatedAudioPlayback> _groups = [];
    private bool _disposed;
    private string? _inventorySignature;
    private ExperienceOptions _options = new();
    internal int Count => _playing.Count + _groups.Sum(group => group.ActiveOutputCount);

    internal static (AudioOutput[] Devices, string? DefaultId) ReadInventory()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<AudioOutput>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
                devices.Add(new(device.ID, device.FriendlyName, device.AudioClient.MixFormat.Channels));
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

    public async Task PlayAsync(string path, ExperienceRequest request, ExperienceOptions options, CancellationToken cancellation = default)
    {
        AudioPlayback? prepared = null;
        CoordinatedAudioPlayback? group = null;
        try
        {
            var inventory = await Task.Run(ReadInventory, cancellation);
            if (_disposed || cancellation.IsCancellationRequested)
                return;
            var selection = AudioDeviceSelection.Resolve(request, options, inventory.Devices, inventory.DefaultId);
            if (selection.Unavailable.Length > 0)
            {
                Telemetry.Warning("AudioDeviceUnavailable", new
                {
                    request.AudioDevice,
                    request.AudioDevices,
                    options.DefaultAudioDevice,
                    selection.Unavailable
                }, request.RequestId);
            }
            var targets = selection.Devices.Where(device => request.Channel is null || request.Channel <= device.Channels).ToArray();
            foreach (var invalid in selection.Devices.Except(targets))
            {
                Telemetry.Warning("AudioChannelUnavailable", new
                {
                    invalid.Name,
                    invalid.Channels,
                    request.Channel
                }, request.RequestId);
            }
            if (targets.Length == 0)
                return;
            Telemetry.Info("AudioOutputsSelected", new
            {
                Devices = targets,
                request.AudioDevice,
                request.AudioDevices
            }, request.RequestId);
            if (targets.Length > 1)
            {
                group = new(targets, path, request, options);
                await group.Prepared.WaitAsync(cancellation);
                if (_disposed || cancellation.IsCancellationRequested)
                {
                    group.Dispose();
                    return;
                }
                group.UpdateOptions(_options);
                _groups.Add(group);
                group.Start((playback, error) =>
                {
                    if (!dispatcher.HasShutdownStarted)
                        dispatcher.BeginInvoke(new Action(() => GroupCompleted(playback, error)));
                });
                return;
            }
            var selected = targets[0];
            prepared = await Task.Run(() => new AudioPlayback(selected, path, request, options), cancellation);
            if (_disposed || cancellation.IsCancellationRequested)
            {
                prepared.Dispose();
                return;
            }
            prepared.UpdateOptions(_options);
            _playing.Add(prepared);
            prepared.Start((playback, error) =>
            {
                if (!dispatcher.HasShutdownStarted)
                    dispatcher.BeginInvoke(new Action(() => Completed(playback, error)));
            });
            Telemetry.Info("AudioPlaybackStarted", new
            {
                Path = path,
                DeviceId = selected.Id,
                DeviceName = selected.Name,
                request.Channel,
                request.Mute,
                Volume = request.VolumePercent,
                ActivePlaybacks = _playing.Count,
                options.MaxAudioPlaybackLength,
                options.AudioTransitionInSeconds,
                options.AudioTransitionOutSeconds
            }, request.RequestId);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { prepared?.Dispose(); group?.Dispose(); }
        catch (Exception ex)
        {
            if (group is not null)
            {
                _groups.Remove(group);
                group.Dispose();
            }
            if (prepared is not null)
            {
                _playing.Remove(prepared);
                prepared.Dispose();
            }
            Telemetry.Error("AudioPlaybackFailed", ex, new
            {
                request.Value,
                request.AudioDevice,
                request.AudioDevices,
                request.Channel,
                Volume = request.VolumePercent
            }, request.RequestId);
        }
    }

    private void GroupCompleted(CoordinatedAudioPlayback playback, Exception? error)
    {
        if (!_groups.Remove(playback))
            return;
        if (error is not null)
            Telemetry.Error("AudioPlaybackFailed", error, new
            {
                playback.Request.AudioDevice,
                playback.Request.AudioDevices
            }, playback.Request.RequestId);
        playback.Dispose();
    }

    private void Completed(AudioPlayback playback, Exception? error)
    {
        if (!_playing.Remove(playback))
            return;
        if (error is not null)
            Telemetry.Error("AudioPlaybackFailed", error, new
            {
                playback.DeviceName,
                playback.Request.Channel
            }, playback.Request.RequestId);
        else
            Telemetry.Info("AudioPlaybackCompleted", new
            {
                playback.DeviceName,
                playback.Request.Channel,
                Volume = playback.Request.VolumePercent,
                playback.PositionSeconds,
                Reason = playback.EndReason
            }, playback.Request.RequestId);
        playback.Dispose();
    }

    public void CheckDevices((AudioOutput[] Devices, string? DefaultId) inventory)
    {
        var signature = System.Text.Json.JsonSerializer.Serialize(inventory.Devices) + inventory.DefaultId;
        if (signature != _inventorySignature)
        {
            Telemetry.Info("AudioOutputsAvailable", new
            {
                inventory.Devices,
                inventory.DefaultId
            });
            _inventorySignature = signature;
        }
        foreach (var playback in _playing.ToArray())
        {
            if (inventory.Devices.Any(d => d.Id == playback.DeviceId))
                continue;
            Telemetry.Warning("AudioDeviceDisconnected", new
            {
                playback.DeviceName,
                playback.Request.Channel
            }, playback.Request.RequestId);
            _playing.Remove(playback);
            playback.Dispose();
        }
        var available = inventory.Devices.Select(device => device.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in _groups)
            group.CheckDevices(available);
    }

    public void UpdateOptions(ExperienceOptions options)
    {
        _options = options;
        foreach (var playback in _playing)
            playback.UpdateOptions(options);
        foreach (var group in _groups)
            group.UpdateOptions(options);
    }
    public void Dispose()
    {
        _disposed = true;
        foreach (var playback in _playing)
            playback.Dispose();
        _playing.Clear();
        foreach (var group in _groups)
            group.Dispose();
        _groups.Clear();
    }
}
