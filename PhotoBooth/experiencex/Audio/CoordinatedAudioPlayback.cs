using NAudio.CoreAudioApi;
using PhotoBooth.Diagnostics;

namespace ExperienceX;

internal sealed class CoordinatedAudioPlayback : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly ManualResetEvent _start = new(false);
    private readonly TaskCompletionSource _prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;
    private ExperienceOptions _options;
    private HashSet<string>? _available;
    private Action<CoordinatedAudioPlayback, Exception?>? _completed;
    private bool _disposed;
    private int _activeOutputs;
    public ExperienceRequest Request
    {
        get;
    }
    public Task Prepared => _prepared.Task;
    public int ActiveOutputCount => Volatile.Read(ref _activeOutputs);

    public CoordinatedAudioPlayback(AudioOutput[] devices, string path, ExperienceRequest request, ExperienceOptions options)
    {
        Request = request;
        _options = options;
        _worker = Task.Factory.StartNew(() => Run(devices, path), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    public void Start(Action<CoordinatedAudioPlayback, Exception?> completed)
    {
        _completed = completed;
        _start.Set();
    }
    public void UpdateOptions(ExperienceOptions options) => Volatile.Write(ref _options, options);
    public void CheckDevices(HashSet<string> available) => Volatile.Write(ref _available, available);

    private void Run(AudioOutput[] devices, string path)
    {
        var outputs = new List<CoordinatedAudioOutput>();
        var completedOutputs = 0;
        var failedOutputs = 0;
        Exception? failure = null;
        try
        {
            Thread.CurrentThread.Name = "Coordinated audio";
            var options = Volatile.Read(ref _options);
            var extraLatency = devices.Max(device => AudioDeviceSelection.LatencySeconds(device, options));
            using var source = new SharedAudioSource(path, extraLatency + 4);
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in devices)
            {
                _stop.Token.ThrowIfCancellationRequested();
                try
                {
                    outputs.Add(new(enumerator, device, source, Request, options));
                }
                catch (Exception ex)
                {
                    failedOutputs++;
                    Telemetry.Warning("AudioOutputPreparationFailed", new
                    {
                        device.Id,
                        device.Name,
                        Reason = ex.Message
                    }, Request.RequestId);
                }
            }
            if (outputs.Count == 0)
                throw new InvalidOperationException("No requested audio output could be prepared.");
            Volatile.Write(ref _activeOutputs, outputs.Count);
            _prepared.TrySetResult();
            if (WaitHandle.WaitAny([_stop.Token.WaitHandle, _start]) == 0)
                return;
            foreach (var output in outputs.ToArray())
            {
                try
                {
                    output.Start();
                }
                catch (Exception ex) { Remove(output, "AudioOutputStartFailed", ex.Message); }
            }
            if (outputs.Count == 0)
                throw new InvalidOperationException("No requested audio output could be started.");
            var lead = outputs.Max(output => Math.Max(output.BufferSeconds, output.StreamLatencyMilliseconds / 1000)) + 0.2 + extraLatency;
            var startTime = CoordinatedAudioOutput.Now + lead;
            Telemetry.Info("AudioGroupStarted", new
            {
                Path = path,
                Outputs = outputs.Select(output => new { output.Device.Id, output.Device.Name }).ToArray(),
                ScheduledLeadMilliseconds = lead * 1000,
                Volume = Request.VolumePercent,
                Request.Channel,
                Request.Mute
            }, Request.RequestId);
            foreach (var output in outputs)
                Telemetry.Info("AudioPlaybackStarted", new
                {
                    Path = path,
                    DeviceId = output.Device.Id,
                    DeviceName = output.Device.Name,
                    Request.Channel,
                    Request.Mute,
                    Volume = Request.VolumePercent,
                    Coordinated = true,
                    options.MaxAudioPlaybackLength,
                    options.AudioTransitionInSeconds,
                    options.AudioTransitionOutSeconds
                }, Request.RequestId);
            var nextMetrics = startTime + 1;
            while (outputs.Count > 0 && !_stop.IsCancellationRequested)
            {
                options = Volatile.Read(ref _options);
                var available = Volatile.Read(ref _available);
                foreach (var output in outputs.ToArray())
                {
                    if (available is not null && !available.Contains(output.Device.Id))
                    {
                        Remove(output, "AudioDeviceDisconnected", "Output no longer available");
                        continue;
                    }
                    try
                    {
                        output.Pump(source, startTime, options);
                        if (output.Complete)
                        {
                            completedOutputs++;
                            Telemetry.Info("AudioPlaybackCompleted", new
                            {
                                DeviceName = output.Device.Name,
                                Request.Channel,
                                Volume = Request.VolumePercent,
                                output.PositionSeconds,
                                Reason = source.DurationSeconds <= options.MaxAudioPlaybackLength ? "MediaEnded" : "MaximumPlaybackLength",
                                output.Underruns,
                                Coordinated = true
                            }, Request.RequestId);
                            outputs.Remove(output);
                            Volatile.Write(ref _activeOutputs, outputs.Count);
                            try
                            {
                                output.Dispose();
                            }
                            catch (Exception ex) { Telemetry.Warning("AudioOutputCleanupFailed", new { output.Device.Id, Reason = ex.Message }, Request.RequestId); }
                        }
                    }
                    catch (Exception ex) { Remove(output, "AudioOutputFailed", ex.Message); }
                }
                if (CoordinatedAudioOutput.Now >= nextMetrics && outputs.Count > 0)
                {
                    Telemetry.Info("AudioSynchronizationMeasured", new
                    {
                        Measurement = "Endpoint clock/cursor estimates; excludes acoustic and unreported downstream delays",
                        Outputs = outputs.Select(output => new
                        {
                            output.Device.Id,
                            output.Device.Name,
                            output.PositionSeconds,
                            output.ErrorMilliseconds,
                            output.ClockRatePpm,
                            output.Underruns
                        }).ToArray()
                    }, Request.RequestId);
                    nextMetrics += 1;
                }
                if (outputs.Count > 0)
                    WaitHandle.WaitAny(new WaitHandle[] { _stop.Token.WaitHandle }.Concat(outputs.Select(output => output.Ready)).ToArray(), 5);
            }
            if (completedOutputs == 0 && failedOutputs > 0 && !_stop.IsCancellationRequested)
                throw new InvalidOperationException("Every selected audio output failed during coordinated playback.");
            Telemetry.Info("AudioGroupCompleted", new
            {
                Cancelled = _stop.IsCancellationRequested,
                CompletedOutputs = completedOutputs,
                FailedOutputs = failedOutputs
            }, Request.RequestId);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { _prepared.TrySetCanceled(); }
        catch (Exception ex) { failure = ex; _prepared.TrySetException(ex); }
        finally
        {
            foreach (var output in outputs)
            {
                try
                {
                    output.Dispose();
                }
                catch (Exception ex) { Telemetry.Warning("AudioOutputCleanupFailed", new { output.Device.Id, Reason = ex.Message }, Request.RequestId); }
            }
            Volatile.Write(ref _activeOutputs, 0);
            _completed?.Invoke(this, failure);
        }
        void Remove(CoordinatedAudioOutput output, string reason, string detail)
        {
            failedOutputs++;
            outputs.Remove(output);
            Volatile.Write(ref _activeOutputs, outputs.Count);
            Telemetry.Warning(reason, new
            {
                output.Device.Id,
                output.Device.Name,
                Detail = detail
            }, Request.RequestId);
            try
            {
                output.Dispose();
            }
            catch (Exception ex) { Telemetry.Warning("AudioOutputCleanupFailed", new { output.Device.Id, Reason = ex.Message }, Request.RequestId); }
        }
    }
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stop.Cancel();
        if (!_worker.Wait(TimeSpan.FromSeconds(5)))
        {
            Telemetry.Warning("AudioGroupShutdownTimeout", new
            {
                TimeoutSeconds = 5
            }, Request.RequestId);
            // Worker owns resources; release handles only after it has exited.
            _ = _worker.ContinueWith(_ => { _stop.Dispose(); _start.Dispose(); }, TaskScheduler.Default);
            return;
        }
        _stop.Dispose();
        _start.Dispose();
    }
}
