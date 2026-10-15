using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using PhotoBooth.Diagnostics;
using Forms = System.Windows.Forms;

namespace ExperienceX;

internal sealed class DisplayManager(Dispatcher _dispatcher, PointCalibration _calibration) : IAsyncDisposable
{
    private readonly Dictionary<string, MainWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly VisualizationManager _visualizations = new();
    private readonly LedOutputManager _leds = new();
    internal LedOutputManager LedOutput => _leds;
    private void UpdateLeds() => _leds.Update(_windows.Values.SelectMany(w=>w.LedScreens));
    internal int[] VisualizationProcessIds => _visualizations.ProcessIds;
    internal Task ControlVisualizationAsync(ExperienceRequest request, ExperienceOptions options, CancellationToken token) => _visualizations.ExecuteAsync(request, options, token);
    private ExperienceOptions _options = new();
    private ScreenMediaCatalog _mediaCatalog = ScreenMediaCatalog.Empty;
    private bool _disposed;
    private string? _displayInventory, _aliasInventory, _defaultDisplayFailure;
    private ConfigurationSubmenu _submenu;
    internal event Action? CancelSubmenuRequested;
    internal void SetSubmenu(ConfigurationSubmenu menu)
    {
        _submenu = menu;
        foreach (var window in _windows.Values) window.SetSubmenu(menu);
    }
    public IEnumerable<MainWindow> Windows => _windows.Values;
    public event Action? CloseRequested;
    public event Action<bool>? TextEditingChanged;
    public event Action<ScreenSurface, string, string>? RenameRequested;
    public Func<ScreenSurface, ScreenOptions, string, Task<bool>>? SelectMediaRequested { get; set; }
    internal Func<string, ScreenOptions, VideoFramingOptions, CancellationToken, Task>? SaveFramingRequested { get; set; }

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
    public void UpdateMediaCatalog(ScreenMediaCatalog catalog)
    {
        _mediaCatalog = catalog;
        foreach (var window in _windows.Values) window.UpdateMediaCatalog(catalog);
    }
    public MainWindow? Select(string? requested)
    {
        Refresh();
        var display = OutputSelection.Display(requested, _options.DefaultDisplayName,
            Forms.Screen.AllScreens.Select(screen => screen.DeviceName).ToArray(), Forms.Screen.PrimaryScreen?.DeviceName, _options.DisplayAliases);
        return display is not null && _windows.TryGetValue(display, out var window) ? window : null;
    }
    public void Refresh()
    {
        if (_disposed)
            return;
        var screens = Forms.Screen.AllScreens;
        var inventory = string.Join("|", screens.Select(s => $"{s.DeviceName}:{s.Bounds}:{s.Primary}"));
        if (inventory != _displayInventory)
        {
            Telemetry.Info("DisplaysAvailable", new
            {
                Displays = screens.Select(s => new
                {
                    Name = s.DeviceName,
                    s.Primary,
                    s.Bounds.X,
                    s.Bounds.Y,
                    s.Bounds.Width,
                    s.Bounds.Height
                }).ToArray(),
                _options.DefaultDisplayName
            });
            _displayInventory = inventory;
        }
        foreach (var screen in screens)
        {
            if (_windows.ContainsKey(screen.DeviceName))
                continue;
            var window = new MainWindow(screen.DeviceName, _options);
            window.UpdateMediaCatalog(_mediaCatalog);
            window.AttachCalibration(_calibration);
            window.SetSubmenu(_submenu);
            window.CancelSubmenuRequested += () => CancelSubmenuRequested?.Invoke();
            window.LedGeometryChanged += UpdateLeds;
            window.TextEditingChanged += editing => TextEditingChanged?.Invoke(editing);
            window.RenameRequested += (surface, oldName, name) => RenameRequested?.Invoke(surface, oldName, name);
            window.SelectMediaRequested = (surface, screen, media) => SelectMediaRequested?.Invoke(surface, screen, media) ?? Task.FromResult(false);
            window.SaveFramingRequested = (screen, framing, token) => SaveFramingRequested?.Invoke(window.DisplayName, screen, framing, token) ??
                Task.FromException(new InvalidOperationException("Video framing persistence is unavailable."));
            _windows.Add(screen.DeviceName, window);
            window.FormClosing += (_, args) =>
            {
                if (_disposed)
                    return;
                args.Cancel = true;
                CloseRequested?.Invoke();
            };
            window.Start();
            Telemetry.Info("DisplayWindowCreated", new
            {
                DisplayName = screen.DeviceName
            });
        }
        // Retain disconnected windows invisibly, ready for the same display to return.
        foreach (var window in _windows.Values)
            window.RefreshDisplay();
        _visualizations.Refresh(_options, _windows.Values);
        UpdateLeds();
        var aliases = _options.DisplayAliases.Select(a => new
        {
            Alias = a.Key.Trim(),
            DisplayName = a.Value.Trim(),
            Available = screens.Any(s => string.Equals(s.DeviceName, a.Value.Trim(), StringComparison.OrdinalIgnoreCase))
        }).ToArray();
        var aliasInventory = JsonSerializer.Serialize(aliases);
        if (_aliasInventory != aliasInventory)
        {
            Telemetry.Info("DisplayAliasesAvailable", new
            {
                Aliases = aliases
            });
            foreach (var alias in aliases.Where(a => !a.Available))
                Telemetry.Warning("DisplayAliasUnavailable", alias);
            _aliasInventory = aliasInventory;
        }
        var defaultDisplay = OutputSelection.Display(null, _options.DefaultDisplayName,
            screens.Select(s => s.DeviceName).ToArray(), Forms.Screen.PrimaryScreen?.DeviceName, _options.DisplayAliases);
        if (defaultDisplay is null)
        {
            if (_defaultDisplayFailure != (_options.DefaultDisplayName ?? "<system-default>"))
                Telemetry.Warning("DefaultDisplayUnavailable", new
                {
                    _options.DefaultDisplayName,
                    RetrySeconds = 1
                });
            _defaultDisplayFailure = _options.DefaultDisplayName ?? "<system-default>";
        }
        else if (_defaultDisplayFailure is not null)
        {
            Telemetry.Info("DefaultDisplayRestored", new
            {
                DisplayName = defaultDisplay
            });
            _defaultDisplayFailure = null;
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
        await _leds.DisposeAsync();
        await _visualizations.DisposeAsync();
        await Task.WhenAll(_windows.Values.Select(window => window.ShutdownWindowAsync()));
        _windows.Clear();
    }
}
