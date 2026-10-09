using System.IO;
using System.Windows.Threading;
using PhotoBooth.Diagnostics;

namespace ExperienceX;

// Composition and routing only. Each service owns its events, timers, tasks, and cleanup.
internal sealed class ExperienceHost : IAsyncDisposable
{
    private readonly ConfigurationService _configuration;
    private readonly MonitorManager _monitors;
    private readonly AudioManager _audio;
    private readonly KeystoneCalibration _calibration;
    private readonly UdpExperienceListener _listener;
    private readonly ApplicationHotkeys _hotkeys;
    private readonly IdleMouseCursor _idleCursor;
    private readonly ConfigurationModeNotice _modeNotice = new();
    private readonly AsyncTaskScope _tasks = new("Experience host");
    private readonly DispatcherTimer _audioPoll = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _polling, _disposed, _togglingMode, _togglingBackground, _closeRequested;
    private string? _deviceFailure;
    private volatile object _health = new { State = "Starting" };
    private ExperienceOptions _options => _configuration.Current;
    public event Action? CloseRequested;

    public ExperienceHost(Dispatcher dispatcher, string path)
    {
        _configuration = new(dispatcher, path);
        _idleCursor = new(dispatcher);
        _audio = new(dispatcher);
        _calibration = new(() => _options, () => _monitors!.Windows, _configuration.Store, _configuration.ReloadAsync);
        _monitors = new(dispatcher, _calibration);
        _monitors.CloseRequested += () => CloseRequested?.Invoke();
        _listener = new(dispatcher, () => _options, HandleRequest);
        _hotkeys = new(() => _tasks.Run(ToggleConfigurationModeAsync, "Toggle configuration mode"), () =>
        {
            if (_disposed || _closeRequested)
                return;
            _closeRequested = true;
            Telemetry.Info("CloseHotkeyPressed", new
            {
                Shortcut = "X"
            });
            // Leave the native message handler before disposing its receiving window.
            dispatcher.BeginInvoke(new Action(() => { if (!_disposed) CloseRequested?.Invoke(); }));
        }, () =>
        {
            var monitor = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Control.MousePosition).DeviceName;
            _tasks.Run(token => ToggleBackgroundAsync(monitor, token), "Toggle monitor background");
        });
        _configuration.Changing += next => { if (next.UdpPort != _options.UdpPort) _listener.Bind(next.UdpPort); };
        _configuration.Changed += ApplyConfiguration;
        _audioPoll.Tick += (_, _) => _tasks.Run(PollAudioAsync, "Audio inventory");
        Telemetry.SetHealthProvider(() => _health);
    }
    public void Start()
    {
        // Reject an unrecoverable configuration before opening any output or network listener.
        _configuration.LoadInitial();
        _audio.UpdateOptions(_options);
        _listener.Bind(_options.UdpPort);
        _monitors.Start(_options);
        _configuration.StartWatching();
        UpdateHotkeys(_options.ConfigurationMode);
        _idleCursor.Start();
        _audioPoll.Start();
        _tasks.Run(PollAudioAsync, "Initial audio inventory");
    }
    private void UpdateHotkeys(bool configurationMode)
    {
        var unavailable = _hotkeys.Register(configurationMode);
        if (unavailable.Length > 0)
            _modeNotice.Display($"{string.Join(", ", unavailable)} unavailable — check the log", true);
    }
    private async Task ToggleConfigurationModeAsync(CancellationToken token)
    {
        if (_disposed || _togglingMode)
            return;
        _togglingMode = true;
        var shortcut = _options.ConfigurationMode ? "C" : "Ctrl+Shift+Alt+C";
        try
        {
            var enabled = await _configuration.ToggleConfigurationModeAsync(token);
            if (_disposed)
                return;
            Telemetry.Info("ConfigurationModeToggled", new
            {
                Shortcut = shortcut,
                ConfigurationMode = enabled
            });
            _modeNotice.Display(enabled ? "Configuration mode ON   •   C: exit   X: close   T: background" : "Configuration mode OFF   •   Ctrl+Shift+Alt+C: configure");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) when (_disposed) { }
        catch (Exception ex)
        {
            Telemetry.Error("ConfigurationModeToggleFailed", ex);
            if (!_disposed)
                _modeNotice.Display("Configuration mode could not be changed", true);
        }
        finally { _togglingMode = false; }
    }
    private async Task ToggleBackgroundAsync(string monitor, CancellationToken token)
    {
        if (_disposed || _togglingBackground)
            return;
        _togglingBackground = true;
        try
        {
            var transparent = await _configuration.ToggleBackgroundTransparencyAsync(monitor, token);
            if (_disposed)
                return;
            Telemetry.Info("BackgroundTransparencyToggled", new
            {
                Shortcut = "T",
                MonitorName = monitor,
                IsTransparent = transparent
            });
            _modeNotice.Display($"{monitor}: {(transparent ? "transparent" : "opaque")}   •   T");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) when (_disposed) { }
        catch (Exception ex)
        {
            Telemetry.Error("BackgroundTransparencyToggleFailed", ex, new
            {
                MonitorName = monitor
            });
            _modeNotice.Display("Background could not be changed", true);
        }
        finally { _togglingBackground = false; }
    }
    private void ApplyConfiguration(ExperienceOptions previous, ExperienceOptions next)
    {
        if (previous.ConfigurationMode != next.ConfigurationMode)
            UpdateHotkeys(next.ConfigurationMode);
        _calibration.ConfigurationChanged(next);
        _monitors.UpdateOptions(next);
        _tasks.Run(_ => _calibration.ConfigurationAppliedAsync(previous, next), "Confirm remote calibration");
        _audio.UpdateOptions(next);
    }
    private async Task PollAudioAsync(CancellationToken token)
    {
        if (_polling || _disposed)
            return;
        _polling = true;
        try
        {
            var inventory = await Task.Run(AudioManager.ReadInventory, token);
            if (_disposed || token.IsCancellationRequested)
                return;
            _audio.CheckDevices(inventory);
            _deviceFailure = null;
            _health = new
            {
                MonitorWindows = _monitors.Windows.Select(window => new { window.MonitorName, State = window.State.ToString() }).ToArray(),
                ConnectedMonitors = System.Windows.Forms.Screen.AllScreens.Length,
                AudioPlaybacks = _audio.Count
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_deviceFailure != ex.Message)
                Telemetry.Error("AudioInventoryFailed", ex);
            _deviceFailure = ex.Message;
        }
        finally { _polling = false; }
    }
    private void HandleRequest(ExperienceRequest request)
    {
        if (_disposed)
            return;
        var video = string.Equals(request.Experience, "video", StringComparison.OrdinalIgnoreCase);
        try
        {
            var path = MediaPath.Resolve(video ? _options.VideoDirectory : _options.AudioDirectory, request.Value, AppContext.BaseDirectory);
            if (video)
            {
                var window = _monitors.Select(request.MonitorName);
                if (window is null)
                {
                    Telemetry.Warning("ExperienceBlockedMonitorUnavailable", new
                    {
                        request.MonitorName,
                        _options.DefaultMonitorName
                    }, request.RequestId);
                    return;
                }
                Telemetry.Info("VideoOutputSelected", new
                {
                    RequestedMonitorName = request.MonitorName,
                    _options.DefaultMonitorName,
                    MonitorName = window.MonitorName
                }, request.RequestId);
                window.PlayVideo(path, request);
            }
            else
                _tasks.Run(token => _audio.PlayAsync(path, request, _options, token), "Prepare audio");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            Telemetry.Warning("MediaRequestRejected", new
            {
                request.Experience,
                request.Value,
                Reason = ex.Message
            }, request.RequestId);
        }
    }


    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _idleCursor.Dispose();
        _tasks.Cancel();
        _hotkeys.Dispose();
        _modeNotice.Dispose();
        _audioPoll.Stop();
        _calibration.Cancel("Shutdown");
        // Quiesce producers before disposing outputs; keep the dispatcher pumping while awaiting.
        await _listener.DisposeAsync();
        await _configuration.DisposeAsync();
        await _calibration.DisposeAsync();
        await _tasks.DisposeAsync();
        _audio.Dispose();
        await _monitors.DisposeAsync();
    }
}
