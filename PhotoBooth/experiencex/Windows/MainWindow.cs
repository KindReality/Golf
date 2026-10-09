using System.Windows.Threading;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;
using PhotoBooth.Diagnostics;

namespace ExperienceX;

// Only window management runs on the UI dispatcher. Decoding and rendering own a worker thread.
public sealed class MainWindow : Forms.Form, IKeystoneSurface
{
    private readonly string _monitor;
    // Form's base constructor calls CreateParams before this constructor assigns the supplied options.
    private ExperienceOptions _options = new();
    private VideoRenderer? _renderer;
    private TopmostGuard? _guard;
    private bool _available;
    private KeystoneCalibration? _calibration;
    private bool _mouseHeld;
    private readonly RendererRecovery _recovery = new();
    private bool _recovering, _waitingForRenderer, _closing;
    private readonly Action? _beforePresent;
    private readonly AsyncTaskScope _tasks = new("Monitor window");
    internal void AttachCalibration(KeystoneCalibration calibration) => _calibration = calibration;
    internal string MonitorName => _monitor;
    string IKeystoneSurface.MonitorName => _monitor;
    int IKeystoneSurface.PixelWidth => ClientSize.Width;
    int IKeystoneSurface.PixelHeight => ClientSize.Height;
    void IKeystoneSurface.ShowCalibration(int point) => ShowCalibration(point);
    void IKeystoneSurface.FadeCalibration() => FadeCalibration();
    void IKeystoneSurface.FlashCalibration(int point, bool success) => FlashCalibration(point, success);
    void IKeystoneSurface.FlashCalibrationPoints(int pointMask)
    {
        _renderer?.FlashCalibrationPoints(pointMask, true);
        _guard?.EnsureTopmost("RemoteCalibrationSaved", true);
    }
    void IKeystoneSurface.PreviewKeystone(KeystoneOptions? value) => PreviewKeystone(value);
    Task<bool> IKeystoneSurface.ConfirmCalibrationAsync(KeystoneOptions expected) => _renderer?.ConfirmKeystoneAsync(expected) ?? Task.FromResult(false);
    void IKeystoneSurface.ReleaseCalibrationCapture() => ReleaseCalibrationCapture();
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
    public MainWindow(string monitor, ExperienceOptions options) : this(monitor, options, null) { }
    internal MainWindow(string monitor, ExperienceOptions options, Action? beforePresent)
    {
        _beforePresent = beforePresent;
        _monitor = monitor;
        _options = options;
        Text = "ExperienceX — " + monitor;
        FormBorderStyle = options.DeveloperMode ? Forms.FormBorderStyle.Sizable : Forms.FormBorderStyle.None;
        ShowInTaskbar = options.DeveloperMode;
        TopMost = !options.DeveloperMode;
        StartPosition = Forms.FormStartPosition.Manual;
    }
    public void Start()
    {
        RefreshMonitor();
        var dispatcher = Dispatcher.CurrentDispatcher;
        _renderer = CreateRenderer(dispatcher);
        _guard = new TopmostGuard(Handle, Dispatcher.CurrentDispatcher, () => Visible && (State != RendererState.Idle || _options.ConfigurationMode || MonitorBackground.For(_monitor, _options).A > 0), () => _renderer?.RequestId);
        ClientSizeChanged += (_, _) => _renderer?.Resize(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        if (_available)
            ShowSurface();
    }
    public void RefreshMonitor()
    {
        if (_closing)
            return;
        var screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName.Equals(_monitor, StringComparison.OrdinalIgnoreCase));
        if (screen is null)
        {
            if (_available)
            {
                _calibration?.Cancel("MonitorUnavailable");
                Hide();
                _renderer?.Stop("MonitorUnavailable");
            }
            if (_available || _renderer is null)
                Telemetry.Warning("MonitorUnavailable", new
                {
                    MonitorName = _monitor
                });
            _available = false;
            return;
        }
        var bounds = _options.DeveloperMode ? new System.Drawing.Rectangle(screen.WorkingArea.Location, new(800, 600)) : screen.Bounds;
        if (Bounds != bounds)
            Bounds = bounds;
        _available = true;
        if (_renderer?.State == RendererState.Failed)
        {
            Hide();
            if (_recovery.NextAttempt == DateTimeOffset.MaxValue && !_recovering)
                _recovery.Failed(DateTimeOffset.UtcNow);
            if (!_recovering && _recovery.Due(DateTimeOffset.UtcNow))
                _tasks.Run(_ => RecoverRendererAsync(), "Recover renderer");
            return;
        }
        if (_waitingForRenderer && _renderer?.State == RendererState.Idle)
        {
            _waitingForRenderer = false;
            _recovery.Ready();
            ShowSurface();
            Telemetry.Info("RendererRecovered", new
            {
                MonitorName = _monitor
            });
        }
        else if (!Visible && !_recovering && !_waitingForRenderer && _renderer is not null)
            ShowSurface();
    }
    private VideoRenderer CreateRenderer(Dispatcher dispatcher) => new(Handle, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height), _options, _monitor,
        () => { if (!_closing && !dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(() => { if (!_closing) Hide(); })); }, _beforePresent);
    private async Task RecoverRendererAsync()
    {
        _recovering = true;
        _recovery.Begin();
        Hide();
        _calibration?.Cancel("RendererRecovery");
        var old = _renderer;
        Telemetry.Warning("RendererRecoveryStarted", new
        {
            MonitorName = _monitor,
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
                MonitorName = _monitor,
                RetryAt = _recovery.NextAttempt
            });
        }
        finally { _recovering = false; }
    }
    public void UpdateOptions(ExperienceOptions options)
    {
        var changed = _options.ConfigurationMode != options.ConfigurationMode;
        _options = options;
        _renderer?.Configure(options);
        if (changed && IsHandleCreated)
        {
            var style = GetWindowLongPtr(Handle, -20).ToInt64();
            SetWindowLongPtr(Handle, -20, new nint(options.ConfigurationMode ? style & ~0x08000000L : style | 0x08000000L));
            if (!options.ConfigurationMode)
                ReleaseCalibrationCapture();
        }
        RefreshMonitor();
    }
    internal void ShowCalibration(int point)
    {
        _renderer?.ShowCalibration(point);
        _guard?.EnsureTopmost("ConfigurationMode", true);
    }
    internal void FadeCalibration() => _renderer?.FadeCalibration();
    internal void FlashCalibration(int point, bool success) => _renderer?.FlashCalibration(point, success);
    internal void PreviewKeystone(KeystoneOptions? value) => _renderer?.PreviewKeystone(value);
    internal void ReleaseCalibrationCapture()
    {
        _mouseHeld = false;
        Capture = false;
    }
    protected override void OnMouseDown(Forms.MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!_options.ConfigurationMode || e.Button != Forms.MouseButtons.Left)
            return;
        _calibration?.Begin(this, (double)e.X / Math.Max(1, ClientSize.Width - 1), (double)e.Y / Math.Max(1, ClientSize.Height - 1));
        _mouseHeld = true;
        Capture = true;
        Activate();
        Focus();
    }
    protected override void OnMouseMove(Forms.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_mouseHeld)
            _calibration?.Move(this, (double)e.X / Math.Max(1, ClientSize.Width - 1), (double)e.Y / Math.Max(1, ClientSize.Height - 1));
    }
    protected override void OnMouseUp(Forms.MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != Forms.MouseButtons.Left || !_mouseHeld)
            return;
        ReleaseCalibrationCapture();
        _calibration?.RequestFinish(this);
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
    protected override bool ProcessCmdKey(ref Forms.Message msg, Forms.Keys keyData) =>
        _calibration?.Key(this, keyData) == true || base.ProcessCmdKey(ref msg, keyData);
    protected override void WndProc(ref Forms.Message m)
    {
        // These Forms windows run on a WPF dispatcher, without the WinForms pre-translation loop.
        if (m.Msg == 0x100 && _calibration?.Key(this, (Forms.Keys)(int)m.WParam | ModifierKeys) == true)
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
            throw new InvalidOperationException("Assigned monitor is unavailable.");
        if (State == RendererState.Failed || _recovering || _waitingForRenderer)
            throw new InvalidOperationException("The DirectX renderer is recovering. Retry with a new request ID once ready.");
        _renderer!.Play(path, request);
        _guard?.EnsureTopmost("VideoRequest", true);
    }
    // Production awaits recovery and worker shutdown without blocking the dispatcher.
    public async Task ShutdownWindowAsync()
    {
        _closing = true;
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
        Telemetry.Info("MonitorSurfaceVisible", new
        {
            MonitorName = _monitor,
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
