using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using PhotoBooth.Diagnostics;
using Forms = System.Windows.Forms;

namespace ExperienceX;

internal sealed class MonitorManager(Dispatcher _dispatcher, KeystoneCalibration _calibration) : IAsyncDisposable
{
    private readonly Dictionary<string, MainWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ExperienceOptions _options = new();
    private bool _disposed;
    private string? _monitorInventory, _aliasInventory, _defaultMonitorFailure;
    public IEnumerable<MainWindow> Windows => _windows.Values;
    public event Action? CloseRequested;

    public void Start(ExperienceOptions options)
    {
        _options = options;
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }
    public void UpdateOptions(ExperienceOptions options)
    {
        _options = options;
        foreach (var window in _windows.Values)
            window.UpdateOptions(options);
        Refresh();
    }
    public MainWindow? Select(string? requested)
    {
        Refresh();
        var monitor = OutputSelection.Monitor(requested, _options.DefaultMonitorName,
            Forms.Screen.AllScreens.Select(screen => screen.DeviceName).ToArray(), Forms.Screen.PrimaryScreen?.DeviceName, _options.MonitorAliases);
        return monitor is not null && _windows.TryGetValue(monitor, out var window) ? window : null;
    }
    public void Refresh()
    {
        if (_disposed)
            return;
        var screens = Forms.Screen.AllScreens;
        var inventory = string.Join("|", screens.Select(s => $"{s.DeviceName}:{s.Bounds}:{s.Primary}"));
        if (inventory != _monitorInventory)
        {
            Telemetry.Info("MonitorsAvailable", new
            {
                Monitors = screens.Select(s => new
                {
                    Name = s.DeviceName,
                    s.Primary,
                    s.Bounds.X,
                    s.Bounds.Y,
                    s.Bounds.Width,
                    s.Bounds.Height
                }).ToArray(),
                _options.DefaultMonitorName
            });
            _monitorInventory = inventory;
        }
        foreach (var screen in screens)
        {
            if (_windows.ContainsKey(screen.DeviceName))
                continue;
            var window = new MainWindow(screen.DeviceName, _options);
            window.AttachCalibration(_calibration);
            _windows.Add(screen.DeviceName, window);
            window.FormClosing += (_, args) =>
            {
                if (_disposed)
                    return;
                args.Cancel = true;
                CloseRequested?.Invoke();
            };
            window.Start();
            Telemetry.Info("MonitorWindowCreated", new
            {
                MonitorName = screen.DeviceName
            });
        }
        // Retain disconnected windows invisibly, ready for the same monitor to return.
        foreach (var window in _windows.Values)
            window.RefreshMonitor();
        var aliases = _options.MonitorAliases.Select(a => new
        {
            Alias = a.Key.Trim(),
            MonitorName = a.Value.Trim(),
            Available = screens.Any(s => string.Equals(s.DeviceName, a.Value.Trim(), StringComparison.OrdinalIgnoreCase))
        }).ToArray();
        var aliasInventory = JsonSerializer.Serialize(aliases);
        if (_aliasInventory != aliasInventory)
        {
            Telemetry.Info("MonitorAliasesAvailable", new
            {
                Aliases = aliases
            });
            foreach (var alias in aliases.Where(a => !a.Available))
                Telemetry.Warning("MonitorAliasUnavailable", alias);
            _aliasInventory = aliasInventory;
        }
        var defaultMonitor = OutputSelection.Monitor(null, _options.DefaultMonitorName,
            screens.Select(s => s.DeviceName).ToArray(), Forms.Screen.PrimaryScreen?.DeviceName, _options.MonitorAliases);
        if (defaultMonitor is null)
        {
            if (_defaultMonitorFailure != (_options.DefaultMonitorName ?? "<system-default>"))
                Telemetry.Warning("DefaultMonitorUnavailable", new
                {
                    _options.DefaultMonitorName,
                    RetrySeconds = 1
                });
            _defaultMonitorFailure = _options.DefaultMonitorName ?? "<system-default>";
        }
        else if (_defaultMonitorFailure is not null)
        {
            Telemetry.Info("DefaultMonitorRestored", new
            {
                MonitorName = defaultMonitor
            });
            _defaultMonitorFailure = null;
        }
    }

    private void DisplaySettingsChanged(object? sender, EventArgs args)
    {
        if (!_disposed && !_dispatcher.HasShutdownStarted)
            _dispatcher.BeginInvoke(new Action(Refresh));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        await Task.WhenAll(_windows.Values.Select(window => window.ShutdownWindowAsync()));
        _windows.Clear();
    }
}
