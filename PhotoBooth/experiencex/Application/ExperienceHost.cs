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
    private AudioOutput[] _inventoryAudio = [];
    private string? _defaultAudioId;
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
    private bool _polling, _disposed, _togglingBackground, _togglingTransparency, _closeRequested;
    private string? _deviceFailure;
    private bool _addScreenArmed, _editingText, _addingScreen;
    private bool _creatingPerspective;
    private readonly Queue<(string Display, string ScreenId, int Direction)> _brightnessChanges = new();
    private bool _adjustingBrightness;
    private Task _matCommands = Task.CompletedTask;

    private void PressMat()
    {
        CancelBackgroundChoice();
        var create = _addScreenArmed;
        _addScreenArmed = false;
        var previous = _matCommands;
        // A rapid A,M,M must finish creating/selecting the mat before toggling it.
        var operation = _matCommands = RunMatCommandAsync(previous, create, _tasks.Token);
        _tasks.Run(_ => operation, "Create or toggle mat");
    }
    private async Task RunMatCommandAsync(Task previous, bool create, CancellationToken token)
    {
        try
        {
            await previous;
            token.ThrowIfCancellationRequested();
            if (_disposed || !_options.ConfigurationMode || _editingText) return;
            if (create) { await CreateScreenAsync(token, "mat"); return; }
            if (_addingScreen || _calibration.SelectedScreen is not ScreenSurface selected ||
                !_options.ScreensFor(selected.DisplayName).Any(s => s.Id == selected.ScreenId && s.IsMat))
            {
                _modeNotice.Display("Select a mat first   •   M: normal / negative");
                return;
            }
            var json = await _configuration.Store.ToggleMatNegativeAsync(selected.DisplayName, selected.ScreenId, token);
            await _configuration.ReloadAsync();
            if (_disposed) return;
            var expected = ExperienceOptions.Parse(json).ScreensFor(selected.DisplayName).Single(s => s.Id == selected.ScreenId);
            var applied = _options.ScreensFor(selected.DisplayName).FirstOrDefault(s => s.Id == selected.ScreenId);
            if (applied?.IsNegative != expected.IsNegative)
                throw new InvalidOperationException("The mat setting could not be applied or changed again remotely.");
            Telemetry.Info("MatNegativeToggled", new { selected.DisplayName, selected.ScreenId, ScreenName = applied.Name, applied.IsNegative, Shortcut = "M" });
            _modeNotice.Display($"{applied.Name}: {(applied.IsNegative ? "negative — black outside" : "normal — black inside")}   •   M", display: selected.DisplayName);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) when (_disposed) { }
        catch (Exception ex) { Telemetry.Error("MatNegativeToggleFailed", ex); _modeNotice.Display(ex.Message, true); }
    }
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
                using var dialog = new VideoScreenDialog(_options.ScreensFor(window.DisplayName), _definitions.Perspectives, VideoScreenDialog.KnownRoles(_options), label);
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
    private async Task<bool> RenameScreenAsync(ScreenSurface surface, string oldName, string name, CancellationToken token, bool browsing = false)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (!browsing && name == oldName) return true;
            var definition = _options.ScreensFor(surface.DisplayName).FirstOrDefault(s => s.Id == surface.ScreenId);
            var binding = definition?.IsVideo == true ? _screenMedia.Current.Match(new() { Name = name }) : null;
            var mediaFile = binding?.Path is { } mediaPath ? Path.GetFileName(mediaPath) : null;
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
    internal nint HotkeyWindowHandle => _hotkeys.Handle;
    internal int[] VisualizationProcessIds => _displays.VisualizationProcessIds;
    internal IEnumerable<MainWindow> DisplayWindows => _displays.Windows;
    internal IPointSurface? SelectedScreen => _calibration.SelectedScreen;

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
        _mediaDiscovery = new(() => _options);
        _definitions = new(dispatcher, async definitions => { await _configuration.Store.ApplyPerspectivesAsync(definitions); await _configuration.ReloadAsync(); });
        _listener = new(dispatcher, () => _options, HandleRequest, message => { _mediaDiscovery.Receive(message); _definitions.Receive(message); }, () => _announcements.RequestAnnouncement());
        _configurationShortcut = new("Experience", () => _options.ConfigurationMode, enabled => _tasks.Run(token => SetSharedConfigurationAsync(enabled, token), "Set shared configuration"), FocusExperience);
        _hotkeys = new(() => _configurationShortcut.SwitchTo("Controller"), () =>
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
        }, hotkeysFocused);
        _configuration.Changing += next => { if (next.UdpCommandsPort != _options.UdpCommandsPort || next.UdpEventsPort != _options.UdpEventsPort) _listener.Bind(next.UdpCommandsPort, next.UdpEventsPort); };
        _configuration.Changed += ApplyConfiguration;
        _hotkeys.AddScreenPrefix = () => { CancelBackgroundChoice(); _addScreenArmed = true; _modeNotice.Display("Add screen   •   V: video   M: mat   G: visualization   L: LEDs"); };
        _hotkeys.EditScreenName = () =>
        {
            CancelBackgroundChoice(); _addScreenArmed = false;
            if (_calibration.SelectedScreen is ScreenSurface selected)
                _displays.Windows.FirstOrDefault(window => window.DisplayName == selected.DisplayName)?.EditScreenName(selected);
            else _modeNotice.Display("Select a screen first   •   N: edit name");
        };
        _hotkeys.ToggleScreenTransparency = () => { CancelBackgroundChoice(); _tasks.Run(ToggleScreenTransparencyAsync, "Toggle screen chroma key"); };
        _hotkeys.CreatePerspective = () => { CancelBackgroundChoice(); _addScreenArmed = false; _tasks.Run(CreatePerspectiveAsync, "Create screen perspective"); };
        _hotkeys.AdjustScreenBrightness = PressScreenBrightness;
        _hotkeys.ChooseWhiteBackground = () => ApplyBackgroundChoice(_backgroundSelection.PressW());
        _hotkeys.AddVideoScreen = () => { if (_addScreenArmed) { _addScreenArmed = false; _tasks.Run(token => CreateScreenAsync(token, "video"), "Create video screen"); } };
        _hotkeys.ChoosePerspective = () => _tasks.Run(ChoosePerspectiveAsync, "Choose screen perspective");
        _hotkeys.AddVisualizationScreen = () => { if (_addScreenArmed) { _addScreenArmed = false; _tasks.Run(token => CreateScreenAsync(token, "visualization"), "Create visualization screen"); } };
        _hotkeys.AddMatScreen = PressMat;
        _hotkeys.AddLedScreen = () => { if (_addScreenArmed) { _addScreenArmed = false; _tasks.Run(token => CreateScreenAsync(token, "fastled"), "Create LED screen"); } };
        _displays.TextEditingChanged += editing => { if (_disposed) return; _editingText = editing; _addScreenArmed = false; CancelBackgroundChoice(); UpdateHotkeys(_options.ConfigurationMode); };
        _displays.RenameRequested += (surface, oldName, name) => _tasks.Run(token => RenameScreenAsync(surface, oldName, name, token), "Rename screen");
        _displays.BrowseRenameRequested = (surface, oldName, name) => RenameScreenAsync(surface, oldName, name, _tasks.Token, browsing: true);
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
        PublishInventory(); _announcements.Start();
        _configuration.StartWatching();
        UpdateHotkeys(_options.ConfigurationMode);
        _idleCursor.Start();
        _audioPoll.Start();
        _tasks.Run(PollAudioAsync, "Initial audio inventory");
    }
    private void FocusExperience()
    {
        if (_disposed) return;
        var window = _displays.Select(_calibration.SelectedScreen?.DisplayName);
        window?.Activate(); window?.Focus();
    }
    private async Task SetSharedConfigurationAsync(bool enabled, CancellationToken token)
    {
        await _configuration.Store.SetConfigurationModeAsync(enabled, token);
        await _configuration.ReloadAsync();
        if (enabled && !PhotoBooth.Windows.ConfigurationShortcut.ControllerRunning) FocusExperience();
        _modeNotice.Display(enabled ? "Configuration ON • Ctrl+Shift+Alt+C: exit • C: Controller • A: add • N: name" : "Configuration OFF");
    }
    private void UpdateHotkeys(bool configurationMode)
    {
        var unavailable = _hotkeys.Register(configurationMode, _editingText, _backgroundSelection.PendingDisplay is not null);
        if (unavailable.Length > 0)
            _modeNotice.Display($"{string.Join(", ", unavailable)} unavailable — check the log", true);
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
    private async Task CreatePerspectiveAsync(CancellationToken token)
    {
        if (_disposed || !_options.ConfigurationMode || _editingText || _creatingPerspective) return;
        if (_calibration.SelectedScreen is not ScreenSurface selected || !_options.ScreensFor(selected.DisplayName).Any(s=>s.Id==selected.ScreenId && s.IsVideo))
        { _modeNotice.Display("Select a video/image screen first • F: create perspective"); return; }
        _creatingPerspective=true;
        try
        {
            var window=_displays.Windows.First(w=>w.DisplayName==selected.DisplayName);
            var initial=_options.ScreensFor(selected.DisplayName).Single(s=>s.Id==selected.ScreenId);
            var controllers=_definitions.AvailableControllers;
            PhotoBooth.Networking.ControllerSharedDefinitions? controller;
            if(initial.Perspective is { } current) controller=controllers.FirstOrDefault(c=>c.ControllerId==current.ControllerId);
            else if(controllers.Length==1) controller=controllers[0];
            else if(controllers.Length>1)
            {
                using var picker=new ControllerPicker(controllers);
                _editingText=true;UpdateHotkeys(true);
                try { if(picker.ShowDialog(window)!=System.Windows.Forms.DialogResult.OK)return; controller=picker.Selection; }
                finally { _editingText=false;UpdateHotkeys(_options.ConfigurationMode); }
            }
            else controller=null;
            if(controller is null)throw new InvalidOperationException(initial.Perspective is null ? "No Controller is available. Start a Controller and try F again." : "The Controller that owns this perspective is unavailable. Try again when it reconnects.");
            if(!_options.ConfigurationMode)return;
            var display=System.Windows.Forms.Screen.AllScreens.FirstOrDefault(d=>d.DeviceName==selected.DisplayName) ?? throw new InvalidOperationException("The selected display is unavailable.");
            await _calibration.FinishAsync(selected); await _configuration.ReloadAsync();
            var screen=_options.ScreensFor(selected.DisplayName).Single(s=>s.Id==selected.ScreenId);
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
                if(Environment.TickCount64>=deadline)throw new TimeoutException("Controller save was not confirmed by its definitions. Check connectivity and try F again.");
                await Task.Delay(100,token);
            }
            if(_disposed || !_options.ConfigurationMode)return;
            var latest=_options.ScreensFor(selected.DisplayName).FirstOrDefault(s=>s.Id==selected.ScreenId);
            if(latest is null || latest.Perspective!=screen.Perspective)throw new InvalidOperationException("The screen's perspective changed while saving. The new perspective remains available on the Controller.");
            await _configuration.Store.SetPerspectiveAsync(selected.DisplayName,selected.ScreenId,saved);
            await _configuration.ReloadAsync();
            Telemetry.Info("ScreenPerspectiveCreated",new { selected.DisplayName, selected.ScreenId, saved.ControllerId,saved.PerspectiveId,saved.Name,saved.Rectangle });
            _modeNotice.Display($"{screen.Name}: {saved.Name} saved • F",display:selected.DisplayName);
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
        _mediaDiscovery.Configure(next);
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
                await _configuration.Store.EnsureAudioDevicesAsync(inventory.Devices, token);
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
                var priorities = _videoPriority.TryBeginBatch(request.IsAmbient, available.Select(output =>
                    VideoLoopIdentity.For(path, output.Display, request with { ScreenId = output.Screen.Id })).ToArray());
                for (var index = 0; index < available.Length; index++)
                {
                    var output = available[index];
                    var window = _displays.Windows.First(w => w.DisplayName.Equals(output.Display, StringComparison.OrdinalIgnoreCase));
                    var targeted = request with { ScreenId = output.Screen.Id, ScreenName = output.Screen.Name };
                    var priority = priorities?[index];
                    if (priority is null)
                    {
                        Telemetry.Info("AmbientVideoRequestSkipped", new { request.Value, DisplayName = output.Display, ScreenName = output.Screen.Name, Reason = "NormalVideoActive" }, request.RequestId);
                        continue;
                    }
                    try
                    {
                        window.PlayVideo(path, targeted with { VideoPriority = priority });
                        Telemetry.Info("VideoOutputSelected", new { RequestedScreen = request.Screen, RequestedDisplayName = request.DisplayName, DisplayName = output.Display, ScreenName = output.Screen.Name }, request.RequestId);
                    }
                    catch (InvalidOperationException ex)
                    {
                        priority.Dispose();
                        Telemetry.Warning("VideoOutputBlocked", new { DisplayName = output.Display, ScreenName = output.Screen.Name, Reason = ex.Message }, request.RequestId);
                    }
                    catch
                    {
                        for (var pending = index; pending < priorities!.Length; pending++) priorities[pending].Dispose();
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
