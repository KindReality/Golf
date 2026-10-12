using System.IO;
using System.Windows.Threading;
using PhotoBooth.Diagnostics;

namespace ExperienceX;

// Composition and routing only. Each service owns its events, timers, tasks, and cleanup.
internal sealed class ExperienceHost : IAsyncDisposable
{
    private readonly ConfigurationService _configuration;
    private readonly DisplayManager _displays;
    private readonly AudioManager _audio;
    private readonly PointCalibration _calibration;
    private readonly UdpExperienceListener _listener;
    private readonly ControllerMediaDiscovery _mediaDiscovery;
    private readonly ControllerDefinitionsClient _definitions;
    private readonly ScreenMediaMonitor _screenMedia;
    private readonly ApplicationHotkeys _hotkeys;
    private readonly PhotoBooth.Windows.ConfigurationShortcut _configurationShortcut;
    private readonly IdleMouseCursor _idleCursor;
    private readonly ConfigurationModeNotice _modeNotice = new();
    private readonly BackgroundSelection _backgroundSelection = new();
    private readonly VideoStopHistory _videoStops = new();
    private readonly AsyncTaskScope _tasks = new("Experience host");
    private readonly DispatcherTimer _audioPoll = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _polling, _disposed, _togglingMode, _togglingBackground, _togglingTransparency, _closeRequested;
    private string? _deviceFailure;
    private bool _addScreenArmed, _editingText, _addingScreen;
    private bool _togglingSizing;
    private readonly Queue<(string Display, string ScreenId, int Direction)> _brightnessChanges = new();
    private bool _adjustingBrightness;
    private async Task CreateScreenAsync(CancellationToken token, string type)
    {
        if (_disposed || !_options.ConfigurationMode || _addingScreen)
            return;
        _addingScreen = true;
        try
        {
            var selected = _calibration.SelectedScreen;
            var window = _displays.Select(selected?.DisplayName) ?? throw new InvalidOperationException("The default display is unavailable.");
            PhotoBooth.Networking.PerspectiveSelection? perspective = null;
            if (type == "visualization" && _options.DisplayScreens.Values.SelectMany(s => s).Any(s => s.IsVisualization))
            {
                _modeNotice.Display("Only one visualization is supported. Delete the existing one first.", display: window.DisplayName);
                return;
            }
            if (type == "video")
            {
                using var picker = new PerspectivePicker(_definitions.Perspectives);
                System.Windows.Forms.DialogResult result;
                _editingText=true; UpdateHotkeys(true);
                try { result=picker.ShowDialog(window); }
                finally { _editingText=false; UpdateHotkeys(_options.ConfigurationMode); }
                if (result != System.Windows.Forms.DialogResult.OK) return;
                perspective = picker.Selection;
            }
            var created = await _configuration.Store.AddScreenAsync(window.DisplayName, token, type, perspective);
            await _configuration.ReloadAsync();
            if (_disposed || !_options.ConfigurationMode)
                return;
            _calibration.SelectScreen(window.Surface(created.Id));
            window.Surface(created.Id).FlashCalibrationPoints(15);
            Telemetry.Info("ScreenCreated", new
            {
                window.DisplayName,
                ScreenName = created.Name,
                ScreenType = created.Type,
                ScreenId = created.Id
            });
            _modeNotice.Display($"Created {created.Name}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Telemetry.Error("ScreenCreationFailed", ex); if (!_disposed) _modeNotice.Display("Screen could not be created — check the log", true); }
        finally { _addingScreen = false; }
    }
    private async Task ChoosePerspectiveAsync(CancellationToken token)
    {
        if (_calibration.SelectedScreen is not ScreenSurface surface || !_options.ConfigurationMode) return;
        var window = _displays.Windows.FirstOrDefault(w => w.DisplayName == surface.DisplayName);
        var screen = _options.ScreensFor(surface.DisplayName).FirstOrDefault(s => s.Id == surface.ScreenId);
        if (window is null || screen is null || !screen.IsVideo) return;
        using var picker = new PerspectivePicker(_definitions.Perspectives, screen.Perspective);
        System.Windows.Forms.DialogResult result;
                _editingText=true; UpdateHotkeys(true);
                try { result=picker.ShowDialog(window); }
                finally { _editingText=false; UpdateHotkeys(_options.ConfigurationMode); }
                if (result != System.Windows.Forms.DialogResult.OK) return;
        try { await _configuration.Store.SetPerspectiveAsync(surface.DisplayName, surface.ScreenId, picker.Selection); await _configuration.ReloadAsync(); }
        catch (Exception ex) { _modeNotice.Display(ex.Message, true); Telemetry.Error("PerspectiveSelectionFailed", ex); }
    }
    private async Task<bool> RenameScreenAsync(ScreenSurface surface, string oldName, string name, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            await _configuration.Store.RenameScreenAsync(surface.DisplayName, surface.ScreenId, oldName, name);
            await _configuration.ReloadAsync();
            if (_disposed)
                return false;
            Telemetry.Info("ScreenRenamed", new
            {
                surface.DisplayName,
                surface.ScreenId,
                OldName = oldName,
                Name = name
            });
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Telemetry.Error("ScreenRenameFailed", ex); if (!_disposed) _modeNotice.Display(ex.Message, true); }
        return false;
    }
    private volatile object _health = new { State = "Starting" };
    private ExperienceOptions _options => _configuration.Current;
    public event Action? CloseRequested;
    internal nint HotkeyWindowHandle => _hotkeys.Handle;
    internal int[] VisualizationProcessIds => _displays.VisualizationProcessIds;
    internal IEnumerable<MainWindow> DisplayWindows => _displays.Windows;
    internal IPointSurface? SelectedScreen => _calibration.SelectedScreen;

    public ExperienceHost(Dispatcher dispatcher, string path)
    {
        _configuration = new(dispatcher, path);
        _idleCursor = new(dispatcher);
        _audio = new(dispatcher);
        _calibration = new(() => _options, () => _displays!.Windows.SelectMany(window => window.Surfaces), _configuration.Store, _configuration.ReloadAsync);
        _displays = new(dispatcher, _calibration);
        _screenMedia = new(dispatcher, _displays.UpdateMediaCatalog);
        _displays.CloseRequested += () => CloseRequested?.Invoke();
        _mediaDiscovery = new(() => _options);
        _definitions = new(dispatcher, async definitions => { await _configuration.Store.ApplyPerspectivesAsync(definitions); await _configuration.ReloadAsync(); });
        _listener = new(dispatcher, () => _options, HandleRequest, message => { _mediaDiscovery.Receive(message); _definitions.Receive(message); });
        _configurationShortcut = new("Experience", () => _tasks.Run(EnableConfigurationAsync, "Enable shared configuration"));
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
            PressBackgroundB();
        });
        _configuration.Changing += next => { if (next.UdpCommandsPort != _options.UdpCommandsPort || next.UdpEventsPort != _options.UdpEventsPort) _listener.Bind(next.UdpCommandsPort, next.UdpEventsPort); };
        _configuration.Changed += ApplyConfiguration;
        _hotkeys.AddScreenPrefix = () => { CancelBackgroundChoice(); _addScreenArmed = true; _modeNotice.Display("Add screen   •   V: video   M: mat   G: visualization"); };
        _hotkeys.EditScreenName = () =>
        {
            CancelBackgroundChoice(); _addScreenArmed = false;
            if (_calibration.SelectedScreen is ScreenSurface selected)
                _displays.Windows.FirstOrDefault(window => window.DisplayName == selected.DisplayName)?.EditScreenName(selected);
            else _modeNotice.Display("Select a screen first   •   N: edit name");
        };
        _hotkeys.ToggleScreenTransparency = () => { CancelBackgroundChoice(); _tasks.Run(ToggleScreenTransparencyAsync, "Toggle screen chroma key"); };
        _hotkeys.ToggleScreenSizing = () => { CancelBackgroundChoice(); _addScreenArmed = false; _tasks.Run(ToggleScreenSizingAsync, "Toggle screen Fit/Stretch"); };
        _hotkeys.AdjustScreenBrightness = PressScreenBrightness;
        _hotkeys.ChooseWhiteBackground = () => ApplyBackgroundChoice(_backgroundSelection.PressW());
        _hotkeys.AddVideoScreen = () => { if (_addScreenArmed) { _addScreenArmed = false; _tasks.Run(token => CreateScreenAsync(token, "video"), "Create video screen"); } };
        _hotkeys.ChoosePerspective = () => _tasks.Run(ChoosePerspectiveAsync, "Choose screen perspective");
        _hotkeys.AddVisualizationScreen = () => { if (_addScreenArmed) { _addScreenArmed = false; _tasks.Run(token => CreateScreenAsync(token, "visualization"), "Create visualization screen"); } };
        _hotkeys.AddMatScreen = () => { if (_addScreenArmed) { _addScreenArmed = false; _tasks.Run(token => CreateScreenAsync(token, "mat"), "Create mat screen"); } };
        _displays.TextEditingChanged += editing => { if (_disposed) return; _editingText = editing; _addScreenArmed = false; CancelBackgroundChoice(); UpdateHotkeys(_options.ConfigurationMode); };
        _displays.RenameRequested += (surface, oldName, name) => _tasks.Run(token => RenameScreenAsync(surface, oldName, name, token), "Rename screen");
        _displays.BrowseRenameRequested = (surface, oldName, name) => RenameScreenAsync(surface, oldName, name, _tasks.Token);
        _audioPoll.Tick += (_, _) => _tasks.Run(PollAudioAsync, "Audio inventory");
        Telemetry.SetHealthProvider(() => _health);
    }
    public void Start()
    {
        // Reject an unrecoverable configuration before opening any output or network listener.
        _configuration.LoadInitial();
        _definitions.Seed(_options.ControllerDefinitions.Values);
        _screenMedia.Start(_options.VideoDirectory);
        _audio.UpdateOptions(_options);
        _listener.Bind(_options.UdpCommandsPort, _options.UdpEventsPort);
        _mediaDiscovery.Configure(_options);
        _displays.Start(_options);
        _configuration.StartWatching();
        UpdateHotkeys(_options.ConfigurationMode);
        _idleCursor.Start();
        _audioPoll.Start();
        _tasks.Run(PollAudioAsync, "Initial audio inventory");
    }
    private async Task EnableConfigurationAsync(CancellationToken token)
    {
        await _configuration.Store.SetConfigurationModeAsync(true, token);
        await _configuration.ReloadAsync();
        Telemetry.Info("SharedConfigurationEnabled",new{ControllerRunning=PhotoBooth.Windows.ConfigurationShortcut.ControllerRunning,ForegroundProcessId=PhotoBooth.Windows.ConfigurationShortcut.ForegroundProcessId});
        if (!PhotoBooth.Windows.ConfigurationShortcut.ControllerRunning)
        {
            var window = _displays.Select(null);
            window?.Activate(); window?.Focus();
        }
        _modeNotice.Display("Configuration ON • E: exit • A: add • N: name • R: reset • Z: undo");
    }
    private void UpdateHotkeys(bool configurationMode)
    {
        var unavailable = _hotkeys.Register(configurationMode, _editingText, _backgroundSelection.PendingDisplay is not null);
        if (unavailable.Length > 0)
            _modeNotice.Display($"{string.Join(", ", unavailable)} unavailable — check the log", true);
    }
    private async Task ToggleConfigurationModeAsync(CancellationToken token)
    {
        if (_disposed || _togglingMode)
            return;
        _togglingMode = true;
        var shortcut = _options.ConfigurationMode ? "E" : "Ctrl+Shift+Alt+C";
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
            _modeNotice.Display(enabled ? "Configuration mode ON   •   A: add screen   V: video   M: mat   G: visualization   N: name/media   E: exit   X: close   S: Fit/Stretch   +/-: brightness   T: screen chroma key   B: display background" : "Configuration mode OFF   •   Ctrl+Shift+Alt+C: configure");
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
    private void CancelBackgroundChoice()
    {
        if (_backgroundSelection.PendingDisplay is null) return;
        _backgroundSelection.Cancel();
        _modeNotice.Hide();
        UpdateHotkeys(_options.ConfigurationMode);
    }
    private void PressBackgroundB()
    {
        if (_disposed || !_options.ConfigurationMode || _togglingBackground) return;
        _addScreenArmed = false;
        var display = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Control.MousePosition).DeviceName;
        if (_options.ScreensFor(display).Any(s => s.IsVisualization))
        {
            CancelBackgroundChoice();
            _modeNotice.Display("Visualization requires a transparent display background", display: display);
            return;
        }
        var change = _backgroundSelection.PressB(display, DisplayBackground.For(display, _options).A == 0);
        if (change is not null) { ApplyBackgroundChoice(change); return; }
        UpdateHotkeys(true);
        var target = _backgroundSelection.PendingDisplay!;
        Telemetry.Info("BackgroundColorChoiceOpened", new { DisplayName = target });
        _modeNotice.Display($"{target}: B: black   W: white", persistent: true, display: target);
    }
    private void ApplyBackgroundChoice(BackgroundChange? change)
    {
        if (_disposed || !_options.ConfigurationMode || _togglingBackground || change is null) return;
        UpdateHotkeys(true);
        _tasks.Run(token => SetBackgroundAsync(change, token), "Set display background");
    }
    private async Task SetBackgroundAsync(BackgroundChange change, CancellationToken token)
    {
        if (_disposed || _togglingBackground)
            return;
        _togglingBackground = true;
        try
        {
            var transparent = await _configuration.SetBackgroundAsync(change.Display, change.IsTransparent, change.Color, token);
            if (_disposed)
                return;
            Telemetry.Info("BackgroundTransparencyToggled", new
            {
                Shortcut = change.Color == "#FFFFFF" ? "B,W" : "B",
                DisplayName = change.Display,
                IsTransparent = transparent,
                BackgroundColor = change.Color
            });
            _modeNotice.Display($"{change.Display}: {(transparent ? "transparent" : change.Color == "#FFFFFF" ? "white" : "black")}   •   B", display: change.Display);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) when (_disposed) { }
        catch (Exception ex)
        {
            Telemetry.Error("BackgroundTransparencyToggleFailed", ex, new
            {
                DisplayName = change.Display
            });
            _modeNotice.Display("Background could not be changed", true);
        }
        finally { _togglingBackground = false; }
    }
    private async Task ToggleScreenTransparencyAsync(CancellationToken token)
    {
        if (_disposed || !_options.ConfigurationMode || _togglingTransparency)
            return;
        if (_calibration.SelectedScreen is not ScreenSurface selected)
        {
            _modeNotice.Display("Select a video screen first   •   T: chroma key");
            return;
        }
        _togglingTransparency = true;
        try
        {
            var enabled = await _configuration.ToggleScreenTransparencyAsync(selected.DisplayName, selected.ScreenId, token);
            if (_disposed) return;
            var name = _options.ScreensFor(selected.DisplayName).FirstOrDefault(s => s.Id == selected.ScreenId)?.Name;
            Telemetry.Info("ScreenTransparencyToggled", new { Shortcut = "T", selected.DisplayName, selected.ScreenId, ScreenName = name, BlackKeyEnabled = enabled });
            _modeNotice.Display($"{name}: chroma key {(enabled ? "ON" : "OFF")}   •   T");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) when (_disposed) { }
        catch (Exception ex)
        {
            Telemetry.Error("ScreenTransparencyToggleFailed", ex, new { selected.DisplayName, selected.ScreenId });
            _modeNotice.Display(ex.Message, true);
        }
        finally { _togglingTransparency = false; }
    }
    private async Task ToggleScreenSizingAsync(CancellationToken token)
    {
        if (_disposed || !_options.ConfigurationMode || _togglingSizing) return;
        if (_calibration.SelectedScreen is not ScreenSurface selected)
        {
            _modeNotice.Display("Select a video screen first   •   S: Fit/Stretch");
            return;
        }
        _togglingSizing = true;
        try
        {
            var mode = await _configuration.ToggleScreenSizingAsync(selected.DisplayName, selected.ScreenId, token);
            if (_disposed) return;
            var name = _options.ScreensFor(selected.DisplayName).FirstOrDefault(s => s.Id == selected.ScreenId)?.Name;
            Telemetry.Info("ScreenSizingToggled", new { Shortcut = "S", selected.DisplayName, selected.ScreenId, ScreenName = name, SizingMode = mode });
            _modeNotice.Display($"{name}: {(mode == "stretch" ? "Stretch" : "Fit (preserve aspect ratio)")}   •   S");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) when (_disposed) { }
        catch (Exception ex)
        {
            Telemetry.Error("ScreenSizingToggleFailed", ex, new { selected.DisplayName, selected.ScreenId });
            _modeNotice.Display(ex.Message, true);
        }
        finally { _togglingSizing = false; }
    }

    private void PressScreenBrightness(int direction)
    {
        if (_disposed || !_options.ConfigurationMode || _editingText) return;
        CancelBackgroundChoice(); _addScreenArmed = false;
        if (_calibration.SelectedScreen is not ScreenSurface selected)
        {
            _modeNotice.Display("Select a video or image screen first   •   +/-: brightness");
            return;
        }
        if (_brightnessChanges.Count >= 32) return;
        _brightnessChanges.Enqueue((selected.DisplayName, selected.ScreenId, direction));
        if (!_adjustingBrightness) _tasks.Run(AdjustScreenBrightnessAsync, "Adjust screen brightness");
    }
    private async Task AdjustScreenBrightnessAsync(CancellationToken token)
    {
        _adjustingBrightness = true;
        try
        {
            while (!_disposed && _options.ConfigurationMode && _brightnessChanges.TryDequeue(out var change))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var brightness = await _configuration.AdjustScreenBrightnessAsync(change.Display, change.ScreenId, change.Direction * 5, token);
                    if (_disposed) return;
                    var name = _options.ScreensFor(change.Display).FirstOrDefault(s => s.Id == change.ScreenId)?.Name;
                    Telemetry.Info("ScreenBrightnessChanged", new
                    {
                        Shortcut = change.Direction > 0 ? "+" : "-", DisplayName = change.Display,
                        change.ScreenId, ScreenName = name, Brightness = brightness
                    });
                    _modeNotice.Display($"{name}: brightness {brightness:0.##}%   •   +/-", display: change.Display);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) when (_disposed) { return; }
                catch (Exception ex)
                {
                    Telemetry.Error("ScreenBrightnessChangeFailed", ex, new { DisplayName = change.Display, change.ScreenId });
                    _modeNotice.Display(ex.Message, true);
                }
            }
        }
        finally { _adjustingBrightness = false; _brightnessChanges.Clear(); }
    }

    private void ApplyConfiguration(ExperienceOptions previous, ExperienceOptions next)
    {
        if (_backgroundSelection.PendingDisplay is { } pending &&
            (!next.ConfigurationMode || DisplayBackground.For(pending, previous) != DisplayBackground.For(pending, next)))
            CancelBackgroundChoice();
        if (previous.ConfigurationMode != next.ConfigurationMode)
        {
            _addScreenArmed = false;
            UpdateHotkeys(next.ConfigurationMode);
        }
        _calibration.ConfigurationChanged(next);
        _displays.UpdateOptions(next);
        _screenMedia.Configure(next.VideoDirectory);
        _tasks.Run(_ => _calibration.ConfigurationAppliedAsync(previous, next), "Confirm remote calibration");
        _audio.UpdateOptions(next);
        _mediaDiscovery.Configure(next);
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
                DisplayWindows = _displays.Windows.Select(window => new { window.DisplayName, State = window.State.ToString() }).ToArray(),
                ConnectedDisplays = System.Windows.Forms.Screen.AllScreens.Length,
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
        if (request.IsStop)
        {
            _videoStops.Record(request.Source, request.PlaybackRequestId!, _options.RequestDeduplicationSeconds);
            foreach (var window in _displays.Windows)
                try { window.StopVideoPlayback(request.Source, request.PlaybackRequestId!); }
                catch (InvalidOperationException ex) { Telemetry.Warning("VideoStopOutputBlocked", new { window.DisplayName, request.PlaybackRequestId, Reason = ex.Message }, request.RequestId); }
            Telemetry.Info("VideoStopRequested", new { request.Source, request.PlaybackRequestId }, request.RequestId);
            return;
        }
        if (_videoStops.Contains(request.Source, request.RequestId))
        {
            Telemetry.Info("VideoRequestAlreadyStopped", new { request.Source }, request.RequestId);
            return;
        }
        var video = string.Equals(request.Experience, "video", StringComparison.OrdinalIgnoreCase);
        try
        {
            IReadOnlyList<(string Display, ScreenOptions Screen)>? outputs = null;
            if (video)
            {
                _displays.Refresh();
                var selected = ScreenSelection.VideoOutputs(request, _options,
                    System.Windows.Forms.Screen.AllScreens.Select(s => s.DeviceName).ToArray(), System.Windows.Forms.Screen.PrimaryScreen?.DeviceName);
                outputs = selected.Where(output =>
                {
                    var binding = _screenMedia.Current.Match(output.Screen);
                    if (binding is null) return true;
                    Telemetry.Info("VideoCommandIgnoredScreenLocked", new
                    {
                        DisplayName = output.Display, ScreenName = output.Screen.Name, ScreenId = output.Screen.Id,
                        BoundPath = binding.Path, RequestedVideo = request.Value
                    }, request.RequestId);
                    return false;
                }).ToArray();
                if (selected.Count > 0 && outputs.Count == 0) return;
            }
            var path = MediaPath.Resolve(video ? _options.VideoDirectory : _options.AudioDirectory, request.Value);
            Telemetry.SetRequestContext(request.RequestId, request.LogContext(path));
            Telemetry.Info("MediaResolved", new { request.Value, Path = path, Local = true }, request.RequestId);
            if (video)
            {
                if (outputs!.Count == 0)
                {
                    Telemetry.Warning("ExperienceBlockedVideoOutputUnavailable", new
                    {
                        request.Screen,
                        request.DisplayName,
                        _options.DefaultDisplayName
                    }, request.RequestId);
                    return;
                }
                foreach (var output in outputs)
                {
                    var window = _displays.Windows.FirstOrDefault(w => w.DisplayName.Equals(output.Display, StringComparison.OrdinalIgnoreCase));
                    if (window is null) continue;
                    try
                    {
                        window.PlayVideo(path, request with { ScreenId = output.Screen.Id, ScreenName = output.Screen.Name });
                        Telemetry.Info("VideoOutputSelected", new { RequestedScreen = request.Screen, RequestedDisplayName = request.DisplayName, DisplayName = output.Display, ScreenName = output.Screen.Name }, request.RequestId);
                    }
                    catch (InvalidOperationException ex)
                    {
                        Telemetry.Warning("VideoOutputBlocked", new { DisplayName = output.Display, ScreenName = output.Screen.Name, Reason = ex.Message }, request.RequestId);
                    }
                }
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
        _configurationShortcut.Dispose();
        _hotkeys.Dispose();
        _modeNotice.Dispose();
        _audioPoll.Stop();
        _calibration.Cancel("Shutdown");
        // Quiesce producers before disposing outputs; keep the dispatcher pumping while awaiting.
        await _listener.DisposeAsync();
        await _definitions.DisposeAsync();
        await _mediaDiscovery.DisposeAsync();
        await _screenMedia.DisposeAsync();
        await _configuration.DisposeAsync();
        await _calibration.DisposeAsync();
        await _tasks.DisposeAsync();
        _audio.Dispose();
        await _displays.DisposeAsync();
    }
}
