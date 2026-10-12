using System.Windows.Threading;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;
using PhotoBooth.Diagnostics;

namespace ExperienceX;

// Only window management runs on the UI dispatcher. Decoding and rendering own a worker thread.
public sealed class MainWindow : Forms.Form, IPointSurface
{
    private readonly string _display;
    // Form's base constructor calls CreateParams before this constructor assigns the supplied options.
    private ExperienceOptions _options = new();
    private VideoRenderer? _renderer;
    private ScreenMediaCatalog _mediaCatalog = ScreenMediaCatalog.Empty;
    private TopmostGuard? _guard;
    private bool _available;
    private PointCalibration? _calibration;
    private bool _mouseHeld;
    private ScreenSurface? _dragScreen;
    private ScreenSurface? _pendingNameEdit;
    private System.Drawing.Point _mouseDownPosition;
    private readonly Dictionary<string, ScreenSurface> _surfaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly PointHitCycle _pointCycle = new(), _screenCycle = new();
    private readonly PointHitCycle _edgeCycle = new();
    private ScreenNameEditor? _nameEditor;
    private ScreenSurface? _editingScreen;
    private string? _originalName;
    private string? _pendingBrowseName;
    private readonly Queue<int> _nameDirections = new();
    private bool _browsingName, _finishAfterBrowse;
    internal event Action<bool>? TextEditingChanged;
    internal event Action<ScreenSurface, string, string>? RenameRequested;
    internal Func<ScreenSurface, string, string, Task<bool>>? BrowseRenameRequested;
    internal VideoRenderer? Renderer => _renderer;
    internal ScreenOptions? ScreenDefinition(string id) => _options.ScreensFor(_display).FirstOrDefault(s => s.Id == id);
    internal IEnumerable<ScreenSurface> Surfaces => _options.ScreensFor(_display).Select(s => Surface(s.Id));
    internal ScreenSurface Surface(string id)
    {
        if (!_surfaces.TryGetValue(id, out var surface))
            _surfaces[id] = surface = new(this, id);
        return surface;
    }

