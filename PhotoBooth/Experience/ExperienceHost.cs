using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using PhotoBooth.Diagnostics;
using Forms = System.Windows.Forms;

namespace Experience;

internal sealed class ExperienceHost : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly AudioManager _audio;
    private readonly string _configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    private readonly ConfigurationFileReader _configurationReader = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, MainWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private ExperienceOptions _options = new();
    private string? _loadedConfiguration;
    private UdpClient? _listener;
    private CancellationTokenSource? _cancellation;
    private bool _polling, _disposed;
    private string? _monitorInventory;
    private string? _aliasInventory;
    private string? _configurationFailure;
    private string? _deviceFailure;
    private string? _defaultMonitorFailure;
    private volatile object _health = new { State = "Starting" };

    internal ExperienceHost(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _audio = new AudioManager(dispatcher);
        Telemetry.SetHealthProvider(() => _health);
    }

    internal void Start()
    {
        // Read initial monitor/development settings before creating any native window.
        try
        {
            var json = _configurationReader.Read(_configPath);
            _options = ExperienceOptions.Parse(json);
            _loadedConfiguration = json;
            LogConfiguration();
        }
        catch (Exception ex) { Telemetry.Error("StartupConfigurationRejected", ex, new { Path = _configPath }); }
        _audio.UpdateOptions(_options);
        Bind(_options.UdpPort);
        RefreshMonitors();
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();
    }

    private void RefreshMonitors()
    {
        if (_disposed) return;
        var screens = Forms.Screen.AllScreens;
        var inventory = string.Join("|", screens.Select(s => $"{s.DeviceName}:{s.Bounds}:{s.Primary}"));
        if (inventory != _monitorInventory)
        {
            Telemetry.Info("MonitorsAvailable", new { Monitors = screens.Select(s => new { Name = s.DeviceName, s.Primary,
                s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height }).ToArray(), _options.DefaultMonitorName });
            _monitorInventory = inventory;
        }
        foreach (var screen in screens)
        {
            if (_windows.ContainsKey(screen.DeviceName)) continue;
            var window = new MainWindow(screen.DeviceName, _options);
            _windows.Add(screen.DeviceName, window);
            window.Closed += (_, _) => { if (!_disposed) System.Windows.Application.Current.Shutdown(); };
            window.Start();
            Telemetry.Info("MonitorWindowCreated", new { MonitorName = screen.DeviceName });
        }
        // Retain disconnected windows invisibly, ready for the same monitor to return.
        foreach (var window in _windows.Values) window.RefreshMonitor();
        var aliases = _options.MonitorAliases.Select(a => new { Alias = a.Key.Trim(), MonitorName = a.Value.Trim(),
            Available = screens.Any(s => string.Equals(s.DeviceName, a.Value.Trim(), StringComparison.OrdinalIgnoreCase)) }).ToArray();
        var aliasInventory = JsonSerializer.Serialize(aliases);
        if (_aliasInventory != aliasInventory)
        {
            Telemetry.Info("MonitorAliasesAvailable", new { Aliases = aliases });
            foreach (var alias in aliases.Where(a => !a.Available))
                Telemetry.Warning("MonitorAliasUnavailable", alias);
            _aliasInventory = aliasInventory;
        }
        var defaultMonitor = OutputSelection.Monitor(null, _options.DefaultMonitorName,
            screens.Select(s => s.DeviceName).ToArray(), Forms.Screen.PrimaryScreen?.DeviceName, _options.MonitorAliases);
        if (defaultMonitor is null)
        {
            if (_defaultMonitorFailure != (_options.DefaultMonitorName ?? "<system-default>"))
                Telemetry.Warning("DefaultMonitorUnavailable", new { _options.DefaultMonitorName, RetrySeconds = 1 });
            _defaultMonitorFailure = _options.DefaultMonitorName ?? "<system-default>";
        }
        else if (_defaultMonitorFailure is not null)
        {
            Telemetry.Info("DefaultMonitorRestored", new { MonitorName = defaultMonitor });
            _defaultMonitorFailure = null;
        }
        _health = new { MonitorWindows = _windows.Values.Select(w => new { w.MonitorName, w.State }).ToArray(),
            ConnectedMonitors = screens.Length, AudioPlaybacks = _audio.Count };
    }

    private void DisplaySettingsChanged(object? sender, EventArgs args)
    {
        if (!_disposed && !_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(new Action(RefreshMonitors));
    }

    private async Task PollAsync()
    {
        RefreshMonitors(); // Always perform the one-second monitor check, even during a pending read.
        if (_polling || _disposed) return;
        _polling = true;
        try
        {
            try
            {
                var json = await Task.Run(() => _configurationReader.Read(_configPath));
                if (_disposed) return;
                if (json != _loadedConfiguration)
                {
                    var next = await Task.Run(() => ExperienceOptions.Parse(json));
                    if (_disposed) return;
                    if (next.DeveloperMode != _options.DeveloperMode)
                        Telemetry.Warning("DeveloperModeRestartRequired");
                    if (next.UdpPort != _options.UdpPort) Bind(next.UdpPort);
                    // Native chrome remains the startup mode for existing and newly connected windows.
                    var startupMode = _options.DeveloperMode;
                    _options = next with { DeveloperMode = startupMode };
                    _loadedConfiguration = json;
                    foreach (var window in _windows.Values) window.UpdateOptions(_options);
                    _audio.UpdateOptions(_options);
                    LogConfiguration();
                }
                _configurationFailure = null;
            }
            catch (Exception ex)
            {
                if (_configurationFailure != ex.Message) Telemetry.Error("ConfigurationRejected", ex, new { Path = _configPath });
                _configurationFailure = ex.Message;
            }
            try
            {
                var inventory = await Task.Run(AudioManager.ReadInventory);
                if (_disposed) return;
                _audio.CheckDevices(inventory);
                _deviceFailure = null;
            }
            catch (Exception ex)
            {
                if (_deviceFailure != ex.Message) Telemetry.Error("AudioInventoryFailed", ex);
                _deviceFailure = ex.Message;
            }
        }
        finally { _polling = false; }
    }

    private void LogConfiguration() => Telemetry.Info("ConfigurationLoaded", new { Path = _configPath,
        _options.DefaultMonitorName, _options.MonitorAliases, _options.DefaultAudioDevice, _options.DeveloperMode, _options.UdpPort,
        _options.MaxVideoPlaybackLength, _options.MaxAudioPlaybackLength, _options.TransitionInSeconds,
        _options.TransitionOutSeconds, _options.AudioTransitionInSeconds, _options.AudioTransitionOutSeconds,
        _options.VideoDirectory, _options.AudioDirectory });

    private void Bind(int port)
    {
        var replacement = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        _cancellation?.Cancel();
        _listener?.Dispose();
        _cancellation?.Dispose();
        _listener = replacement;
        _cancellation = new();
        _ = ListenAsync(replacement, _cancellation.Token);
        Telemetry.Info("UdpListening", new { Address = "0.0.0.0", Port = port });
    }

    private async Task ListenAsync(UdpClient listener, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                var packet = await listener.ReceiveAsync(cancellation).ConfigureAwait(false);
                ExperienceRequest request;
                try { request = ExperienceRequest.Parse(packet.Buffer); }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                {
                    Telemetry.Warning("UdpRequestInvalid", new { Sender = packet.RemoteEndPoint.ToString(),
                        Bytes = packet.Buffer.Length, Reason = ex.Message });
                    continue;
                }
                Telemetry.Info("UdpRequestReceived", new { Sender = packet.RemoteEndPoint.ToString(),
                    request.Experience, request.Value, request.MonitorName, request.AudioDevice, request.Channel,
                    request.Mute, request.Source }, request.RequestId);
                await _dispatcher.InvokeAsync(() => HandleRequest(request));
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Telemetry.Error("UdpReceiveFailed", ex);
                await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private void HandleRequest(ExperienceRequest request)
    {
        if (_disposed) return;
        var video = string.Equals(request.Experience, "video", StringComparison.OrdinalIgnoreCase);
        try
        {
            var path = MediaPath.Resolve(video ? _options.VideoDirectory : _options.AudioDirectory, request.Value, AppContext.BaseDirectory);
            if (video)
            {
                RefreshMonitors();
                var screens = Forms.Screen.AllScreens;
                var monitor = OutputSelection.Monitor(request.MonitorName, _options.DefaultMonitorName,
                    screens.Select(s => s.DeviceName).ToArray(), Forms.Screen.PrimaryScreen?.DeviceName, _options.MonitorAliases);
                if (monitor is null || !_windows.TryGetValue(monitor, out var window))
                {
                    Telemetry.Warning("ExperienceBlockedMonitorUnavailable", new { request.MonitorName,
                        _options.DefaultMonitorName }, request.RequestId);
                    return;
                }
                Telemetry.Info("VideoOutputSelected", new { RequestedMonitorName = request.MonitorName,
                    _options.DefaultMonitorName, MonitorName = monitor }, request.RequestId);
                window.PlayVideo(path, request);
            }
            else _ = _audio.PlayAsync(path, request, _options);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            Telemetry.Warning("MediaRequestRejected", new { request.Experience, request.Value, Reason = ex.Message }, request.RequestId);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        _cancellation?.Cancel();
        _listener?.Dispose();
        _cancellation?.Dispose();
        _audio.Dispose();
        foreach (var window in _windows.Values.ToArray()) window.ShutdownWindow();
        _windows.Clear();
    }
}
