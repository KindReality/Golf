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
    private readonly VideoPlaybackPriority _videoPriority = new();
    internal int AudioPlaybackCount => _audio.Count;
    internal bool CalibrationBusy => _calibration.Busy;
    private readonly PointCalibration _calibration;
    private readonly UdpExperienceListener _listener;
    private readonly ExperienceAnnouncements _announcements;
    private readonly PlaybackPriorityEvents _priorityEvents;
    private AudioOutput[] _inventoryAudio = [];
    private string? _defaultAudioId;
    private readonly LocalControllerConnection _localController;
    private readonly ControllerDefinitionsCache _definitions;
    private readonly ScreenMediaMonitor _screenMedia;
    private readonly ApplicationHotkeys _hotkeys;
    private readonly PhotoBooth.Windows.ConfigurationShortcut _configurationShortcut;
    private readonly IdleMouseCursor _idleCursor;
    private readonly ConfigurationModeNotice _modeNotice = new();
    private readonly BackgroundSelection _backgroundSelection = new();
    private readonly VideoStopHistory _videoStops = new();
    private readonly AsyncTaskScope _tasks = new("Experience host");
    private readonly DispatcherTimer _audioPoll = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _polling, _disposed, _togglingBackground, _togglingTransparency, _restartRequested;
    private string? _deviceFailure;
    private bool _addScreenArmed, _editingText, _addingScreen;
    private bool _creatingPerspective, _choosingPerspective, _resettingVideo, _editingScreenProperties;
    internal ConfigurationSubmenu ActiveSubmenu => _addScreenArmed ? ConfigurationSubmenu.AddScreen :
        _backgroundSelection.PendingDisplay is not null ? ConfigurationSubmenu.Background : ConfigurationSubmenu.None;
    internal string HudMessage => _modeNotice.Message;
    private string? _addScreenDisplay;
    private readonly Queue<(string Display, string ScreenId, int Direction)> _brightnessChanges = new();
    private bool _adjustingBrightness;
    private Task _matCommands = Task.CompletedTask;
    private Task _clipboardCommands = Task.CompletedTask;
    private Task _screenLockCommands = Task.CompletedTask;
    private bool _pastingScreen;
    private bool CanEditScreen(ScreenSurface selected)
    {
        var screen = _options.ScreensFor(selected.DisplayName).FirstOrDefault(s => s.Id == selected.ScreenId);
        if (screen is null) return false;
        if (!screen.IsLocked) return true;
        _modeNotice.Display($"{screen.Name} is locked   •   L: unlock", display: selected.DisplayName);
        return false;
    }
    private void PressScreenLock()
    {
        if (_disposed || !_options.ConfigurationMode || _editingText || ActiveSubmenu != ConfigurationSubmenu.None) return;
        if (_calibration.SelectedScreen is not ScreenSurface selected)
        { _modeNotice.Display("Select an item first   •   L: lock / unlock"); return; }
        var previous = _screenLockCommands;
        _screenLockCommands = ToggleScreenLockAsync(previous, selected, _tasks.Token);
        var operation = _screenLockCommands;
        _tasks.Run(_ => operation, "Toggle screen editing lock");
    }
    private async Task ToggleScreenLockAsync(Task previous, ScreenSurface selected, CancellationToken token)
    {
        try
        {
            await previous;
            if (_disposed || !_options.ConfigurationMode || _editingText) return;
            var window = _displays.Windows.FirstOrDefault(w => w.DisplayName == selected.DisplayName);
            if (window is null) return;
            await window.FinishFramingAsync();
            await _calibration.FinishAsync(selected);
            await _configuration.Store.ToggleScreenLockAsync(selected.DisplayName, selected.ScreenId, token);
            await _configuration.ReloadAsync();
            if (_disposed) return;
            var screen = _options.ScreensFor(selected.DisplayName).FirstOrDefault(s => s.Id == selected.ScreenId);
            if (screen is null) return;
            if (_calibration.SelectedScreen == selected) _calibration.SelectScreen(selected);
            Telemetry.Info("ScreenLockToggled", new { selected.DisplayName, selected.ScreenId, ScreenName = screen.Name, screen.IsLocked, Shortcut = "L" });
            _modeNotice.Display($"{screen.Name}: {(screen.IsLocked ? "locked   •   L: unlock" : "unlocked   •   L: lock")}", display: selected.DisplayName);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed) { Telemetry.Error("ScreenLockToggleFailed", ex); _modeNotice.Display(ex.Message, true); } }
    }
    private void PressScreenClipboard(bool paste)
    {
        if (_disposed || !_options.ConfigurationMode || _editingText || _addingScreen && !_pastingScreen) return;
        CancelBackgroundChoice(); _addScreenArmed = false;
        var selected = _calibration.SelectedScreen as ScreenSurface;
        if (selected is not null && !CanEditScreen(selected)) return;
        var display = paste ? System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).DeviceName : selected?.DisplayName;
        var window = display is null ? null : _displays.Select(display);
        var previous = _clipboardCommands;
        var operation = _clipboardCommands = RunClipboardCommandAsync(previous, paste, selected, window, _tasks.Token);
        _tasks.Run(_ => operation, paste ? "Paste screen" : "Copy screen");
    }
    private async Task RunClipboardCommandAsync(Task previous, bool paste, ScreenSurface? selected, MainWindow? window, CancellationToken token)
    {
        var creating = false;
        try
        {
            await previous;
            token.ThrowIfCancellationRequested();
            if (_disposed || !_options.ConfigurationMode || _editingText || _addingScreen) return;
            if (window is null || !window.Visible) throw new InvalidOperationException("The target display is unavailable.");
            if (_calibration.Busy || window.Capture) throw new InvalidOperationException("Finish moving the screen before copying or pasting.");
            await window.FinishFramingAsync();
            if (_disposed || !_options.ConfigurationMode || _editingText) return;
            if (!paste)
            {
                var screen = selected is null ? null : _options.ScreensFor(selected.DisplayName).FirstOrDefault(s => s.Id == selected.ScreenId);
                if (screen is null || !screen.IsVideo && !screen.IsMat)
                    throw new InvalidOperationException("Select a video, image or mat screen to copy.");
                screen.RequireEditable();
                var mediaFile = screen.MediaFile ?? _screenMedia.Current.Match(screen)?.RelativePath;
                ScreenClipboard.Write(new() { SourceDisplay = selected!.DisplayName, Screen = screen with { MediaFile = mediaFile } });
                Telemetry.Info("ScreenCopied", new { selected.DisplayName, ScreenId = screen.Id, ScreenName = screen.Name, ScreenType = screen.Type, mediaFile });
                _modeNotice.Display($"Copied {screen.Name}   •   Ctrl+V to paste", display: selected.DisplayName);
                return;
            }
            var copy = ScreenClipboard.Read();
            _addingScreen = _pastingScreen = creating = true;
            var added = await _configuration.Store.PasteScreenAsync(window.DisplayName, copy, token);
            await _configuration.ReloadAsync();
            if (_disposed || !_options.ConfigurationMode) return;
            _calibration.SelectScreen(window.Surface(added.Id));
            window.Activate(); window.Focus(); window.Surface(added.Id).FlashCalibrationPoints(15);
            Telemetry.Info("ScreenPasted", new { window.DisplayName, ScreenId = added.Id, ScreenName = added.Name,
                ScreenType = added.Type, copy.SourceDisplay, SourceScreenId = copy.Screen.Id, added.MediaFile });
            _modeNotice.Display($"Pasted {added.Name}", display: window.DisplayName);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Telemetry.Error(paste ? "ScreenPasteFailed" : "ScreenCopyFailed", ex);
            if (!_disposed) _modeNotice.Display(ex.Message, true);
        }
        finally { if (creating) _addingScreen = _pastingScreen = false; }
    }

    private void PressMat()
    {
        CancelBackgroundChoice();
        var create = _addScreenArmed;
        var display = _addScreenDisplay;
        _addScreenArmed = false;
        UpdateHotkeys(_options.ConfigurationMode);
        var previous = _matCommands;
        // A rapid A,M,M must finish creating/selecting the mat before toggling it.
        var operation = _matCommands = RunMatCommandAsync(previous, create, display, _tasks.Token);
        _tasks.Run(_ => operation, "Create or toggle mat");
    }
    private async Task RunMatCommandAsync(Task previous, bool create, string? display, CancellationToken token)
    {
        try
        {
            await previous;
            token.ThrowIfCancellationRequested();
            if (_disposed || !_options.ConfigurationMode || _editingText) return;
            if (create) { await CreateScreenAsync(token, "mat", display); return; }
            if (_addingScreen || _calibration.SelectedScreen is not ScreenSurface selected ||
                !_options.ScreensFor(selected.DisplayName).Any(s => s.Id == selected.ScreenId && s.IsMat))
            {
                _modeNotice.Display("Select a mat first   •   M: normal / negative");
                return;
            }
            if (!CanEditScreen(selected)) return;
            var json = await _configuration.Store.ToggleMatNegativeAsync(selected.DisplayName, selected.ScreenId, token);
            await _configuration.ReloadAsync();
            if (_disposed) return;
            var expected = ExperienceOptions.Parse(json).ScreensFor(selected.DisplayName).Single(s => s.Id == selected.ScreenId);
            var applied = _options.ScreensFor(selected.DisplayName).FirstOrDefault(s => s.Id == selected.ScreenId);
            if (applied?.IsNegative != expected.IsNegative)
                throw new InvalidOperationException("The mat setting could not be applied or changed again remotely.");
            Telemetry.Info("MatNegativeToggled", new { selected.DisplayName, selected.ScreenId, ScreenName = applied.Name, applied.IsNegative, Shortcut = "M" });
            _modeNotice.Display($"{applied.Name}: {(applied.IsNegative ? "negative — cut out outside" : "normal — cut out inside")}   •   M", display: selected.DisplayName);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) when (_disposed) { }
        catch (Exception ex) { Telemetry.Error("MatNegativeToggleFailed", ex); _modeNotice.Display(ex.Message, true); }
    }
    private void PressAddScreen(string type)
    {
        if (!_addScreenArmed) return;
        var display = _addScreenDisplay;
        _addScreenArmed = false;
        UpdateHotkeys(_options.ConfigurationMode);
        _tasks.Run(token => CreateScreenAsync(token, type, display), $"Create {type} screen");
    }
    private async Task CreateScreenAsync(CancellationToken token, string type, string? display)
    {
        if (_disposed || !_options.ConfigurationMode || _addingScreen)
            return;
        _addingScreen = true;
        try
        {
            var window = _displays.Select(display) ?? throw new InvalidOperationException("The selected display is unavailable.");
            PhotoBooth.Networking.PerspectiveSelection? perspective = null;
            string? name = null;
            var roles = "";
            if (type == "visualization" && _options.DisplayScreens.Values.SelectMany(s => s).Any(s => s.IsVisualization))
            {
                _modeNotice.Display("Only one visualization is supported. Delete the existing one first.", display: window.DisplayName);
                return;
            }
            if (type == "video")
            {
                var label = _options.DisplayAliases.FirstOrDefault(alias => alias.Value.Equals(window.DisplayName, StringComparison.OrdinalIgnoreCase)).Key ?? window.DisplayName;
                using var dialog = new ScreenPropertiesDialog(_options.ScreensFor(window.DisplayName), _definitions.Perspectives, ScreenPropertiesDialog.KnownRoles(_options, _definitions.Roles), label);
                System.Windows.Forms.DialogResult result;
                _editingText = true; UpdateHotkeys(true);
                try { result = dialog.ShowDialog(window); }
                finally { _editingText = false; UpdateHotkeys(_options.ConfigurationMode); }
                if (result != System.Windows.Forms.DialogResult.OK) return;
                name = dialog.ScreenName; roles = dialog.Roles; perspective = dialog.Selection;
            }
            else if (type == "fastled")
            {
                using var picker = new PerspectivePicker(_definitions.Perspectives, led: type == "fastled");
                System.Windows.Forms.DialogResult result;
                _editingText=true; UpdateHotkeys(true);
                try { result=picker.ShowDialog(window); }
                finally { _editingText=false; UpdateHotkeys(_options.ConfigurationMode); }
                if (result != System.Windows.Forms.DialogResult.OK || type == "fastled" && picker.Selection is null) return;
                perspective = picker.Selection;
            }
            var created = await _configuration.Store.AddScreenAsync(window.DisplayName, token, type, perspective, name, roles);
            await _configuration.ReloadAsync();
            if (_disposed || !_options.ConfigurationMode)
                return;
            _calibration.SelectScreen(window.Surface(created.Id));
            window.Activate();
            window.Focus();
            window.Surface(created.Id).FlashCalibrationPoints(15);
            Telemetry.Info("ScreenCreated", new
            {
                window.DisplayName,
                ScreenName = created.Name,
                ScreenType = created.Type,
                ScreenId = created.Id,
                created.Roles,
                Perspective = created.Perspective?.Name
            });
            _modeNotice.Display($"Created {created.Name}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Telemetry.Error("ScreenCreationFailed", ex); if (!_disposed) _modeNotice.Display("Screen could not be created — check the log", true); }
        finally { _addingScreen = false; }
    }
    private async Task ChoosePerspectiveAsync(CancellationToken token)
    {
        if (_disposed || !_options.ConfigurationMode || _editingText || _creatingPerspective || _choosingPerspective ||
            _calibration.SelectedScreen is not ScreenSurface surface) return;
        var window = _displays.Windows.FirstOrDefault(w => w.DisplayName == surface.DisplayName);
        var screen = _options.ScreensFor(surface.DisplayName).FirstOrDefault(s => s.Id == surface.ScreenId);
        if (window is null || screen is null || !screen.IsVideo) return;
        if (!CanEditScreen(surface)) return;
        _choosingPerspective = true;
        CancelBackgroundChoice(); _addScreenArmed = false;
        try
        {
            await window.FinishFramingAsync();
            await _calibration.FinishAsync(surface);
            await _configuration.ReloadAsync();
            if (_disposed || !_options.ConfigurationMode) return;
            screen = _options.ScreensFor(surface.DisplayName).FirstOrDefault(s => s.Id == surface.ScreenId);
            if (screen is null || !screen.IsVideo || !CanEditScreen(surface)) return;
            var display = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(d => d.DeviceName == surface.DisplayName)
                ?? throw new InvalidOperationException("The selected display is unavailable.");
            var dimensions = ScreenPerspectiveCrop.Calculate(screen.Points, display.Bounds.Width, display.Bounds.Height);
            using var picker = new PerspectivePicker(_definitions.Perspectives, screen.Perspective, createFrom: dimensions);
            System.Windows.Forms.DialogResult result;
            _editingText = true; UpdateHotkeys(true);
            try { result = picker.ShowDialog(window); }
            finally { _editingText = false; UpdateHotkeys(_options.ConfigurationMode); }
            if (result != System.Windows.Forms.DialogResult.OK || _disposed || !_options.ConfigurationMode) return;
            if (picker.CreateNew) await CreatePerspectiveAsync(token, surface);
            else
            {
                await _configuration.Store.SetPerspectiveAsync(surface.DisplayName, surface.ScreenId, picker.Selection);
                await _configuration.ReloadAsync();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed) { _modeNotice.Display(ex.Message, true); Telemetry.Error("PerspectiveSelectionFailed", ex); } }
        finally { _choosingPerspective = false; }
    }
    private async Task EditScreenPropertiesAsync(CancellationToken token)
    {
        if (_disposed || !_options.ConfigurationMode || _editingText || _editingScreenProperties || _addingScreen ||
            _calibration.SelectedScreen is not ScreenSurface selected) return;
        var window = _displays.Windows.FirstOrDefault(w => w.DisplayName == selected.DisplayName);
        if (window is null || !CanEditScreen(selected)) return;
        _editingScreenProperties = true;
        CancelBackgroundChoice(); _addScreenArmed = false;
        try
        {
            await window.FinishFramingAsync();
            await _calibration.FinishAsync(selected);
            await _configuration.ReloadAsync();
            var screen = _options.ScreensFor(selected.DisplayName).FirstOrDefault(s => s.Id == selected.ScreenId);
            if (_disposed || !_options.ConfigurationMode || screen is null || !CanEditScreen(selected)) return;
            var label = _options.DisplayAliases.FirstOrDefault(alias => alias.Value.Equals(selected.DisplayName, StringComparison.OrdinalIgnoreCase)).Key ?? selected.DisplayName;
            using var dialog = new ScreenPropertiesDialog(_options.ScreensFor(selected.DisplayName), _definitions.Perspectives,
                ScreenPropertiesDialog.KnownRoles(_options, _definitions.Roles), label, screen);
            System.Windows.Forms.DialogResult result;
            _editingText = true; UpdateHotkeys(true);
            try { result = dialog.ShowDialog(window); }
            finally { _editingText = false; UpdateHotkeys(_options.ConfigurationMode); }
            if (result != System.Windows.Forms.DialogResult.OK || _disposed || !_options.ConfigurationMode) return;
            var mediaFile = screen.MediaFile;
            if (screen.IsVideo && dialog.ScreenName != screen.Name && mediaFile is null)
                mediaFile = _screenMedia.Current.Match(new() { Name = dialog.ScreenName })?.RelativePath;
            await _configuration.Store.SaveScreenPropertiesAsync(selected.DisplayName, screen, dialog.ScreenName, dialog.Roles, dialog.Selection, mediaFile, token);
            await _configuration.ReloadAsync();
            if (_disposed) return;
            _calibration.SelectScreen(window.Surface(screen.Id));
            window.Surface(screen.Id).FlashCalibrationPoints(-1);
            window.Activate(); window.Focus();
            Telemetry.Info("ScreenPropertiesSaved", new { selected.DisplayName, ScreenId = screen.Id, ScreenName = dialog.ScreenName, dialog.Roles, Perspective = dialog.Selection?.Name });
            _modeNotice.Display($"Saved {dialog.ScreenName}", display: selected.DisplayName);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Telemetry.Error("ScreenPropertiesSaveFailed", ex); if (!_disposed) _modeNotice.Display(ex.Message, true); }
        finally { _editingScreenProperties = false; }
    }
    private async Task<bool> RenameScreenAsync(ScreenSurface surface, string oldName, string name, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (name == oldName) return true;
            var definition = _options.ScreensFor(surface.DisplayName).FirstOrDefault(s => s.Id == surface.ScreenId);
            var binding = definition?.IsVideo == true ? _screenMedia.Current.Match(new() { Name = name }) : null;
            var mediaFile = definition?.MediaFile ?? binding?.RelativePath;
            await _configuration.Store.RenameScreenAsync(surface.DisplayName, surface.ScreenId, oldName, name, mediaFile);
            await _configuration.ReloadAsync();
            if (_disposed)
                return false;
            Telemetry.Info("ScreenRenamed", new
            {
                surface.DisplayName,
                surface.ScreenId,
                OldName = oldName,
                Name = _options.ScreensFor(surface.DisplayName).FirstOrDefault(s => s.Id == surface.ScreenId)?.Name,
                RequestedName = name, MediaFile = mediaFile
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
    internal event Action? ShutdownRequested;
    internal event Action? RestartRequested;
    internal void ReportRestartFailure(string reason)
    {
        if (_disposed) return;
        _restartRequested = false;
        _modeNotice.Display("Restart could not complete: " + reason, true);
    }
    internal nint HotkeyWindowHandle => _hotkeys.Handle;
    internal int[] VisualizationProcessIds => _displays.VisualizationProcessIds;
    internal IEnumerable<MainWindow> DisplayWindows => _displays.Windows;
    internal RenderHealth[] RendererHealth => _displays.RendererHealth;
    internal RendererRecoveryOptions RecoveryOptions => _options.RendererRecovery;
    internal IPointSurface? SelectedScreen => _calibration.SelectedScreen;
    internal string? SelectedDisplayName => _calibration.SelectedDisplayName;

    public ExperienceHost(Dispatcher dispatcher, string path, Func<bool>? hotkeysFocused = null)
    {
        _configuration = new(dispatcher, path);
        _idleCursor = new(dispatcher);
        _audio = new(dispatcher);
        _calibration = new(() => _options, () => _displays!.Windows.SelectMany(window => window.Surfaces), _configuration.Store, _configuration.ReloadAsync);
        _displays = new(dispatcher, _calibration);
        _screenMedia = new(dispatcher, _displays.UpdateMediaCatalog);
        _displays.CloseRequested += () => CloseRequested?.Invoke();
        _announcements = new(() => _options);
        _priorityEvents = new(() => _options);
        _definitions = new(async definitions => { await _configuration.Store.ApplyControllerDefinitionsAsync(definitions); await _configuration.ReloadAsync(); });
        _localController = new(dispatcher, _definitions.ApplyAsync);
        _listener = new(dispatcher, () => _options, HandleRequest, discovered: () => _announcements.RequestAnnouncement());
        _configurationShortcut = new("Experience", () => _options.ConfigurationMode, enabled => _tasks.Run(token => SetSharedConfigurationAsync(enabled, token), "Set shared configuration"), FocusExperience,
            () => dispatcher.BeginInvoke(new Action(() => ShutdownRequested?.Invoke())));
        _hotkeys = new(() => _configurationShortcut.SwitchTo("Controller"), () =>
        {
            if (_disposed || _restartRequested || !_options.ConfigurationMode || _editingText)
                return;
            _restartRequested = true;
            Telemetry.Info("RestartHotkeyPressed", new
            {
                Shortcut = "X"
            });
            // Leave the native message handler before disposing its receiving window.
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed) return;
                _modeNotice.Display("Restarting Experience…", display: _calibration.SelectedScreen?.DisplayName);
                RestartRequested?.Invoke();
            }));
        }, () =>
        {
            PressBackgroundB();
        }, hotkeysFocused);
        _configuration.Changing += next => { if (next.UdpCommandsPort != _options.UdpCommandsPort || next.UdpEventsPort != _options.UdpEventsPort) _listener.Bind(next.UdpCommandsPort, next.UdpEventsPort); };
        _configuration.Changed += ApplyConfiguration;
        _hotkeys.CancelSubmenu = CancelConfigurationSubmenu;
        _displays.CancelSubmenuRequested += CancelConfigurationSubmenu;
        _hotkeys.AddScreenPrefix = () =>
        {
            CancelBackgroundChoice();
            _addScreenArmed = true;
            _addScreenDisplay = _calibration.SelectedDisplayName ?? _displays.Select(null)?.DisplayName;
            UpdateHotkeys(_options.ConfigurationMode);
        };
        _hotkeys.BeforeShortcut = () => { foreach (var window in _displays.Windows) window.SuspendModifierFraming(); };
        _hotkeys.EditScreenName = () =>
        {
            CancelBackgroundChoice(); _addScreenArmed = false;
            if (_calibration.SelectedScreen is ScreenSurface selected)
                _displays.Windows.FirstOrDefault(window => window.DisplayName == selected.DisplayName)?.EditScreenName(selected);
            else _modeNotice.Display("Select a screen first   •   N: edit name");
        };
        _hotkeys.EditScreenProperties = () =>
        {
            if (_calibration.SelectedScreen is null) _modeNotice.Display("Select a screen first   •   Enter: screen properties");
            else _tasks.Run(EditScreenPropertiesAsync, "Edit screen properties");
        };
        _hotkeys.ToggleScreenTransparency = () => { CancelBackgroundChoice(); _tasks.Run(ToggleScreenTransparencyAsync, "Toggle screen chroma key"); };
        _hotkeys.ToggleScreenLock = PressScreenLock;
        _hotkeys.AdjustScreenBrightness = PressScreenBrightness;
        _hotkeys.CopyScreen = () => PressScreenClipboard(false);
        _hotkeys.PasteScreen = () => PressScreenClipboard(true);
        _hotkeys.ZoomScreen = direction =>
        {
            if (_disposed || !_options.ConfigurationMode || _editingText) return;
            CancelBackgroundChoice(); _addScreenArmed = false;
            if (_calibration.SelectedScreen is ScreenSurface selected)
            {
                var window = _displays.Windows.FirstOrDefault(window => window.DisplayName == selected.DisplayName);
                if (window is not null) _tasks.Run(async _ => { await window.FinishFramingAsync(); if (!_disposed && _calibration.SelectedScreen == selected) window.ZoomSelected(direction); }, "Keyboard media zoom");
            }
        };
        _hotkeys.ResetVideo = () => _tasks.Run(ResetSelectedVideoAsync, "Reset selected video");
        _hotkeys.ChooseWhiteBackground = () => ApplyBackgroundChoice(_backgroundSelection.PressW());
        _hotkeys.AddVideoScreen = () => PressAddScreen("video");
        _hotkeys.ChoosePerspective = () => _tasks.Run(ChoosePerspectiveAsync, "Choose screen perspective");
        _hotkeys.AddVisualizationScreen = () => PressAddScreen("visualization");
        _hotkeys.AddMatScreen = PressMat;
        _hotkeys.AddLedScreen = () => PressAddScreen("fastled");
        _displays.TextEditingChanged += editing => { if (_disposed) return; _editingText = editing; _addScreenArmed = false; CancelBackgroundChoice(); UpdateHotkeys(_options.ConfigurationMode); };
        _displays.RenameRequested += (surface, oldName, name) => _tasks.Run(token => RenameScreenAsync(surface, oldName, name, token), "Rename screen");
        _displays.SelectMediaRequested = async (surface, screen, media) =>
        {
            try
            {
                await _configuration.Store.SelectMediaAsync(surface.DisplayName,screen,media);
                await _configuration.ReloadAsync();
                Telemetry.Info("ScreenMediaSelected",new { surface.DisplayName,ScreenId=screen.Id,ScreenName=screen.Name,MediaFile=media });
                return true;
            }
            catch(Exception ex) { Telemetry.Error("ScreenMediaSelectionFailed",ex); if(!_disposed)_modeNotice.Display(ex.Message,true);return false; }
        };
        _displays.SaveFramingRequested = async (display, screen, framing, token) =>
        {
            await _configuration.Store.SaveVideoFramingAsync(display, screen, framing, token);
            await _configuration.ReloadAsync();
            if (_options.ScreensFor(display).FirstOrDefault(s => s.Id == screen.Id)?.Framing != framing)
                throw new InvalidOperationException("Video framing was changed again remotely.");
            Telemetry.Info("VideoFramingSaved", new { DisplayName = display, ScreenName = screen.Name, ScreenId = screen.Id, framing.Zoom, framing.CenterX, framing.CenterY, framing.RotationDegrees, Perspective = screen.Perspective?.Name });
        };
        _audioPoll.Tick += (_, _) => _tasks.Run(PollAudioAsync, "Audio inventory");
        Telemetry.SetHealthProvider(() => new { State = _health, Renderers = _displays.RendererHealth });
    }
    public void Start()
    {
        // Reject an unrecoverable configuration before opening any output or network listener.
        _configuration.LoadInitial();
        _definitions.Seed(_options.ControllerDefinitions.Values);
        _screenMedia.Start(_options.VideoDirectory);
        _audio.UpdateOptions(_options);
        _listener.Bind(_options.UdpCommandsPort, _options.UdpEventsPort);
        _localController.Configure(_options);
        _displays.Start(_options);
        PublishInventory(); _announcements.Start();
        _configuration.StartWatching();
        UpdateHotkeys(_options.ConfigurationMode);
        _idleCursor.Start();
        _audioPoll.Start();
        _tasks.Run(PollAudioAsync, "Initial audio inventory");
    }
    internal void FocusExperience()
    {
        if (_disposed) return;
        var window = _displays.Select(_calibration.SelectedDisplayName);
        window?.FocusConfiguration();
    }
    private async Task SetSharedConfigurationAsync(bool enabled, CancellationToken token)
    {
        if (!enabled) foreach (var window in _displays.Windows) await window.FinishFramingAsync();
        await _configuration.Store.SetConfigurationModeAsync(enabled, token);
        await _configuration.ReloadAsync();
        if (enabled) FocusExperience();
        _modeNotice.Display(enabled ? "Configuration ON • Ctrl+Shift+C: exit • C: Controller • A: add • Enter: screen properties" : "Configuration OFF");
    }
    private void UpdateHotkeys(bool configurationMode)
    {
        var menu = configurationMode && !_editingText ? ActiveSubmenu : ConfigurationSubmenu.None;
        _displays.SetSubmenu(menu);
        _modeNotice.SetMenu(menu == ConfigurationSubmenu.None ? null : ConfigurationSubmenuChoices.Text(menu),
            menu == ConfigurationSubmenu.Background ? _backgroundSelection.PendingDisplay : menu == ConfigurationSubmenu.AddScreen ? _addScreenDisplay : null);
        var unavailable = _hotkeys.Register(configurationMode, _editingText, menu);
        if (unavailable.Length > 0)
            _modeNotice.Display($"{string.Join(", ", unavailable)} unavailable — check the log", true);
    }
    private void CancelConfigurationSubmenu()
    {
        _addScreenArmed = false;
        _backgroundSelection.Cancel();
        UpdateHotkeys(_options.ConfigurationMode);
    }
    private void CancelBackgroundChoice()
    {
        if (_backgroundSelection.PendingDisplay is null) return;
        _backgroundSelection.Cancel();
        UpdateHotkeys(_options.ConfigurationMode);
    }
    private void PressBackgroundB()
    {
        if (_disposed || !_options.ConfigurationMode || _togglingBackground) return;
        _addScreenArmed = false;
        var display = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Control.MousePosition).DeviceName;
        var change = _backgroundSelection.PressB(display, DisplayBackground.For(display, _options).A == 0);
        if (change is not null) { ApplyBackgroundChoice(change); return; }
        UpdateHotkeys(true);
        var target = _backgroundSelection.PendingDisplay!;
        Telemetry.Info("BackgroundColorChoiceOpened", new { DisplayName = target });
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
            var visualizations = _options.ScreensFor(change.Display).Where(s => s.IsVisualization).Select(s => s.Id).ToArray();
            var transparent = await _configuration.SetBackgroundAsync(change.Display, change.IsTransparent, change.Color, token);
            if (_disposed)
                return;
            Telemetry.Info("BackgroundTransparencyToggled", new
            {
                Shortcut = change.Color == "#FFFFFF" ? "B,W" : change.Color == "#000000" ? "B,B" : "B",
                DisplayName = change.Display,
                IsTransparent = transparent,
                BackgroundColor = change.Color,
                RemovedVisualizationScreens = transparent ? Array.Empty<string>() : visualizations
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
            _modeNotice.Display("Select a video screen first   •   K: chroma key");
            return;
        }
        if (!CanEditScreen(selected)) return;
        _togglingTransparency = true;
        try
        {
            var enabled = await _configuration.ToggleScreenTransparencyAsync(selected.DisplayName, selected.ScreenId, token);
            if (_disposed) return;
            var name = _options.ScreensFor(selected.DisplayName).FirstOrDefault(s => s.Id == selected.ScreenId)?.Name;
            Telemetry.Info("ScreenTransparencyToggled", new { Shortcut = "T", selected.DisplayName, selected.ScreenId, ScreenName = name, BlackKeyEnabled = enabled });
            _modeNotice.Display($"{name}: chroma key {(enabled ? "ON" : "OFF")}   •   K");
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
    private async Task ResetSelectedVideoAsync(CancellationToken token)
    {
        if (_disposed || !_options.ConfigurationMode || _editingText || _resettingVideo ||
            ActiveSubmenu != ConfigurationSubmenu.None || _calibration.SelectedScreen is not ScreenSurface selected) return;
        var window = _displays.Windows.FirstOrDefault(w => w.DisplayName == selected.DisplayName);
        if (window is null || _options.ScreensFor(selected.DisplayName).FirstOrDefault(s => s.Id == selected.ScreenId)?.IsVideo != true) return;
        if (!CanEditScreen(selected)) return;
        _resettingVideo = true;
        try
        {
            await window.FinishFramingAsync();
            await _calibration.FinishAsync(selected);
            await _configuration.ReloadAsync();
            if (_disposed || !_options.ConfigurationMode || _calibration.SelectedScreen != selected) return;
            var expected = _options.ScreensFor(selected.DisplayName).FirstOrDefault(s => s.Id == selected.ScreenId);
            if (expected?.IsVideo != true) return;
            await _configuration.Store.ResetVideoAsync(selected.DisplayName, expected, token);
            await _configuration.ReloadAsync();
            if (_disposed) return;
            var applied = _options.ScreensFor(selected.DisplayName).FirstOrDefault(s => s.Id == selected.ScreenId);
            if (applied is null || applied.Framing != new VideoFramingOptions() || applied.Brightness != 100 ||
                applied.Perspective is not null || applied.BlackKey is not null)
                throw new InvalidOperationException("Video settings changed again remotely while resetting.");
            selected.FlashCalibrationPoints(15);
            Telemetry.Info("VideoScreenReset", new { selected.DisplayName, selected.ScreenId, ScreenName = applied.Name,
                applied.Framing, applied.Brightness, applied.Perspective, BlackKey = applied.EffectiveBlackKey(_options), Shortcut = "Ctrl+R" });
            _modeNotice.Display($"{applied.Name}: video reset   •   Ctrl+R", display: selected.DisplayName);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed) { Telemetry.Error("VideoScreenResetFailed", ex); _modeNotice.Display(ex.Message, true); } }
        finally { _resettingVideo = false; }
    }

    private async Task CreatePerspectiveAsync(CancellationToken token, ScreenSurface selected)
    {
        if (_disposed || !_options.ConfigurationMode || _editingText || _creatingPerspective) return;
        if (!CanEditScreen(selected)) return;
        if (!_options.ScreensFor(selected.DisplayName).Any(s=>s.Id==selected.ScreenId && s.IsVideo)) return;
        _creatingPerspective=true;
        try
        {
            var window=_displays.Windows.First(w=>w.DisplayName==selected.DisplayName);
            var initial=_options.ScreensFor(selected.DisplayName).Single(s=>s.Id==selected.ScreenId);
            var controller=_definitions.LocalController;
            if(controller is null)throw new InvalidOperationException("The local Controller is unavailable. Start it on this computer and try P again.");
            if(!_options.ConfigurationMode)return;
            var display=System.Windows.Forms.Screen.AllScreens.FirstOrDefault(d=>d.DeviceName==selected.DisplayName) ?? throw new InvalidOperationException("The selected display is unavailable.");
            await _calibration.FinishAsync(selected); await _configuration.ReloadAsync();
            var screen=_options.ScreensFor(selected.DisplayName).Single(s=>s.Id==selected.ScreenId);
            screen.RequireEditable();
            var (width,height)=ScreenPerspectiveCrop.Calculate(screen.Points,display.Bounds.Width,display.Bounds.Height);
            var request=new PhotoBooth.Networking.PerspectiveCreateRequest { ControllerId=controller.ControllerId,Name=screen.Name,WidthPercent=width,HeightPercent=height };
            var bytes=request.Command(_options,controller.ControllerName);
            _modeNotice.Display($"Saving perspective on {controller.ControllerName}…",persistent:true,display:selected.DisplayName);
            await PhotoBooth.Networking.UdpBroadcastSender.SendAsync(bytes,_options,token);
            Telemetry.Info("PerspectiveCreationSent",new { controller.ControllerId, controller.ControllerName, request.PerspectiveId, request.Name, request.WidthPercent, request.HeightPercent, selected.DisplayName, selected.ScreenId });
            var deadline=Environment.TickCount64+20000;
            PhotoBooth.Networking.PerspectiveSelection? saved;
            while((saved=_definitions.FindPerspective(controller.ControllerId,request.PerspectiveId)) is null)
            {
                if(_disposed || !_options.ConfigurationMode)return;
                if(Environment.TickCount64>=deadline)throw new TimeoutException("Controller save was not confirmed by its definitions. Check connectivity and try P again.");
                await Task.Delay(100,token);
            }
            if(_disposed || !_options.ConfigurationMode)return;
            var latest=_options.ScreensFor(selected.DisplayName).FirstOrDefault(s=>s.Id==selected.ScreenId);
            if(latest is null || latest.Perspective!=screen.Perspective)throw new InvalidOperationException("The screen's perspective changed while saving. The new perspective remains available on the Controller.");
            await _configuration.Store.SetPerspectiveAsync(selected.DisplayName,selected.ScreenId,saved);
            await _configuration.ReloadAsync();
            Telemetry.Info("ScreenPerspectiveCreated",new { selected.DisplayName, selected.ScreenId, saved.ControllerId,saved.PerspectiveId,saved.Name,saved.Rectangle });
            _modeNotice.Display($"{screen.Name}: {saved.Name} saved • P",display:selected.DisplayName);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception ex) { if(!_disposed){Telemetry.Error("ScreenPerspectiveCreationFailed",ex);_modeNotice.Display(ex.Message,true);} }
        finally { _creatingPerspective=false; }
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
        if (!CanEditScreen(selected)) return;
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
        if (previous.ConfigurationMode && !next.ConfigurationMode)
        {
            foreach (var dialog in System.Windows.Forms.Application.OpenForms.OfType<ConfigurationDialog>().ToArray()) { dialog.DialogResult=System.Windows.Forms.DialogResult.Cancel; dialog.Close(); }
            _modeNotice.Hide();
        }
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
        _localController.Configure(next);
        PublishInventory();
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
            _inventoryAudio = inventory.Devices; _defaultAudioId = inventory.DefaultId;
            if (inventory.Devices.Any(device => !_options.AudioDevices.Keys.Any(key => key.Trim().Equals(device.Id, StringComparison.OrdinalIgnoreCase))))
            {
                await _configuration.Store.EnsureAudioDevicesAsync(inventory.Devices, inventory.DefaultId, token);
                await _configuration.ReloadAsync();
                if (_disposed || token.IsCancellationRequested) return;
            }
            PublishInventory();
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
    private void PublishInventory()
    {
        try { _announcements.Publish(_displays.Windows, _screenMedia.Current, _inventoryAudio, _defaultAudioId); }
        catch (Exception ex) { Telemetry.Error("ExperienceInventoryPublishFailed", ex); }
    }
    private void HandleVideoFade(ExperienceRequest request)
    {
        try
        {
            _displays.Refresh();
            var outputs = ScreenSelection.VideoOutputs(request, _options, System.Windows.Forms.Screen.AllScreens.Select(s => s.DeviceName).ToArray(), System.Windows.Forms.Screen.PrimaryScreen?.DeviceName);
            Telemetry.SetRequestContext(request.RequestId, request.LogContext());
            if (outputs.Count == 0)
            {
                Telemetry.Warning("VideoFadeOutputUnavailable", new { request.Command, request.Screen, request.DisplayName, request.Roles }, request.RequestId);
                return;
            }
            foreach (var output in outputs)
            {
                var window = _displays.Windows.FirstOrDefault(w => w.DisplayName.Equals(output.Display, StringComparison.OrdinalIgnoreCase));
                if (window is null) continue;
                var targeted = request with { ScreenId = output.Screen.Id, ScreenName = output.Screen.Name, PriorityCoordinator = _videoPriority, PriorityRejected = _priorityEvents.Report };
                if (_videoPriority.BlockingPriorityFor(request.EffectivePriority, output.Display, output.Screen.Id) is { } blocked)
                {
                    _priorityEvents.Report(targeted, output.Display, output.Screen.Name, blocked);
                    Telemetry.Info("VideoFadeSkippedLowerPriority", new { request.Command, DisplayName = output.Display, ScreenName = output.Screen.Name, Priority = request.EffectivePriority, ActivePriority = blocked }, request.RequestId);
                    continue;
                }
                window.FadeVideoScreen(targeted);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { Telemetry.Warning("VideoFadeRequestRejected", new { request.Command, request.Screen, request.Roles, Reason = ex.Message }, request.RequestId); }
    }
    private void HandleRequest(ExperienceRequest request)
    {
        if (_disposed)
            return;
        if (string.Equals(request.Experience, "visualization", StringComparison.OrdinalIgnoreCase))
        {
            _displays.Refresh();
            _tasks.Run(async token =>
            {
                try { await _displays.ControlVisualizationAsync(request, _options, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex) { Telemetry.Warning("VisualizationCommandRejected", new { request.Screen, request.Command, request.VisualType, request.Value, Reason = ex.Message }, request.RequestId); }
            }, "Control visualization");
            return;
        }
        if (request.IsStop)
        {
            _videoStops.Record(request.Source, request.PlaybackRequestId!, _options.RequestDeduplicationSeconds);
            foreach (var window in _displays.Windows)
                try { window.StopVideoPlayback(request.Source, request.PlaybackRequestId!); }
                catch (InvalidOperationException ex) { Telemetry.Warning("VideoStopOutputBlocked", new { window.DisplayName, request.PlaybackRequestId, Reason = ex.Message }, request.RequestId); }
            Telemetry.Info("VideoStopRequested", new { request.Source, request.PlaybackRequestId }, request.RequestId);
            return;
        }
        if (request.IsFade) { HandleVideoFade(request); return; }
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
            if (request.HasRoles && (video ? outputs!.Count == 0 : !_inventoryAudio.Any(device =>
                PhotoBooth.Networking.RoleList.Matches(AudioDeviceOptions.RolesFor(device, _options), request.Roles) && (request.Channel is null || request.Channel <= device.Channels))))
            {
                Telemetry.Info("RolePlaybackNoMatchingOutputs", new { request.Experience, request.Roles, request.Value }, request.RequestId);
                return;
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
                var available = outputs.Where(output => _displays.Windows.Any(window => window.DisplayName.Equals(output.Display, StringComparison.OrdinalIgnoreCase))).ToArray();
                var priorities = _videoPriority.TryBeginBatch(request.EffectivePriority, available.Select(output =>
                    VideoPlaybackIdentity.For(path, output.Display, request with { ScreenId = output.Screen.Id })).ToArray(), out var blockingPriorities);
                for (var index = 0; index < available.Length; index++)
                {
                    var output = available[index];
                    var window = _displays.Windows.First(w => w.DisplayName.Equals(output.Display, StringComparison.OrdinalIgnoreCase));
                    var targeted = request with { ScreenId = output.Screen.Id, ScreenName = output.Screen.Name, PriorityCoordinator = _videoPriority,
                        BlockingPriority = blockingPriorities[index], PriorityRejected = _priorityEvents.Report };
                    var priority = priorities[index];
                    // Report the known conflict before enqueueing renderer work. A frozen
                    // renderer must not suppress notices for remembered background loops.
                    if (priority is null && blockingPriorities[index] is { } blockedBy)
                        _priorityEvents.Report(targeted, output.Display, output.Screen.Name, blockedBy);
                    if (priority is null && !request.Loop)
                    {
                        Telemetry.Info("VideoRequestSkippedLowerPriority", new { request.Value, DisplayName = output.Display, ScreenName = output.Screen.Name, Priority = request.EffectivePriority }, request.RequestId);
                        continue;
                    }
                    try
                    {
                        window.PlayVideo(path, targeted with { VideoPriority = priority });
                        Telemetry.Info("VideoOutputSelected", new { RequestedScreen = request.Screen, RequestedDisplayName = request.DisplayName, DisplayName = output.Display, ScreenName = output.Screen.Name }, request.RequestId);
                    }
                    catch (InvalidOperationException ex)
                    {
                        priority?.Dispose();
                        Telemetry.Warning("VideoOutputBlocked", new { DisplayName = output.Display, ScreenName = output.Screen.Name, Reason = ex.Message }, request.RequestId);
                    }
                    catch
                    {
                        for (var pending = index; pending < priorities.Length; pending++) priorities[pending]?.Dispose();
                        throw;
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
        await _announcements.DisposeAsync();
        await _priorityEvents.DisposeAsync();
        await _localController.DisposeAsync();
        await _screenMedia.DisposeAsync();
        foreach (var window in _displays.Windows) await window.FinishFramingAsync();
        await _configuration.DisposeAsync();
        await _calibration.DisposeAsync();
        await _tasks.DisposeAsync();
        _audio.Dispose();
        await _displays.DisposeAsync();
    }
}
