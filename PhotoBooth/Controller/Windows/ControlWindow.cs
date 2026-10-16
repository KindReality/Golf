using PhotoBooth.Networking;
using System.Diagnostics;
using System.IO.Ports;
using Controller.Application;
using Controller.Diagnostics;
using Controller.Configuration;

namespace Controller.Windows;

internal sealed class ControlWindow : Form
{
    private readonly string _path;
    private readonly Icon _applicationIcon;
    private readonly string? _templateDirectory;
    private readonly Label _identity = new() { AutoSize = true, Text = "Starting Controller…", Dock = DockStyle.Fill };
    private readonly ComboBox _playlists = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
    private readonly ComboBox _scenes = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
    private readonly Button _activate = new() { Text = "Activate scene", AutoSize = true, Enabled = false };
    private readonly Label _sceneStatus = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Label _connectionStatus = new() { Text = "CONNECTING", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight };
    private readonly Button _run = new() { Text = "Run playlist", AutoSize = true, Enabled = false };
    private readonly Button _cancel = new() { Text = "Cancel selected", AutoSize = true, Enabled = false };
    private readonly ListView _hardware = List("Device", "Port", "Status");
    private readonly ListView _running = List("Playlist", "Owner", "Elapsed ms", "Remaining");
    private readonly ListView _activity = List("Time", "Level", "Activity");
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 250 };
    private ControllerHost? _host;
    private readonly PhotoBooth.Windows.ConfigurationShortcut _configurationShortcut;
    private ToolbarIconButton _configurationButton = null!, _backButton = null!;
    private Button _editorButton = null!;
    private PerspectiveEditor? _editor;
    private LivePage? _live;
    private ShowEditorPage? _showEditor;
    private Button _showButton = null!;
    private LedPerspectiveEditor? _ledEditor;
    private readonly Panel _pages = new() { Dock = DockStyle.Fill };
    private Control _dashboard = null!;
    private Button _liveButton = null!;
    private PowerToolbarButton _powerButton = null!;
    private readonly ToolTip _toolbarTips = new();
    private PhotoBooth.Updates.BuildUpdateMonitor? _updates;
    private ExperienceProcessMonitor? _experience;
    private ActivityEntry? _latest;
    private bool _closing, _closed;
    private bool _allowExit, _shutdownBoth, _restarting;
    private long _nextDisplayCheck;
    private string? _displayTarget, _unavailableDisplay;
    private (string Name, Rectangle Bounds)? _displayPlacement;

    public ControlWindow(string path, string? templateDirectory = null)
    {
        _path = path;
        _configurationShortcut = new("Controller", () => _host?.Options.ConfigurationMode == true, enabled => { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => SetSharedConfiguration(enabled))); }, () => { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(FocusEditor)); }, () => { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(ExitController)); });
        _templateDirectory = templateDirectory;
        using (var icon = typeof(ControlWindow).Assembly.GetManifestResourceStream("Controller.Assets.Application.ico")
            ?? throw new InvalidOperationException("The Controller icon is missing."))
            _applicationIcon = new Icon(icon);
        Icon = _applicationIcon;
        ShowInTaskbar = true;
        Text = "Home Technologies • Controller";
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        BackColor = DarkTheme.Background;
        ForeColor = DarkTheme.Text;
        ClientSize = new Size(1060, 760);
        MinimumSize = new Size(980, 660);
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Normal;
        // Keep startup invisible until the configured display has been resolved.
        Opacity = 0;
        _identity.ForeColor = DarkTheme.Muted;
        _identity.Font = new Font("Segoe UI", 9);
        _sceneStatus.ForeColor = DarkTheme.Accent;
        _connectionStatus.ForeColor = DarkTheme.Accent;
        _connectionStatus.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        DarkTheme.Choices(_playlists);
        DarkTheme.Choices(_scenes);
        DarkTheme.Button(_activate, true);
        DarkTheme.Button(_run, true);
        DarkTheme.Button(_cancel);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 12), ColumnCount = 2, RowCount = 4, BackColor = DarkTheme.Background };
        layout.ColumnStyles.Add(new(SizeType.Percent, 42));
        layout.ColumnStyles.Add(new(SizeType.Percent, 58));
        layout.RowStyles.Add(new(SizeType.Absolute, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 152));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 30));
        var configuration = _configurationButton = new ToolbarIconButton(ToolbarGlyph.Configure, "Enter configuration mode");
        configuration.Click += (_, _) => _configurationShortcut.SetBoth(_host?.Options.ConfigurationMode != true);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = Padding.Empty, Padding = new Padding(24, 8, 24, 0) };
        header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.Absolute, 190));
        header.RowStyles.Add(new(SizeType.Absolute, 46)); header.RowStyles.Add(new(SizeType.Absolute, 22)); header.RowStyles.Add(new(SizeType.Absolute, 48));
        header.Controls.Add(new Label { Text = "Controller", Font = new Font("Segoe UI", 25, FontStyle.Bold), Dock = DockStyle.Fill, AutoSize = true, Margin = Padding.Empty }, 0, 0);
        header.Controls.Add(_connectionStatus, 1, 0);
        header.Controls.Add(_identity, 0, 1);
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = false, FlowDirection = FlowDirection.RightToLeft };
        Button PageButton(string text)
        {
            var button = new Button { Text = text, AutoSize = false, Size = new(140, 40), Margin = new(3, 2, 3, 2), Enabled = false };
            DarkTheme.Button(button); return button;
        }
        _editorButton = PageButton("Perspectives"); _liveButton = PageButton("Live"); _showButton = PageButton("Scenes");
        _backButton = new(ToolbarGlyph.Return, "Return to dashboard") { Visible = false };
        var hide = new ToolbarIconButton(ToolbarGlyph.Minimize, "Minimize Controller"); hide.Click += (_, _) => Close();
        _powerButton = new(RestartBoth, ShutdownBoth);
        navigation.Controls.AddRange([_powerButton, hide, configuration, _editorButton, _showButton, _liveButton, _backButton]);
        _toolbarTips.SetToolTip(hide, "Minimize Controller to the taskbar");
        _toolbarTips.SetToolTip(_backButton, "Return to dashboard");
        _toolbarTips.SetToolTip(configuration, "Enter configuration mode (Ctrl+Shift+C)");
        _editorButton.Click += (_, _) => OpenPerspectives(); _liveButton.Click += (_, _) => OpenLive(); _backButton.Click += (_, _) => ShowDashboard();
        _showButton.Click += (_, _) => OpenScenes();
        header.Controls.Add(navigation, 1, 2);
        header.SetColumnSpan(navigation, 2); header.SetColumn(navigation, 0);
        layout.RowStyles[0].Height = 0;
        layout.Controls.Add(CommandCard("SCENE CONTROL", _scenes, _activate, _sceneStatus, null, new Padding(0, 0, 8, 12)), 0, 1);
        layout.Controls.Add(CommandCard("STORYBOARD PLAYBACK", _playlists, _run, DarkTheme.Caption("Select an active run below to cancel it."), _cancel, new Padding(8, 0, 0, 12)), 1, 1);
        var devices = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        devices.RowStyles.Add(new(SizeType.Percent, 50)); devices.RowStyles.Add(new(SizeType.Percent, 50));
        devices.Controls.Add(ListCard("HARDWARE", "Arduino devices and available serial ports", _hardware, new Padding(0, 0, 8, 12)), 0, 0);
        devices.Controls.Add(ListCard("RUNNING PLAYLISTS", "Live execution across scenes and triggers", _running, new Padding(0, 0, 8, 0)), 0, 1);
        layout.Controls.Add(devices, 0, 2);
        layout.Controls.Add(ListCard("ACTIVITY FEED", "Events, commands and scene transitions", _activity, new Padding(8, 0, 0, 0)), 1, 2);
        var footer = DarkTheme.Caption("HOME TECHNOLOGIES  /  CONTROL SYSTEMS"); footer.Font = new Font("Segoe UI", 8);
        layout.Controls.Add(footer, 0, 3); layout.SetColumnSpan(footer, 2);
        _dashboard = layout; _pages.Controls.Add(layout);
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        shell.RowStyles.Add(new(SizeType.Absolute, 128)); shell.RowStyles.Add(new(SizeType.Percent, 100));
        shell.Controls.Add(header, 0, 0); shell.Controls.Add(_pages, 0, 1); Controls.Add(shell);
        _run.Click += (_, _) =>
        {
            try { if (_playlists.SelectedItem is string name) _host?.Engine.RunManually(name); }
            catch (Exception ex) { _host?.Log.Error("ManualRunFailed", ex); }
        };
        _activate.Click += (_, _) =>
        {
            try { if (_scenes.SelectedItem is string name) _host?.Scenes.Request(name); }
            catch (Exception ex) { _host?.Log.Error("ManualSceneFailed", ex); }
        };
        _cancel.Click += (_, _) =>
        {
            if (_running.SelectedItems.Count == 1 && _running.SelectedItems[0].Tag is string id) _host?.Engine.Cancel(id);
        };
        _running.SelectedIndexChanged += (_, _) => _cancel.Enabled = !_closing && _running.SelectedItems.Count == 1;
        _refresh.Tick += (_, _) => RefreshState();
        Shown += StartAsync;
        FormClosing += StopAsync;
    }

    private static DarkCard CommandCard(string heading, ComboBox choices, Button action, Control status, Button? secondary, Padding margin)
    {
        var card = new DarkCard { Margin = margin };
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = Padding.Empty, Padding = new Padding(24, 8, 24, 0), BackColor = DarkTheme.Surface };
        content.ColumnStyles.Add(new(SizeType.Percent, 100)); content.ColumnStyles.Add(new(SizeType.AutoSize));
        content.RowStyles.Add(new(SizeType.Absolute, 26)); content.RowStyles.Add(new(SizeType.Absolute, 42)); content.RowStyles.Add(new(SizeType.Percent, 100));
        var caption = DarkTheme.Caption(heading); content.Controls.Add(caption, 0, 0); content.SetColumnSpan(caption, 2);
        choices.Dock = DockStyle.Fill; choices.Margin = new Padding(0, 3, 12, 0);
        action.Margin = new Padding(0, 0, 0, 4);
        content.Controls.Add(choices, 0, 1); content.Controls.Add(action, 1, 1);
        content.Controls.Add(status, 0, 2);
        if (secondary is not null) { secondary.Margin = new Padding(0, 2, 0, 0); content.Controls.Add(secondary, 1, 2); }
        else content.SetColumnSpan(status, 2);
        card.Controls.Add(content); return card;
    }
    private static DarkCard ListCard(string title, string subtitle, ListView list, Padding margin)
    {
        var card = new DarkCard { Margin = margin };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, BackColor = DarkTheme.Surface };
        layout.RowStyles.Add(new(SizeType.Absolute, 22)); layout.RowStyles.Add(new(SizeType.Absolute, 24)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(DarkTheme.Caption(title), 0, 0);
        var description = DarkTheme.Caption(subtitle); description.Font = new Font("Segoe UI", 9);
        layout.Controls.Add(description, 0, 1); layout.Controls.Add(list, 0, 2);
        card.Controls.Add(layout); return card;
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        PhotoBooth.Appearance.DarkWindowChrome.Apply(Handle);
    }

    private async void StartAsync(object? sender, EventArgs e)
    {
        try
        {
            _host = await Task.Run(() => new ControllerHost(_path, _templateDirectory));
            if (_closing) { await _host.DisposeAsync(); _host = null; _closed = true; Close(); return; }
            if (_templateDirectory is not null)
            {
                _updates = new PhotoBooth.Updates.BuildUpdateMonitor("Controller", _templateDirectory);
                _updates.ManualRestartFailed += reason => { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => { _restarting = false; _host?.Log.Warning("ControllerRestartFailed", reason); RefreshState(); })); };
            }
            var controllerSource = _templateDirectory ?? Path.GetDirectoryName(PhotoBooth.Configuration.MachineConfigurationFiles.Resolve("Controller"))!;
            var experienceSource = Path.Combine(Path.GetDirectoryName(controllerSource)!, "ExperienceX");
            _experience = new(experienceSource, () => !_closing && _host.Options.ExperienceSupervisionEnabled, _host.Log);
            _refresh.Start();
            RefreshState();
            Opacity = 1;
        }
        catch (Exception ex)
        {
            TelemetryError(ex);
            Opacity = 1;
            MessageBox.Show(this, $"Controller could not start:\n{ex.Message}\n\nConfiguration: {_path}", "Controller", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _closed = true;
            Close();
        }
    }

    private void RefreshState()
    {
        if (_host is null || _closing) return;
        var options = _host.Options;
        var configurationGlyph = options.ConfigurationMode ? ToolbarGlyph.Play : ToolbarGlyph.Configure;
        if (_configurationButton.Glyph != configurationGlyph)
        {
            _configurationButton.Glyph = configurationGlyph;
            _configurationButton.AccessibleName = options.ConfigurationMode ? "Exit configuration mode" : "Enter configuration mode";
            _toolbarTips.SetToolTip(_configurationButton, _configurationButton.AccessibleName + " (Ctrl+Shift+C)");
        }
        _powerButton.RestartEnabled = _updates is not null && !_restarting;
        _powerButton.ShutdownEnabled = !_restarting;
        _editorButton.Enabled = options.ConfigurationMode; _liveButton.Enabled = true;
        _showButton.Enabled = options.ConfigurationMode;
        if (!options.ConfigurationMode && (_editor is not null || _showEditor?.Visible == true)) ShowDashboard();
        ApplyDisplayPlacement(options);
        _connectionStatus.Text = "●  NETWORK ACTIVE";
        _identity.Text = $"{options.EffectiveDeviceName} • {options.Build.Environment} • Events {options.UdpEventsPort} • Commands {options.UdpCommandsPort}";
        SetChoices(_playlists, options.Playlists.Where(p => p.Type == "storyboard").Select(p => p.Name).ToArray());
        SetChoices(_scenes, options.Scenes.Select(s => s.Name).ToArray());
        var scene = _host.Scenes.Snapshot();
        _sceneStatus.Text = $"Scene: {scene.Active ?? "none"} • {scene.Phase}" + (scene.Destination is { } destination ? $" → {destination}" : "");
        _run.Enabled = _playlists.SelectedItem is not null;
        _activate.Enabled = _scenes.SelectedItem is not null;
        _hardware.BeginUpdate();
        _hardware.Items.Clear();
        var statuses = _host.ArduinoStatus;
        foreach (var device in options.Arduinos)
        {
            var status = statuses.FirstOrDefault(s => s.Name.Equals(device.Name, StringComparison.OrdinalIgnoreCase));
            _hardware.Items.Add(new ListViewItem([device.Name, device.Port, device.Enabled ? status?.Status ?? "Connecting" : "Disabled"]));
        }
        try
        {
            foreach (var port in SerialPort.GetPortNames().Where(p => !options.Arduinos.Any(a => a.Port.Equals(p, StringComparison.OrdinalIgnoreCase))))
                _hardware.Items.Add(new ListViewItem(["Unassigned", port, "Configure an Arduino alias to connect"]));
        }
        catch (Exception ex) { _host.Log.Error("SerialInventoryFailed", ex); }
        if (_hardware.Items.Count == 0) _hardware.Items.Add(new ListViewItem(["No serial devices", "", "UDP remains active"]));
        _hardware.EndUpdate();
        var selected = _running.SelectedItems.Count == 1 ? _running.SelectedItems[0].Tag : null;
        _running.BeginUpdate();
        _running.Items.Clear();
        foreach (var run in _host.Engine.Snapshot())
        {
            var item = new ListViewItem([run.Playlist, run.Trigger ?? "Manual", run.ElapsedMilliseconds.ToString("F0"), run.RemainingItems.ToString()]) { Tag = run.Id };
            _running.Items.Add(item);
            if (Equals(selected, run.Id)) item.Selected = true;
        }
        _running.EndUpdate();
        _cancel.Enabled = _running.SelectedItems.Count == 1;
        var activity = _host.Log.Snapshot();
        if (activity.FirstOrDefault() != _latest)
        {
            _latest = activity.FirstOrDefault();
            _activity.BeginUpdate();
            _activity.Items.Clear();
            foreach (var entry in activity)
                _activity.Items.Add(new ListViewItem([entry.At.ToString("HH:mm:ss.fff"), entry.Level, entry.Message]));
            _activity.EndUpdate();
        }
    }

    private void ApplyDisplayPlacement(ControllerOptions options)
    {
        var target = ControllerDisplaySelection.Target(options);
        var now = Environment.TickCount64;
        if (string.Equals(_displayTarget, target, StringComparison.OrdinalIgnoreCase) && now < _nextDisplayCheck) return;
        _displayTarget = target;
        _nextDisplayCheck = now + 1000;
        var displays = Screen.AllScreens;
        var selectedName = ControllerDisplaySelection.Select(options, displays.Select(display => display.DeviceName).ToArray(), Screen.PrimaryScreen?.DeviceName);
        var selected = displays.FirstOrDefault(display => display.DeviceName == selectedName);
        if (selected is null) return;
        var missing = target is not null && !displays.Any(display => string.Equals(display.DeviceName, target, StringComparison.OrdinalIgnoreCase));
        if (missing && !string.Equals(_unavailableDisplay, target, StringComparison.OrdinalIgnoreCase))
            _host!.Log.Warning("ControllerDisplayUnavailable", $"Display {target} unavailable; using {selected.DeviceName}", new { RequestedDisplay = options.DefaultDisplayName, ResolvedDisplay = target, FallbackDisplay = selected.DeviceName });
        _unavailableDisplay = missing ? target : null;
        var placement = (selected.DeviceName, selected.Bounds);
        if (_displayPlacement == placement) return;
        _displayPlacement = placement;
        WindowState = FormWindowState.Normal;
        MinimumSize = new Size(Math.Min(980, selected.Bounds.Width), Math.Min(660, selected.Bounds.Height));
        Bounds = selected.Bounds;
        _host!.Log.Info("ControllerWindowPositioned", $"Controller window on {selected.DeviceName}", new { RequestedDisplay = options.DefaultDisplayName, Display = selected.DeviceName, Bounds = new { Left, Top, Width, Height }, WindowState = WindowState.ToString() });
    }

    private async void StopAsync(object? sender, FormClosingEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (!_allowExit && e.CloseReason is CloseReason.UserClosing or CloseReason.TaskManagerClosing)
        {
            WindowState = FormWindowState.Minimized;
            _host?.Log.Info("ControllerMinimized", "Controller continues running in the taskbar");
            return;
        }
        if (_closing) return;
        _closing = true;
        _run.Enabled = _cancel.Enabled = _activate.Enabled = false;
        _powerButton.RestartEnabled = _powerButton.ShutdownEnabled = false;
        _refresh.Stop();
        if (_host is null) return; // Startup owns the eventual close if it is still in progress.
        try
        {
            if (_experience is not null) await _experience.DisposeAsync();
            if (_updates is not null) await _updates.StopAsync(_shutdownBoth);
            await _host.DisposeAsync();
            if (_shutdownBoth) await ExperienceProcessMonitor.StopExperienceAsync(_host.Log);
        }
        catch (Exception ex) { TelemetryError(ex); }
        _closed = true;
        Close();
    }

    internal void RestoreFromTaskbar()
    {
        if (_closing || _closed) return;
        Show(); WindowState = FormWindowState.Normal;
        _displayPlacement = null; _nextDisplayCheck = 0;
        if (_host is not null) ApplyDisplayPlacement(_host.Options);
        else if (Screen.PrimaryScreen is { } display) Bounds = display.Bounds;
        Activate(); Focus();
        _host?.Log.Info("ControllerRestored", "Controller restored from the taskbar");
    }
    private void RestartBoth()
    {
        if (_closing || _restarting || _updates is null) return;
        _restarting = true; RefreshState();
        _host?.Log.Info("ApplicationsRestartRequested", "Restarting Controller and Experience on this computer");
        _updates.RequestRestart(restartExperience: true);
    }
    internal void ShutdownBoth()
    {
        if (_closing || _closed || _restarting) return;
        _shutdownBoth = true;
        _host?.Log.Info("ApplicationsShutdownRequested", "Shutting down Controller and Experience on this computer");
        ExitController();
    }
    internal void ExitController()
    {
        _host?.Log.Info("ControllerExitRequested", "Closing Controller", new { ShutdownBoth = _shutdownBoth });
        _allowExit = true; Close();
    }

    private static void TelemetryError(Exception ex) => PhotoBooth.Diagnostics.Telemetry.Error("ControllerFailed", ex);
    private static void SetChoices(ComboBox box, string[] names)
    {
        if (box.Items.Cast<string>().SequenceEqual(names)) return;
        var previous = box.SelectedItem; box.Items.Clear(); box.Items.AddRange(names);
        if (previous is not null && box.Items.Contains(previous)) box.SelectedItem = previous;
        else if (names.Length > 0) box.SelectedIndex = 0;
    }
    private async void SetSharedConfiguration(bool enabled)
    {
        if (_host is null || _closing) return;
        try
        {
            if (enabled && !PhotoBooth.Windows.ConfigurationShortcut.ExperienceRunning) FocusEditor();
            await _host.SetConfigurationModeAsync(enabled);
            if (!_closing && enabled && !PhotoBooth.Windows.ConfigurationShortcut.ExperienceRunning) FocusEditor();
        }
        catch (Exception ex) { _host.Log.Error("ConfigurationModeFailed", ex); }
    }
    private void FocusEditor()
    {
        if (_closing) return;
        if (WindowState == FormWindowState.Minimized) { RestoreFromTaskbar(); return; }
        Activate(); Focus();
    }
    private void CloseEditors()
    {
        var editor = _editor; _editor = null;
        _ledEditor?.Close(); _ledEditor?.Dispose(); _ledEditor = null;
        editor?.Close(); editor?.Dispose();
    }
    internal void ShowDashboard()
    { CloseEditors(); _showEditor?.Hide(); if (_live is not null) _live.Hide(); _dashboard.Show(); _dashboard.BringToFront(); _backButton.Visible = false; }
    internal void OpenScenes()
    {
        if (_host?.Options.ConfigurationMode != true) return;
        CloseEditors(); _dashboard.Hide(); _live?.Hide(); _showEditor ??= new(_host);
        if (_showEditor.Parent is null) _pages.Controls.Add(_showEditor);
        _showEditor.Show(); _showEditor.BringToFront(); _showEditor.RefreshState(); _backButton.Visible = true;
    }
    internal void OpenLive()
    {
        if (_host is null) return;
        CloseEditors(); _showEditor?.Hide(); _dashboard.Hide(); _live ??= new(_host);
        if (_live.Parent is null) _pages.Controls.Add(_live);
        _live.Show(); _live.BringToFront(); _live.ActivatePage(); _backButton.Visible = true;
    }
    internal void OpenPerspectives()
    {
        if (_host?.Options.ConfigurationMode != true) return;
        CloseEditors(); _showEditor?.Hide(); _dashboard.Hide(); _live?.Hide();
        _editor = new(_host, OpenLed, () => _configurationShortcut.SwitchTo("Experience")) { TopLevel = false, FormBorderStyle = FormBorderStyle.None, Dock = DockStyle.Fill, MinimumSize = Size.Empty };
        _pages.Controls.Add(_editor); _editor.Show(); _editor.BringToFront(); _backButton.Visible = true;
    }
    private void OpenLed(PerspectiveDefinition? original, Action<PerspectiveDefinition> saved)
    {
        if (_editor is null) return;
        _editor.Hide();
        _ledEditor = new(original) { TopLevel = false, FormBorderStyle = FormBorderStyle.None, Dock = DockStyle.Fill };
        var editor = _ledEditor;
        editor.FormClosed += (_, _) =>
        {
            if (_closing || _editor is null || _editor.IsDisposed) return;
            if (editor.Result is { } result) saved(result);
            _editor.Show(); _editor.BringToFront();
        };
        _pages.Controls.Add(editor); editor.Show(); editor.BringToFront();
    }
    internal static bool TextInputFocused(Control parent) => parent.Controls.Cast<Control>().Any(c => c.ContainsFocus && (c is TextBoxBase or ComboBox or UpDownBase || TextInputFocused(c)));
    protected override bool ProcessCmdKey(ref Message m, Keys key)
    {
        if (_host?.Options.ConfigurationMode == true && key == Keys.E && !TextInputFocused(this))
        { _configurationShortcut.SwitchTo("Experience"); return true; }
        return base.ProcessCmdKey(ref m,key);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _toolbarTips.Dispose(); _configurationShortcut.Dispose(); CloseEditors(); _live?.Dispose(); _showEditor?.Dispose(); _refresh.Dispose(); }
        base.Dispose(disposing);
        if (disposing) _applicationIcon.Dispose();
    }
    private static ListView List(params string[] columns)
    {
        var list = new DarkListView { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill, Margin = Padding.Empty, Font = new Font("Segoe UI", 9) };
        foreach (var title in columns) list.Columns.Add(title);
        list.SizeChanged += (_, _) =>
        {
            if (list.Columns.Count == 0) return;
            var width = Math.Max(0, list.ClientSize.Width);
            if (columns[0] == "Playlist")
            {
                // Keep every execution column visible even at the minimum window size.
                var weights = new[] { .29, .19, .27, .25 };
                for (int i = 0; i < columns.Length - 1; i++) list.Columns[i].Width = (int)(width * weights[i]);
            }
            else
            {
                var scale = list.DeviceDpi / 96f;
                list.Columns[0].Width = (int)((columns[0] == "Time" ? 112 : 122) * scale);
                list.Columns[1].Width = (int)((columns[1] == "Level" ? 104 : 72) * scale);
            }
            list.Columns[^1].Width = Math.Max(0, width - list.Columns.Cast<ColumnHeader>().SkipLast(1).Sum(column => column.Width));
        };
        return list;
    }
}