    private readonly RendererRecovery _recovery = new();
    private bool _recovering, _waitingForRenderer, _closing;
    private readonly Action? _beforePresent;
    private readonly AsyncTaskScope _tasks = new("Display window");
    internal void AttachCalibration(PointCalibration calibration) => _calibration = calibration;
    internal string DisplayName => _display;
    string IPointSurface.DisplayName => _display;
    int IPointSurface.PixelWidth => ClientSize.Width;
    int IPointSurface.PixelHeight => ClientSize.Height;
    void IPointSurface.ShowCalibration(int point) => ShowCalibration(point);
    void IPointSurface.FadeCalibration() => FadeCalibration();
    void IPointSurface.FlashCalibration(int point, bool success) => FlashCalibration(point, success);
    void IPointSurface.FlashCalibrationPoints(int pointMask)
    {
        _renderer?.FlashCalibrationPoints(pointMask, true);
        _guard?.EnsureTopmost("RemoteCalibrationSaved", true);
    }
    void IPointSurface.PreviewPoints(PointOptions? value) => PreviewPoints(value);
    Task<bool> IPointSurface.ConfirmCalibrationAsync(PointOptions expected) => _renderer?.ConfirmPointsAsync(expected) ?? Task.FromResult(false);
    void IPointSurface.ReleaseCalibrationCapture() => ReleaseCalibrationCapture();
    internal RendererState State => _renderer?.State ?? RendererState.Starting;
    protected override bool ShowWithoutActivation => !_options.ConfigurationMode;
    protected override Forms.CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= 0x00200000;
            if (!_options.ConfigurationMode)
                p.ExStyle |= 0x08000000;
            return p;
        }
    }
    public MainWindow(string display, ExperienceOptions options) : this(display, options, null) { }
    internal MainWindow(string display, ExperienceOptions options, Action? beforePresent)
    {
        _beforePresent = beforePresent;
        _display = display;
        _options = options;
        Text = "ExperienceX — " + display;
        FormBorderStyle = options.DeveloperMode ? Forms.FormBorderStyle.Sizable : Forms.FormBorderStyle.None;
        ShowInTaskbar = options.DeveloperMode;
        TopMost = !options.DeveloperMode;
        StartPosition = Forms.FormStartPosition.Manual;
    }
    public void Start()
    {
        RefreshDisplay();
        var dispatcher = Dispatcher.CurrentDispatcher;
        _renderer = CreateRenderer(dispatcher);
        _guard = new TopmostGuard(Handle, Dispatcher.CurrentDispatcher, () => Visible && (State != RendererState.Idle || _options.ConfigurationMode || _options.ScreensFor(_display).Any(s => s.IsMat || s.IsVisualization) || DisplayBackground.For(_display, _options).A > 0), () => _renderer?.RequestId);
        ClientSizeChanged += (_, _) => _renderer?.Resize(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        if (_available)
            ShowSurface();
    }
    public void RefreshDisplay()
    {
        if (_closing)
            return;
        var screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName.Equals(_display, StringComparison.OrdinalIgnoreCase));
        if (screen is null)
        {
            if (_available)
            {
                _calibration?.Cancel("DisplayUnavailable");
                Hide();
                _renderer?.SetDisplayAvailable(false);
            }
            if (_available || _renderer is null)
                Telemetry.Warning("DisplayUnavailable", new
                {
                    DisplayName = _display
                });
            _available = false;
            return;
        }
        var bounds = _options.DeveloperMode ? new System.Drawing.Rectangle(screen.WorkingArea.Location, new(800, 600)) : screen.Bounds;
        if (Bounds != bounds)
            Bounds = bounds;
        var restored = !_available;
        _available = true;
        if (restored) _renderer?.SetDisplayAvailable(true);
        if (_renderer?.State == RendererState.Failed)
        {
            Hide();
            if (_recovery.NextAttempt == DateTimeOffset.MaxValue && !_recovering)
                _recovery.Failed(DateTimeOffset.UtcNow);
            if (!_recovering && _recovery.Due(DateTimeOffset.UtcNow))
                _tasks.Run(_ => RecoverRendererAsync(), "Recover renderer");
            return;
        }
        if (_waitingForRenderer && _renderer?.State is RendererState.Idle or RendererState.Opening or RendererState.Playing or RendererState.FadingOut)
        {
            _waitingForRenderer = false;
            _recovery.Ready();
            ShowSurface();
            Telemetry.Info("RendererRecovered", new
            {
                DisplayName = _display
            });
        }
        else if (!Visible && !_recovering && !_waitingForRenderer && _renderer is not null)
            ShowSurface();
    }
    private VideoRenderer CreateRenderer(Dispatcher dispatcher) => new(Handle, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height), _options, _display,
        () => { if (!_closing && !dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(() => { if (!_closing) Hide(); })); }, _beforePresent, _mediaCatalog);
    internal void UpdateMediaCatalog(ScreenMediaCatalog catalog)
    {
        _mediaCatalog = catalog;
        _renderer?.UpdateMediaCatalog(catalog);
    }
    private async Task RecoverRendererAsync()
    {
        _recovering = true;
        _recovery.Begin();
        Hide();
        _calibration?.Cancel("RendererRecovery");
        var old = _renderer;
        Telemetry.Warning("RendererRecoveryStarted", new
        {
            DisplayName = _display,
            Attempt = _recovery.Attempts
        });
        try
        {
            if (old is not null)
                await Task.Run(old.Dispose);
            if (_closing)
                return;
            if (old is not null && !old.Stopped)
                throw new TimeoutException("The old graphics worker has not stopped; its HWND cannot be reused yet.");
            _renderer = CreateRenderer(Dispatcher.CurrentDispatcher);
            _waitingForRenderer = true;
        }
        catch (Exception ex)
        {
            _recovery.Failed(DateTimeOffset.UtcNow);
            Telemetry.Error("RendererRecoveryFailed", ex, new
            {
                DisplayName = _display,
                RetryAt = _recovery.NextAttempt
            });
        }
        finally { _recovering = false; }
    }
    public void UpdateOptions(ExperienceOptions options)
    {
        var changed = _options.ConfigurationMode != options.ConfigurationMode;
        _options = options;
        foreach (var id in _surfaces.Keys.Where(id => ScreenDefinition(id) is null).ToArray())
            _surfaces.Remove(id);
        if (_editingScreen is not null && (!options.ConfigurationMode ||
            (ScreenDefinition(_editingScreen.ScreenId)?.Name != _originalName && ScreenDefinition(_editingScreen.ScreenId)?.Name != _pendingBrowseName)))
            FinishNameEdit(false);
        _renderer?.Configure(options);
        if (changed && IsHandleCreated)
        {
            var style = GetWindowLongPtr(Handle, -20).ToInt64();
            SetWindowLongPtr(Handle, -20, new nint(options.ConfigurationMode ? style & ~0x08000000L : style | 0x08000000L));
            if (!options.ConfigurationMode)
                ReleaseCalibrationCapture();
        }
        RefreshDisplay();
    }
    internal void ShowCalibration(int point)
    {
        _renderer?.ShowCalibration(point);
        _guard?.EnsureTopmost("ConfigurationMode", true);
    }
    internal void FadeCalibration() => _renderer?.FadeCalibration();
    internal void FlashCalibration(int point, bool success) => _renderer?.FlashCalibration(point, success);
    internal void PreviewPoints(PointOptions? value) => _renderer?.PreviewPoints(value);
    internal void ReleaseCalibrationCapture()
    {
        _mouseHeld = false;
        _dragScreen = null;
        _pendingNameEdit = null;
        Capture = false;
    }
    protected override void OnMouseDown(Forms.MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!_options.ConfigurationMode || e.Button != Forms.MouseButtons.Left)
            return;
        Activate();
        Focus();
        if (_nameEditor is not null)
        {
            FinishNameEdit(false);
            return;
        }
        var selected = _calibration?.SelectedScreen as ScreenSurface;
        _mouseDownPosition = e.Location;
        var x = (double)e.X / Math.Max(1, ClientSize.Width - 1);
        var y = (double)e.Y / Math.Max(1, ClientSize.Height - 1);
        var screens = _options.ScreensFor(_display);
        // A flat list supports any number of points, preserving screen/point overlap order.
        var allPoints = screens.Where(s => !s.IsVisualization).SelectMany(screen => PointEditing.Points(screen.Points).Select((p, point) => (screen, point, p))).ToArray();
        var radius = PointEditing.HitRadius(ClientSize.Width, ClientSize.Height, _options.PointHitDiameterPercent);
        var hits = allPoints.Select((item, index) => (item, index)).Where(t =>
        {
            var p = t.item.p;
            return Math.Pow((p.X - x) * Math.Max(1, ClientSize.Width - 1), 2) + Math.Pow((p.Y - y) * Math.Max(1, ClientSize.Height - 1), 2) <= radius * radius;
        }).Select(t => t.index).ToArray();
        int chosen;
        if (hits.Length > 0)
        {
            chosen = _pointCycle.Pick(hits);
            _screenCycle.Pick([]);
            var dragging = Surface(allPoints[chosen].screen.Id);
            _mouseHeld = _calibration?.Begin(dragging, x, y, allPoints[chosen].point) == true;
            _dragScreen = _mouseHeld ? dragging : null;
            Capture = _mouseHeld;
        }
        else
        {
            _pointCycle.Pick([]);
            if (selected?.DisplayName == _display && ScreenDefinition(selected.ScreenId) is { } selectedDefinition &&
                ScreenNameLabel.Bounds(selected.ScreenName, selectedDefinition.Points, ClientSize.Width, ClientSize.Height).Contains(e.Location))
            {
                if (selectedDefinition.IsVisualization || !ScreenSelection.Contains(selectedDefinition.Points, x, y))
                {
                    BeginNameEdit(selected);
                    return;
                }
                _mouseHeld = _calibration?.BeginScreenDrag(selected, x, y) == true;
                _dragScreen = _mouseHeld ? selected : null;
                _pendingNameEdit = _mouseHeld ? selected : null;
                Capture = _mouseHeld;
                return;
            }
            chosen = _screenCycle.Pick(screens.Select((screen, index) => (screen, index)).Where(item => ScreenSelection.Contains(item.screen.Points, x, y)).Select(item => item.index).ToArray());
            if (chosen < 0)
                _calibration?.SelectScreen(null);
            else
            {
                var dragging = Surface(screens[chosen].Id);
                _mouseHeld = _calibration?.BeginScreenDrag(dragging, x, y) == true;
                _dragScreen = _mouseHeld ? dragging : null;
                Capture = _mouseHeld;
            }
        }
    }
    protected override void OnMouseDoubleClick(Forms.MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (!_options.ConfigurationMode || e.Button != Forms.MouseButtons.Left || _nameEditor is not null) return;
        var x = (double)e.X / Math.Max(1, ClientSize.Width - 1);
        var y = (double)e.Y / Math.Max(1, ClientSize.Height - 1);
        // Edge insertion has a small pixel tolerance independent of the larger point grab area.
        var candidates = _options.ScreensFor(_display).Select((screen, index) => (screen, index,
            edge: PolygonGeometry.ClosestEdge(screen.Points, x, y, ClientSize.Width, ClientSize.Height)))
            .Where(item => item.screen.IsMat && item.edge.Distance <= 8 &&
                !PointEditing.Points(item.screen.Points).Any(p => Math.Sqrt(Math.Pow((p.X - item.edge.Point.X) * (ClientSize.Width - 1), 2) +
                    Math.Pow((p.Y - item.edge.Point.Y) * (ClientSize.Height - 1), 2)) < 2)).ToArray();
        var chosen = _edgeCycle.Pick(candidates.Select(item => item.index).ToArray());
        if (chosen < 0) return;
        var target = candidates.First(item => item.index == chosen);
        _calibration?.Cancel("InsertPoint");
        ReleaseCalibrationCapture();
        _calibration?.RequestInsertPoint(Surface(target.screen.Id), target.edge.Edge, target.edge.Point);
    }
    internal void EditScreenName(ScreenSurface surface)
    {
        if (!_options.ConfigurationMode || surface.DisplayName != _display || ScreenDefinition(surface.ScreenId) is null) return;
        Activate(); Focus();
        ReleaseCalibrationCapture();
        BeginNameEdit(surface);
    }
    private void BeginNameEdit(ScreenSurface surface)
    {
        if (_nameEditor is not null) FinishNameEdit(false);
        _calibration?.Cancel("ScreenNameEdit");
        _editingScreen = surface;
        _originalName = surface.ScreenName;
        TextEditingChanged?.Invoke(true);
        var k = ScreenDefinition(surface.ScreenId)!.Points;
        var bounds = ScreenNameLabel.Bounds(surface.ScreenName, k, ClientSize.Width, ClientSize.Height);
        _nameDirections.Clear();
        _finishAfterBrowse = false;
        _nameEditor = new(FinishNameEdit, ScreenDefinition(surface.ScreenId)!.IsVideo ? BrowseMediaName : null)
        {
            Text = surface.ScreenName,
            MaxLength = 80,
            Font = new System.Drawing.Font("Segoe UI", ScreenNameLabel.FontSize(ClientSize.Width, ClientSize.Height), System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel),
            ForeColor = System.Drawing.Color.FromArgb(8, 65, 170),
            BackColor = System.Drawing.Color.FromArgb(200, 225, 255),
            TextAlign = Forms.HorizontalAlignment.Center,
            Bounds = new(Math.Clamp(bounds.X, 0, Math.Max(0, ClientSize.Width - bounds.Width)), Math.Clamp(bounds.Y, 0, Math.Max(0, ClientSize.Height - bounds.Height)), bounds.Width, bounds.Height)
        };
        Controls.Add(_nameEditor);
        _nameEditor.BringToFront();
        _nameEditor.Focus();
        _nameEditor.SelectAll();
    }
    private void BrowseMediaName(int direction)
    {
        if (_nameEditor is null || _editingScreen is null || BrowseRenameRequested is null || _nameDirections.Count >= 32) return;
        _nameDirections.Enqueue(direction);
        if (!_browsingName) _tasks.Run(BrowseMediaNamesAsync, "Browse screen media names");
    }
    private async Task BrowseMediaNamesAsync(CancellationToken token)
    {
        _browsingName = true;
        try
        {
            while (_nameEditor is { } editor && _editingScreen is { } surface && _nameDirections.TryDequeue(out var direction))
            {
                token.ThrowIfCancellationRequested();
                var definition = ScreenDefinition(surface.ScreenId);
                if (definition is null || !definition.IsVideo) break;
                var others = _options.ScreensFor(_display).Where(s => s.Id != surface.ScreenId).ToArray();
                var names = _mediaCatalog.Names().Where(name => !others.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.DefaultName, name, StringComparison.OrdinalIgnoreCase))).ToArray();
                var defaultName = ScreenOptions.DefaultFor(definition, _options.ScreensFor(_display));
                var next = ScreenMediaNameCycle.Next(defaultName, _mediaCatalog.BrowseName(definition) ?? definition.Name, names, direction);
                if (next == definition.Name) continue;
                _pendingBrowseName = next;
                editor.ReadOnly = true;
                var renamed = await BrowseRenameRequested!(surface, definition.Name, next);
                if (!ReferenceEquals(editor, _nameEditor)) break;
                _pendingBrowseName = null;
                _originalName = ScreenDefinition(surface.ScreenId)?.Name ?? definition.Name;
                editor.Text = _originalName;
                editor.SelectAll();
                editor.ReadOnly = false;
                if (!renamed) { _nameDirections.Clear(); break; }
            }
        }
        finally
        {
            _browsingName = false;
            _pendingBrowseName = null;
            if (_nameEditor is not null) _nameEditor.ReadOnly = false;
            if (_finishAfterBrowse) { _finishAfterBrowse = false; FinishNameEdit(true); }
        }
    }
    private void FinishNameEdit(bool commit)
    {
        if (_nameEditor is null || _editingScreen is null)
            return;
        if (commit && _browsingName) { _finishAfterBrowse = true; return; }
        _nameDirections.Clear();
        _finishAfterBrowse = false;
        var surface = _editingScreen;
        var oldName = _originalName!;
        var name = _nameEditor.Text.Trim();
        var editor = _nameEditor;
        _nameEditor = null;
        _editingScreen = null;
        _originalName = null;
        Controls.Remove(editor);
        editor.Dispose();
        TextEditingChanged?.Invoke(false);
        Focus();
        if (commit)
            RenameRequested?.Invoke(surface, oldName, name);
    }
    protected override void OnMouseMove(Forms.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pendingNameEdit is not null)
        {
            var threshold = Forms.SystemInformation.DragSize;
            if (Math.Abs(e.X - _mouseDownPosition.X) < Math.Max(1, threshold.Width / 2) &&
                Math.Abs(e.Y - _mouseDownPosition.Y) < Math.Max(1, threshold.Height / 2))
                return;
            _pendingNameEdit = null;
        }
        if (_mouseHeld && _dragScreen is not null)
            _calibration?.Move(_dragScreen, (double)e.X / Math.Max(1, ClientSize.Width - 1), (double)e.Y / Math.Max(1, ClientSize.Height - 1));
    }
    protected override void OnMouseUp(Forms.MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != Forms.MouseButtons.Left || !_mouseHeld)
            return;
        var dragged = _dragScreen;
        var editName = _pendingNameEdit;
        ReleaseCalibrationCapture();
        if (editName is not null)
            BeginNameEdit(editName);
        else if (dragged is not null)
            _calibration?.RequestFinish(dragged);
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && _mouseHeld)
        {
            _mouseHeld = false;
            _calibration?.Cancel("MouseCaptureLost");
        }
    }
    private IPointSurface KeySurface() => _calibration?.SelectedScreen is ScreenSurface selected && selected.DisplayName == _display ? selected : Surfaces.FirstOrDefault() ?? (IPointSurface)this;
    protected override bool ProcessCmdKey(ref Forms.Message msg, Forms.Keys keyData) =>
        (_nameEditor is null && _calibration?.Key(KeySurface(), keyData) == true) || base.ProcessCmdKey(ref msg, keyData);
    protected override void WndProc(ref Forms.Message m)
    {
        // These Forms windows run on a WPF dispatcher, without the WinForms pre-translation loop.
        if (_nameEditor is null && m.Msg == 0x100 && _calibration?.Key(KeySurface(), (Forms.Keys)(int)m.WParam | ModifierKeys) == true)
        {
            m.Result = 0;
            return;
        }
        if (_options.ConfigurationMode && m.Msg == 0x102 && (int)m.WParam is 9 or 13 or 27)
        {
            m.Result = 0;
            return;
        }
        if (m.Msg == 0x21 && !_options.ConfigurationMode)
        {
            m.Result = new nint(3);
            return;
        }
        base.WndProc(ref m);
    }
    public void PlayVideo(string path, ExperienceRequest request)
    {
        if (!_available)
            throw new InvalidOperationException("Assigned display is unavailable.");
        if (State == RendererState.Failed || _recovering || _waitingForRenderer)
            throw new InvalidOperationException("The DirectX renderer is recovering. Retry with a new request ID once ready.");
        _renderer!.Play(path, request);
        _guard?.EnsureTopmost("VideoRequest", true);
    }
    public void StopVideoPlayback(string? source, string playbackRequestId) => _renderer?.StopPlayback(source, playbackRequestId);
    // Production awaits recovery and worker shutdown without blocking the dispatcher.
    public async Task ShutdownWindowAsync()
    {
        _closing = true;
        FinishNameEdit(false);
        Hide();
        ReleaseCalibrationCapture();
        _guard?.Dispose();
        await _tasks.DisposeAsync();
        var renderer = _renderer;
        _renderer = null;
        if (renderer is not null)
            await Task.Run(renderer.Dispose);
        Close();
    }
    // Synchronous cleanup for isolated native-window tests, which have no pending UI operations.
    public void ShutdownWindow()
    {
        _closing = true;
        FinishNameEdit(false);
        _tasks.Cancel();
        Hide();
        _guard?.Dispose();
        _renderer?.Dispose();
        _renderer = null;
        Close();
    }
    private void ShowSurface()
    {
        Show();
        // Override a launcher's SW_HIDE startup hint without taking keyboard focus.
        ShowWindow(Handle, 4);
        Telemetry.Info("DisplaySurfaceVisible", new
        {
            DisplayName = _display,
            Visible = IsWindowVisible(Handle),
            Width = ClientSize.Width,
            Height = ClientSize.Height
        });
    }
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
