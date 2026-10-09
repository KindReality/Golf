using System.IO;
using System.Windows.Threading;
using PhotoBooth.Diagnostics;
using Forms = System.Windows.Forms;

namespace ExperienceX;

// All state and notifications belong to the dispatcher. Only file I/O runs in the background.
internal sealed class ConfigurationService : IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly string _configPath;
    private readonly KeystoneConfigurationStore _store;
    private readonly Func<IReadOnlyList<string>> _monitors;
    private readonly AsyncTaskScope _tasks = new("Configuration");
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _reloadDebounce = new() { Interval = TimeSpan.FromMilliseconds(75) };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private Task? _reloadTask;
    private bool _reloadAgain, _disposed;
    private volatile ExperienceOptions _options = new();
    private string? _loadedConfiguration, _configurationFailure;
    public ExperienceOptions Current => _options;
    public KeystoneConfigurationStore Store => _store;
    public event Action<ExperienceOptions>? Changing;
    public event Action<ExperienceOptions, ExperienceOptions>? Changed;

    public ConfigurationService(Dispatcher dispatcher, string path, Func<IReadOnlyList<string>>? monitors = null)
    {
        _dispatcher = dispatcher;
        _configPath = path;
        _store = new(path);
        _monitors = monitors ?? (() => Forms.Screen.AllScreens.Select(screen => screen.DeviceName).ToArray());
        _reloadDebounce.Tick += (_, _) => { _reloadDebounce.Stop(); _tasks.Run(_ => ReloadAsync(), "Debounced reload"); };
        _poll.Tick += (_, _) => _tasks.Run(_ => ReloadAsync(), "Polling reload");
    }
    public void LoadInitial()
    {
        if (_store.RecoverAtStartup())
            Telemetry.Warning("ConfigurationRecoveredFromBackup", new
            {
                Path = _configPath
            });
        var json = _store.EnsureDisplays(_monitors());
        _options = ExperienceOptions.Parse(json);
        _loadedConfiguration = json;
        LogConfiguration();
    }
    public void StartWatching()
    {
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(_configPath)!, Path.GetFileName(_configPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };
        _watcher.Changed += ConfigurationFileChanged;
        _watcher.Created += ConfigurationFileChanged;
        _watcher.Deleted += ConfigurationFileChanged;
        _watcher.Renamed += ConfigurationFileChanged;
        _watcher.Error += (_, args) => { Telemetry.Warning("ConfigurationWatcherFailed", new { Reason = args.GetException().Message, FallbackSeconds = 1 }); QueueConfigurationReload(); };
        _watcher.EnableRaisingEvents = true;
        _poll.Start();
    }
    private void ConfigurationFileChanged(object sender, FileSystemEventArgs args) => QueueConfigurationReload();
    private void QueueConfigurationReload()
    {
        if (_disposed || _dispatcher.HasShutdownStarted)
            return;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed)
                return;
            _reloadDebounce.Stop();
            _reloadDebounce.Start();
        }));
    }
    public Task ReloadAsync()
    {
        _reloadAgain = true;
        return _reloadTask is { IsCompleted: false } ? _reloadTask : _reloadTask = ReloadLoopAsync();
    }
    public async Task<bool> ToggleConfigurationModeAsync(CancellationToken cancellation = default)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var json = await _store.ToggleConfigurationModeAsync(cancellation);
        cancellation.ThrowIfCancellationRequested();
        await ReloadAsync();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_options.ConfigurationMode != ExperienceOptions.Parse(json).ConfigurationMode)
            throw new InvalidOperationException("Configuration mode could not be applied or was changed again remotely.");
        return _options.ConfigurationMode;
    }
    public async Task<bool> ToggleBackgroundTransparencyAsync(string monitor, CancellationToken cancellation = default)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var json = await _store.ToggleBackgroundTransparencyAsync(monitor, cancellation);
        cancellation.ThrowIfCancellationRequested();
        await ReloadAsync();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var expectedOptions = ExperienceOptions.Parse(json);
        var key = MonitorBackground.KeyFor(monitor, expectedOptions)!;
        var expected = expectedOptions.MonitorBackgrounds[key];
        var appliedKey = MonitorBackground.KeyFor(monitor, _options);
        if (appliedKey is null || _options.MonitorBackgrounds[appliedKey] != expected)
            throw new InvalidOperationException("Background transparency could not be applied or was changed again remotely.");
        return expected.IsTransparent;
    }
    private async Task ReloadLoopAsync()
    {
        while (_reloadAgain && !_disposed)
        {
            _reloadAgain = false;
            try
            {
                var monitors = _monitors();
                var json = await Task.Run(() => _store.EnsureDisplays(monitors));
                if (_disposed)
                    return;
                if (json != _loadedConfiguration)
                {
                    var next = ExperienceOptions.Parse(json);
                    if (next.DeveloperMode != _options.DeveloperMode)
                        Telemetry.Warning("DeveloperModeRestartRequired");
                    Changing?.Invoke(next);
                    var previous = _options;
                    _options = next with
                    {
                        DeveloperMode = _options.DeveloperMode
                    };
                    _loadedConfiguration = json;
                    Changed?.Invoke(previous, _options);
                    LogConfiguration();
                }
                _configurationFailure = null;
            }
            catch (Exception ex)
            {
                if (_configurationFailure != ex.Message)
                    Telemetry.Error("ConfigurationRejected", ex, new
                    {
                        Path = _configPath
                    });
                _configurationFailure = ex.Message;
            }
        }
    }

    private void LogConfiguration() => Telemetry.Info("ConfigurationLoaded", new
    {
        Path = _configPath,
        _options.ConfigurationVersion,
        _options.DefaultMonitorName,
        _options.MonitorAliases,
        _options.MonitorBackgrounds,
        _options.DefaultAudioDevice,
        _options.DeveloperMode,
        _options.UdpPort,
        _options.DeviceName,
        _options.EffectiveDeviceName,
        _options.UdpBroadcastAddress,
        _options.UdpInterfaceAddress,
        _options.RequestLifetimeSeconds,
        _options.MaxVideoPlaybackLength,
        _options.MaxAudioPlaybackLength,
        _options.TransitionInSeconds,
        _options.TransitionOutSeconds,
        _options.AudioTransitionInSeconds,
        _options.AudioTransitionOutSeconds,
        _options.VideoDirectory,
        _options.AudioDirectory,
        _options.ConfigurationMode,
        _options.KeystoneHitDiameterPercent,
        _options.DisplayKeystones,
        _options.CalibrationGridEnabled,
        _options.RequestDeduplicationSeconds
    });


    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _watcher?.Dispose();
        _reloadDebounce.Stop();
        _poll.Stop();
        await _tasks.DisposeAsync();
    }
}
