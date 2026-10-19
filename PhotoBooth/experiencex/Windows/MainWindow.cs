using System.Windows.Threading;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;
using PhotoBooth.Diagnostics;

namespace ExperienceX;

// Only window management runs on the UI dispatcher. Decoding and rendering own a worker thread.
public sealed class MainWindow : Forms.Form, IPointSurface
{
    private readonly string _display;
    private readonly System.Drawing.Icon _applicationIcon;
    private ConfigurationSubmenu _submenu;
    internal ConfigurationSubmenu Submenu => _submenu;
    internal event Action? CancelSubmenuRequested;
    internal void SetSubmenu(ConfigurationSubmenu menu)
    {
        if (_submenu == menu) return;
        _submenu = menu;
        if (menu != ConfigurationSubmenu.None)
        {
            _modifierFraming.Suspend();
            if (_mouseHeld)
            {
                if (_framing.Dragging) _framing.EndDrag();
                var dragged = _dragScreen;
                ReleaseCalibrationCapture();
                if (dragged is not null) _calibration?.RequestFinish(dragged);
            }
        }
        _renderer?.SuppressHelp(menu != ConfigurationSubmenu.None);
        _ledTools?.Renderer.SuppressHelp(menu != ConfigurationSubmenu.None);
    }
    // Form's base constructor calls CreateParams before this constructor assigns the supplied options.
    private ExperienceOptions _options = new();
    private volatile VideoRenderer? _renderer;
    internal RenderHealth? RendererHealth => _renderer?.Diagnostics;
    private LedCalibrationWindow? _ledTools;
    private bool _recoveringLedTools;
    private readonly Dictionary<string,PointOptions> _ledPreviews = new();
    internal VideoRenderer? ToolsRenderer => _ledTools?.Renderer;
    internal event Action? LedGeometryChanged;
    internal IEnumerable<LedOutputScreen> LedScreens => !_available || !Visible || State==RendererState.Failed ? [] : _options.ScreensFor(_display).Where(s=>s.IsLed && s.Perspective!.Available).Select(s=>new LedOutputScreen(_display,new(PointToScreen(System.Drawing.Point.Empty),ClientSize), _ledPreviews.TryGetValue(s.Id,out var points) ? s with {Points=points} : s));
    internal void PreviewLed(string id,PointOptions? points)
    {
        if(points is null) _ledPreviews.Remove(id); else _ledPreviews[id]=points;
        LedGeometryChanged?.Invoke();
    }
    private void RefreshLedTools()
    {
        if (_renderer is null || _closing) return;
        if (_recoveringLedTools) return;
        if (_ledTools?.Renderer.State == RendererState.Failed)
        {
            _tasks.Run(async _ =>
            {
                _recoveringLedTools=true;var failed=_ledTools;_ledTools=null;
                try { if(failed is not null) await failed.ShutdownAsync(); }
                catch(Exception ex) { Telemetry.Error("LedCalibrationRecoveryFailed",ex,new {DisplayName=_display}); }
                finally { _recoveringLedTools=false; }
                if(!_closing) { Telemetry.Info("LedCalibrationRecoveryStarted",new {DisplayName=_display}); RefreshLedTools(); }
            },"Recover LED calibration renderer");
            return;
        }
        bool needed=_available && _options.ConfigurationMode && _options.ScreensFor(_display).Any(s=>s.IsLed);
        if(needed && _ledTools is null)
        {
            _ledTools=new(this,_options);
        }
        _ledTools?.Update(this,_options);
        _ledTools?.Renderer.SuppressHelp(_submenu != ConfigurationSubmenu.None);
        _renderer?.ShowTools(!needed);
    }
    private ScreenMediaCatalog _mediaCatalog = ScreenMediaCatalog.Empty;
    private TopmostGuard? _guard;
    private bool _available;
    private PointCalibration? _calibration;
    private bool _mouseHeld;
    private bool _pointerShift;
    private bool _pointerControl;
    private bool PanModifier => _pointerShift || (ModifierKeys & Forms.Keys.Shift) != 0;
    private bool ZoomModifier => _pointerControl || (ModifierKeys & Forms.Keys.Control) != 0;
    private bool RotateModifier => (ModifierKeys & Forms.Keys.Alt) != 0;
    private ScreenSurface? _dragScreen;
    private ScreenSurface? _pendingNameEdit;
    private System.Drawing.Point _mouseDownPosition;
    private readonly Dictionary<string, ScreenSurface> _surfaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly PointHitCycle _pointCycle = new(), _screenCycle = new();
    private readonly PointHitCycle _edgeCycle = new();
    private readonly PointHitCycle _edgeDragCycle = new();
    private ScreenNameEditor? _nameEditor;
    private ScreenSurface? _editingScreen;
    private string? _originalName;
    private readonly Dictionary<string, ScreenMediaBrowser> _mediaBrowsers = new();
    private readonly Queue<(ScreenSurface Surface, Forms.Keys Key)> _mediaKeys = new();
    private bool _browsingMedia;
    internal event Action<bool>? TextEditingChanged;
    internal event Action<ScreenSurface, string, string>? RenameRequested;
    internal Func<ScreenSurface, ScreenOptions, string, Task<bool>>? SelectMediaRequested;
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
    private readonly VideoFramingEditor _framing;
    private readonly ModifierFramingGesture _modifierFraming;
    private readonly Forms.Timer _modifierPoll = new() { Interval = 25 };
    private readonly byte[] _keyboard = new byte[256];
    internal Func<ModifierFramingInput>? ModifierInputForTest = null;
    internal bool ModifierFramingActive => _modifierFraming.Active;
    internal Func<ScreenOptions, VideoFramingOptions, CancellationToken, Task>? SaveFramingRequested;
    internal VideoFramingEditor FramingEditor => _framing;
    internal Task FinishFramingAsync() { _modifierFraming.Suspend(); return _framing.FinishAsync(); }
    internal void SuspendModifierFraming() => _modifierFraming.Suspend();
    internal void AttachCalibration(PointCalibration calibration) => _calibration = calibration;
    internal string DisplayName => _display;
    internal void FocusConfiguration()
    {
        if (_closing || !_options.ConfigurationMode || !Visible) return;
        Activate(); Focus(); _guard?.EnsureTopmost("ConfigurationFocus", true);
    }
    string IPointSurface.DisplayName => _display;
    int IPointSurface.PixelWidth => ClientSize.Width;
    int IPointSurface.PixelHeight => ClientSize.Height;
    void IPointSurface.ShowCalibration(int point) => ShowCalibration(point);
    void IPointSurface.ShowJoinTarget(int point) => _renderer?.ShowJoinTarget(point);
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
        _framing = new((id, view) => _renderer?.PreviewFraming(id, view),
            (screen, view, token) => SaveFramingRequested?.Invoke(screen, view, token) ?? Task.FromException(new InvalidOperationException("Video framing persistence is unavailable.")),
            (id, success) => _renderer?.FlashCalibrationPoints(15, success, id),
            screen => VideoFramingOptions.ScreenAspect(screen.Points, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height)));
        _modifierFraming = new(_framing, () =>
        {
            var selected = _calibration?.SelectedScreen;
            if (selected is not null) _calibration?.SelectScreen(selected);
        });
        _modifierPoll.Tick += (_, _) => PollModifierFraming();
        Text = "ExperienceX — " + display;
        using (var icon = typeof(MainWindow).Assembly.GetManifestResourceStream("ExperienceX.Assets.Application.ico")
            ?? throw new InvalidOperationException("The Experience icon is missing."))
            _applicationIcon = new System.Drawing.Icon(icon);
        Icon = _applicationIcon;
        FormBorderStyle = options.DeveloperMode ? Forms.FormBorderStyle.Sizable : Forms.FormBorderStyle.None;
        ShowInTaskbar = true;
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
        RefreshLedTools();
        if (_options.ConfigurationMode) _modifierPoll.Start();
    }
    public void RefreshDisplay()
    {
        if (_closing)
            return;
        _renderer?.SetPresentationSuspended(!_available || WindowState == Forms.FormWindowState.Minimized);
        var screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName.Equals(_display, StringComparison.OrdinalIgnoreCase));
        if (screen is null)
        {
            _renderer?.SetPresentationSuspended(true);
            if (_available)
            {
                _framing.Cancel("DisplayUnavailable");
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
            RefreshLedTools();
            return;
        }
        var bounds = _options.DeveloperMode ? new System.Drawing.Rectangle(screen.WorkingArea.Location, new(800, 600)) : screen.Bounds;
        if (Bounds != bounds)
            Bounds = bounds;
        var restored = !_available;
        _available = true;
        _renderer?.SetPresentationSuspended(WindowState == Forms.FormWindowState.Minimized);
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
        RefreshLedTools();
    }
    private VideoRenderer CreateRenderer(Dispatcher dispatcher)
    {
        var renderer = new VideoRenderer(Handle, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height), _options, _display,
            () => { if (!_closing && !dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(() => { if (!_closing) Hide(); })); }, _beforePresent, _mediaCatalog);
        renderer.SuppressHelp(_submenu != ConfigurationSubmenu.None);
        return renderer;
    }
    internal void UpdateMediaCatalog(ScreenMediaCatalog catalog)
    {
        _mediaCatalog = catalog;
        _renderer?.UpdateMediaCatalog(catalog);
        RefreshMediaLabels();
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
        var wasDragging = _framing.Dragging;
        _framing.Configure(options, _display);
        if (wasDragging && !_framing.Dragging) ReleaseCalibrationCapture();
        _options = options;
        if (options.ConfigurationMode) _modifierPoll.Start();
        else { _modifierPoll.Stop(); _modifierFraming.Cancel("ConfigurationModeDisabled"); }
        foreach (var id in _surfaces.Keys.Where(id => ScreenDefinition(id) is null).ToArray())
            _surfaces.Remove(id);
        if (_editingScreen is not null && (!options.ConfigurationMode ||
            (ScreenDefinition(_editingScreen.ScreenId)?.Name != _originalName)))
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
        RefreshLedTools();
        RefreshMediaLabels();
        LedGeometryChanged?.Invoke();
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
        if (_framing.Dragging) _framing.Cancel("CaptureReleased");
        _mouseHeld = false;
        _dragScreen = null;
        _pendingNameEdit = null;
        Capture = false;
    }
    protected override void OnMouseDown(Forms.MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_submenu != ConfigurationSubmenu.None || !_options.ConfigurationMode || e.Button != Forms.MouseButtons.Left)
            return;
        _modifierFraming.Suspend();
        Activate();
        Focus();
        if (_nameEditor is not null)
        {
            var continueGesture = (ZoomModifier || PanModifier || RotateModifier) && _nameEditor.Text == _originalName && !_browsingMedia;
            FinishNameEdit(false);
            if (!continueGesture) return;
        }
        var selected = _calibration?.SelectedScreen as ScreenSurface;
        _mouseDownPosition = e.Location;
        var x = (double)e.X / Math.Max(1, ClientSize.Width - 1);
        var y = (double)e.Y / Math.Max(1, ClientSize.Height - 1);
        var screens = _options.ScreensFor(_display);
        if ((ZoomModifier || PanModifier || RotateModifier) && selected?.DisplayName == _display &&
            ScreenDefinition(selected.ScreenId) is { IsVideo: true } selectedVideo && ScreenSelection.Contains(selectedVideo.Points, x, y))
        {
            BeginSurfaceDrag(selected, x, y);
            return;
        }
        _framing.Flush();
        if (_framing.Busy) return;
        if (selected?.DisplayName == _display && ScreenDefinition(selected.ScreenId) is { IsLed: true, IsLedArray: false } matrix)
        {
            var handle=LedGeometry.RotationHandle(matrix.Points,ClientSize.Width,ClientSize.Height);
            if(Math.Pow((handle.X-x)*(ClientSize.Width-1),2)+Math.Pow((handle.Y-y)*(ClientSize.Height-1),2)<=18*18)
            {
                _mouseHeld=_calibration?.BeginRotation(selected,x,y)==true; _dragScreen=_mouseHeld ? selected:null; Capture=_mouseHeld; return;
            }
        }
        // A flat list supports any number of points, preserving screen/point overlap order.
        var editable = ScreenSelection.EditablePoints(screens, selected?.DisplayName == _display ? selected.ScreenId : null).ToArray();
        var allPoints = editable.SelectMany(screen => PointEditing.Points(screen.Points).Select((p, point) => (screen, point, p))).ToArray();
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
            var edges = editable.Select((screen, index) => (screen, index, edge: PolygonGeometry.ClosestEdge(screen.Points, x, y, ClientSize.Width, ClientSize.Height)))
                .Where(item => item.edge.Distance <= 8 * DeviceDpi / 96.0).ToArray();
            var pickedEdge = _edgeDragCycle.Pick(edges.Select(item => item.index).ToArray());
            if (pickedEdge >= 0)
            {
                var edge = edges.Single(item => item.index == pickedEdge); var dragging = Surface(edge.screen.Id);
                _mouseHeld = _calibration?.BeginEdgeDrag(dragging, edge.edge.Edge, x, y) == true;
                _dragScreen = _mouseHeld ? dragging : null; Capture = _mouseHeld; return;
            }
            if (selected?.DisplayName == _display && ScreenDefinition(selected.ScreenId) is { } selectedDefinition &&
                ScreenNameLabel.Bounds(selected.ScreenName, selectedDefinition.Points, ClientSize.Width, ClientSize.Height).Contains(e.Location))
            {
                if (selectedDefinition.IsVisualization || !(selectedDefinition.IsLed ? LedGeometry.Contains(selectedDefinition,x,y,ClientSize.Width,ClientSize.Height) : ScreenSelection.Contains(selectedDefinition.Points, x, y)))
                {
                    BeginNameEdit(selected);
                    return;
                }
                _mouseHeld = BeginSurfaceDrag(selected, x, y);
                _dragScreen = _mouseHeld ? selected : null;
                _pendingNameEdit = _mouseHeld && !_framing.Dragging ? selected : null;
                Capture = _mouseHeld;
                return;
            }
            chosen = _screenCycle.Pick(screens.Select((screen, index) => (screen, index)).Where(item => item.screen.IsVisualization || (item.screen.IsLed ? LedGeometry.Contains(item.screen,x,y,ClientSize.Width,ClientSize.Height) : ScreenSelection.Contains(item.screen.Points, x, y))).Select(item => item.index).ToArray());
            if (chosen < 0)
                _calibration?.SelectDisplay(_display);
            else
            {
                var dragging = Surface(screens[chosen].Id);
                _mouseHeld = BeginSurfaceDrag(dragging, x, y);
                _dragScreen = _mouseHeld ? dragging : null;
                Capture = _mouseHeld;
            }
        }
    }
    private bool BeginSurfaceDrag(ScreenSurface screen, double x, double y)
    {
        if ((ZoomModifier || PanModifier || RotateModifier) && ScreenDefinition(screen.ScreenId) is { IsVideo: true } video)
        {
            if (_calibration?.Busy == true) return false;
            _calibration?.SelectScreen(screen);
            var width = Math.Max(1, ClientSize.Width - 1) * 96.0 / DeviceDpi;
            var height = Math.Max(1, ClientSize.Height - 1) * 96.0 / DeviceDpi;
            var dragging = ZoomModifier ? _framing.BeginZoom(video, x, y, width, height)
                : RotateModifier ? _framing.BeginRotate(video, x, y, width, height) : _framing.BeginPan(video, x, y);
            Capture = dragging;
            return dragging;
        }
        return _calibration?.BeginScreenDrag(screen, x, y) == true;
    }
    protected override void OnMouseWheel(Forms.MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_submenu != ConfigurationSubmenu.None || _closing || !_options.ConfigurationMode || _mouseHeld || _framing.Dragging || _calibration?.Busy == true ||
            _calibration?.SelectedScreen is not ScreenSurface selected || selected.DisplayName != _display ||
            ScreenDefinition(selected.ScreenId) is not { IsVideo: true } video) return;
        if (_nameEditor is not null)
        {
            if (_nameEditor.Text != _originalName || _browsingMedia) return;
            FinishNameEdit(false); // A center-label click followed by scrolling means zoom, not rename.
        }
        var x = (double)e.X / Math.Max(1, ClientSize.Width - 1);
        var y = (double)e.Y / Math.Max(1, ClientSize.Height - 1);
        if (!ScreenSelection.Contains(video.Points, x, y)) return;
        _calibration.SelectScreen(selected);
        _framing.Wheel(video, x, y, e.Delta);
    }
    internal void ZoomSelected(int direction)
    {
        _modifierFraming.Suspend();
        if (_submenu != ConfigurationSubmenu.None || _closing || !_options.ConfigurationMode || _nameEditor is not null || _mouseHeld || _framing.Dragging || _calibration?.Busy == true ||
            _calibration?.SelectedScreen is not ScreenSurface selected || selected.DisplayName != _display ||
            ScreenDefinition(selected.ScreenId) is not { IsVideo: true } video) return;
        var position = PointToClient(Forms.Cursor.Position);
        var x = (double)position.X / Math.Max(1, ClientSize.Width - 1);
        var y = (double)position.Y / Math.Max(1, ClientSize.Height - 1);
        var anchor = ScreenSelection.Contains(video.Points, x, y) && VideoFramingOptions.TryPointer(video.Points, x, y, out var pointer)
            ? new ScreenPoint(Math.Clamp(pointer.X, 0, 1), Math.Clamp(pointer.Y, 0, 1)) : new ScreenPoint(.5, .5);
        _calibration.SelectScreen(selected);
        _framing.KeyboardZoom(video, anchor, direction);
    }
    protected override void OnMouseDoubleClick(Forms.MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (_submenu != ConfigurationSubmenu.None || !_options.ConfigurationMode || e.Button != Forms.MouseButtons.Left || _nameEditor is not null) return;
        var x = (double)e.X / Math.Max(1, ClientSize.Width - 1);
        var y = (double)e.Y / Math.Max(1, ClientSize.Height - 1);
        // Edge insertion has a small pixel tolerance independent of the larger point grab area.
        var selected = _calibration?.SelectedScreen;
        var candidates = ScreenSelection.EditablePoints(_options.ScreensFor(_display), selected?.DisplayName == _display ? selected.ScreenId : null).Select((screen, index) => (screen, index,
            edge: PolygonGeometry.ClosestEdge(screen.Points, x, y, ClientSize.Width, ClientSize.Height)))
            .Where(item => item.screen.SupportsPointInsertion && item.edge.Distance <= 8 &&
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
        _modifierFraming.Suspend();
        if (_nameEditor is not null) FinishNameEdit(false);
        _calibration?.Cancel("ScreenNameEdit");
        _editingScreen = surface;
        _originalName = surface.ScreenName;
        TextEditingChanged?.Invoke(true);
        var k = ScreenDefinition(surface.ScreenId)!.Points;
        var bounds = ScreenNameLabel.Bounds(surface.ScreenName, k, ClientSize.Width, ClientSize.Height);
        _mediaKeys.Clear();
        _nameEditor = new(FinishNameEdit,
            ScreenDefinition(surface.ScreenId)!.IsVideo ? e =>
        {
            if (_nameEditor is not { } editor) return;
            var position = PointToClient(editor.PointToScreen(e.Location));
            OnMouseWheel(new(e.Button, e.Clicks, position.X, position.Y, e.Delta));
        } : null)
        {
            Text = surface.ScreenName,
            MaxLength = 80,
            Font = new System.Drawing.Font("Segoe UI", ScreenNameLabel.FontSize(ClientSize.Width, ClientSize.Height), System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel),
            ForeColor = System.Drawing.Color.FromArgb(255, 20, 36),
            BackColor = System.Drawing.Color.FromArgb(200, 225, 255),
            TextAlign = Forms.HorizontalAlignment.Center,
            Bounds = new(Math.Clamp(bounds.X, 0, Math.Max(0, ClientSize.Width - bounds.Width)), Math.Clamp(bounds.Y, 0, Math.Max(0, ClientSize.Height - bounds.Height)), bounds.Width, bounds.Height)
        };
        Controls.Add(_nameEditor);
        _nameEditor.BringToFront();
        _nameEditor.Focus();
        _nameEditor.SelectAll();
    }
    internal string? BrowserLabel(string id) => _mediaBrowsers.TryGetValue(id,out var browser) ? browser.Label : null;
    private ScreenMediaBrowser Browser(ScreenOptions screen)
    {
        if (!_mediaBrowsers.TryGetValue(screen.Id,out var browser)) _mediaBrowsers[screen.Id]=browser=new();
        return browser;
    }
    private void RefreshMediaLabels()
    {
        foreach(var screen in _options.ScreensFor(_display).Where(s=>s.IsVideo))
        {
            var browser=Browser(screen);
            if (!_browsingMedia) browser.Reconcile(_mediaCatalog,screen);
            var caption=_options.ConfigurationMode && browser.Active ? browser.Label : null;
            _renderer?.SetMediaCaption(screen.Id,caption);
            _ledTools?.Renderer.SetMediaCaption(screen.Id,caption);
        }
        foreach(var id in _mediaBrowsers.Keys.Where(id=>ScreenDefinition(id) is null).ToArray()) _mediaBrowsers.Remove(id);
    }
    private void BrowseMedia(Forms.Keys key)
    {
        if (_nameEditor is not null || _calibration?.SelectedScreen is not ScreenSurface surface || surface.DisplayName!=_display ||
            ScreenDefinition(surface.ScreenId)?.IsVideo!=true || SelectMediaRequested is null || _mediaKeys.Count>=32) return;
        _mediaKeys.Enqueue((surface,key));
        if (!_browsingMedia) _tasks.Run(BrowseMediaAsync,"Browse media folders");
    }
    private async Task BrowseMediaAsync(CancellationToken token)
    {
        _browsingMedia=true;
        try
        {
            while(_mediaKeys.TryDequeue(out var change))
            {
                token.ThrowIfCancellationRequested();
                if (!_options.ConfigurationMode || _nameEditor is not null || _calibration?.SelectedScreen!=change.Surface ||
                    _calibration.HasSelectedPoint(change.Surface) || ScreenDefinition(change.Surface.ScreenId) is not {IsVideo:true} screen) continue;
                var browser=Browser(screen);
                var media=browser.Navigate(_mediaCatalog,screen,change.Key);
                RefreshMediaLabels();
                if (media is not null && !await SelectMediaRequested!(change.Surface,screen,media)) { _mediaKeys.Clear(); break; }
            }
        }
        finally { _browsingMedia=false; RefreshMediaLabels(); }
    }
    private void FinishNameEdit(bool commit)
    {
        if (_nameEditor is null || _editingScreen is null)
            return;
        _mediaKeys.Clear();
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
        if (_submenu != ConfigurationSubmenu.None) return;
        if (_modifierFraming.Active) { PollModifierFraming(); return; }
        if (_framing.Dragging)
        {
            _framing.MoveDrag((double)e.X / Math.Max(1, ClientSize.Width - 1), (double)e.Y / Math.Max(1, ClientSize.Height - 1));
            return;
        }
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
        if (e.Button == Forms.MouseButtons.Left && _framing.Dragging && !_modifierFraming.Active)
        {
            _framing.MoveDrag((double)e.X / Math.Max(1, ClientSize.Width - 1), (double)e.Y / Math.Max(1, ClientSize.Height - 1));
            _framing.EndDrag();
            ReleaseCalibrationCapture();
            return;
        }
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
        if (!Capture && _framing.Dragging && !_modifierFraming.Active) _framing.Cancel("MouseCaptureLost");
        if (!Capture && _mouseHeld)
        {
            _mouseHeld = false;
            _calibration?.Cancel("MouseCaptureLost");
        }
    }
    private IPointSurface KeySurface() => _calibration?.SelectedScreen is ScreenSurface selected && selected.DisplayName == _display ? selected : Surfaces.FirstOrDefault() ?? (IPointSurface)this;
    private void PollModifierFraming()
    {
        var selected = _calibration?.SelectedScreen as ScreenSurface;
        var screen = selected?.DisplayName == _display ? ScreenDefinition(selected.ScreenId) : null;
        var allowed = _submenu == ConfigurationSubmenu.None && !_closing && _available && Visible && _options.ConfigurationMode && _nameEditor is null &&
            !_mouseHeld && _calibration?.Busy != true && _calibration?.Editing != true && !ConfigurationDialog.Active;
        var input = ModifierInputForTest?.Invoke() ?? ReadModifierInput();
        _modifierFraming.Update(input, screen, allowed, Math.Max(1,ClientSize.Width-1)*96.0/DeviceDpi, Math.Max(1,ClientSize.Height-1)*96.0/DeviceDpi);
    }
    private ModifierFramingInput ReadModifierInput()
    {
        if (!GetKeyboardState(_keyboard)) return new(Forms.Keys.None,new(0,0),false);
        bool Down(int key) => (_keyboard[key] & 0x80) != 0;
        var modifiers = (Down(0x10) ? Forms.Keys.Shift : 0) | (Down(0x11) ? Forms.Keys.Control : 0) | (Down(0x12) ? Forms.Keys.Alt : 0);
        var other = Enumerable.Range(8,248).Any(key => key is not (0x10 or 0x11 or 0x12 or 0xa0 or 0xa1 or 0xa2 or 0xa3 or 0xa4 or 0xa5) && Down(key));
        var pointer = PointToClient(Forms.Cursor.Position);
        return new(modifiers,new((double)pointer.X/Math.Max(1,ClientSize.Width-1),(double)pointer.Y/Math.Max(1,ClientSize.Height-1)),
            ContainsFocus && PhotoBooth.Windows.ConfigurationShortcut.ApplicationFocused,
            Forms.Control.MouseButtons != Forms.MouseButtons.None,other,Down(0x1b));
    }
    protected override void OnDeactivate(EventArgs e)
    {
        _modifierFraming?.Cancel("FocusLost"); base.OnDeactivate(e);
    }
    private bool HandleScreenKey(Forms.Keys key)
    {
        if (_submenu != ConfigurationSubmenu.None)
        {
            if (key == Forms.Keys.Escape) CancelSubmenuRequested?.Invoke();
            return true;
        }
        if (_nameEditor is not null) return false;
        if (key == Forms.Keys.Escape && _framing.Dragging)
        {
            if (_modifierFraming.Active) _modifierFraming.Cancel("Escape"); else _framing.Cancel("Escape");
            ReleaseCalibrationCapture(); return true;
        }
        if (_options.ConfigurationMode && key is Forms.Keys.Up or Forms.Keys.Down or Forms.Keys.Left or Forms.Keys.Right &&
            _calibration?.SelectedScreen is ScreenSurface selected && selected.DisplayName == _display &&
            !_calibration.HasSelectedPoint(selected) && ScreenDefinition(selected.ScreenId)?.IsVideo == true)
        {
            BrowseMedia(key);
            return true;
        }
        return _calibration?.Key(KeySurface(), key) == true;
    }
    protected override bool ProcessCmdKey(ref Forms.Message msg, Forms.Keys keyData) =>
        HandleScreenKey(keyData) || base.ProcessCmdKey(ref msg, keyData);
    protected override void WndProc(ref Forms.Message m)
    {
        // Alt is an editing gesture here; don't let its release activate the system menu.
        if (m.Msg == 0x112 && (m.WParam.ToInt64() & 0xfff0) == 0xf100 &&
            _options.ConfigurationMode && _nameEditor is null && _submenu == ConfigurationSubmenu.None &&
            _calibration?.SelectedScreen?.ScreenId is { } selectedId && ScreenDefinition(selectedId)?.IsVideo == true)
        { m.Result = 0; return; }
        if (m.Msg == 0x201)
        {
            _pointerShift = (m.WParam.ToInt64() & 4) != 0;
            _pointerControl = (m.WParam.ToInt64() & 8) != 0;
            try { base.WndProc(ref m); }
            finally { _pointerShift = false; _pointerControl = false; }
            return;
        }
        // These Forms windows run on a WPF dispatcher, without the WinForms pre-translation loop.
        if (m.Msg is 0x100 or 0x104 && (Forms.Keys)(int)m.WParam is not (Forms.Keys.ShiftKey or Forms.Keys.ControlKey or Forms.Keys.Menu or Forms.Keys.LShiftKey or Forms.Keys.RShiftKey or Forms.Keys.LControlKey or Forms.Keys.RControlKey or Forms.Keys.LMenu or Forms.Keys.RMenu or Forms.Keys.Escape))
            _modifierFraming?.Suspend();
        if (m.Msg == 0x100 && (Forms.Keys)(int)m.WParam == Forms.Keys.J && (m.LParam.ToInt64() & (1L << 30)) != 0 && _options.ConfigurationMode && _nameEditor is null)
        { m.Result = 0; return; }
        if (m.Msg == 0x100 && HandleScreenKey((Forms.Keys)(int)m.WParam | ModifierKeys))
        {
            m.Result = 0;
            return;
        }
        if (_submenu != ConfigurationSubmenu.None && m.Msg is 0x102 or 0x106) { m.Result = 0; return; }
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
        _modifierPoll.Stop(); _modifierFraming.Finish();
        await _framing.DisposeAsync();
        _closing = true;
        FinishNameEdit(false);
        Hide();
        if(_ledTools is not null) { await _ledTools.ShutdownAsync(); _ledTools=null; }
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
        _modifierPoll.Stop(); _modifierFraming.Cancel("Shutdown");
        _framing.Stop();
        _closing = true;
        FinishNameEdit(false);
        _tasks.Cancel();
        Hide();
        if(_ledTools is not null) { _ledTools.Renderer.Dispose(); _ledTools.Close(); _ledTools.Dispose(); _ledTools=null; }
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
    [DllImport("user32.dll")] private static extern bool GetKeyboardState([Out] byte[] state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _modifierPoll?.Dispose(); _modifierFraming?.Cancel("WindowDisposed"); }
        base.Dispose(disposing);
        if (disposing) _applicationIcon.Dispose();
    }
}
