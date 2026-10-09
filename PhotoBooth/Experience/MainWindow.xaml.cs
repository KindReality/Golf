using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using PhotoBooth.Diagnostics;
using System.Windows.Controls;

namespace Experience;

public partial class MainWindow : System.Windows.Window
{
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly System.Diagnostics.Stopwatch _playbackElapsed = new();

    private ExperienceOptions _options = new();

    private long _playbackVersion;
    private bool _closed;
    private bool _awaitingVideoPlayback;
    private readonly bool _developerMode;
    private string? _lastMonitorPlacement;
    private string? _activeRequestId;
    private volatile string _telemetryState = "Idle";
    private bool _started;
    private bool _placingMonitor;
    private bool _monitorAvailable;
    private string? _monitorFailure;
    private bool _scheduledFadeOut;
    private TopmostGuard? _topmostGuard;
    private PlaybackPerformanceProbe? _performanceProbe;

    private BlackKeyOptions? _appliedBlackKey;
    private KeystoneOptions? _appliedKeystone;
    private double _meshAspect;
    private readonly BlackKeyEffect _surfaceEffect = new();
    private EventHandler? _transparentCompletion;
    private bool _finishingPlayback;

    internal string MonitorName { get; }
    internal string State => _telemetryState;

    internal MainWindow(string monitorName, ExperienceOptions options)
    {
        MonitorName = monitorName;
        _options = options;
        _developerMode = options.DeveloperMode;
        InitializeComponent();
        ApplyBlackKey();

        if (_developerMode)
        {
            AllowsTransparency = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Width = 800;
            Height = 600;
            Topmost = false;
            ShowActivated = true;
        }
        Loaded += (_, _) => PlaceOnMonitor();
        SizeChanged += (_, _) => ApplyKeystone();
        Title = $"Experience — {MonitorName}";
        SourceInitialized += (_, _) => { PlaceOnMonitor(); ApplyKeystone(); };
        _playbackTimer.Tick += (_, _) => CheckPlaybackLimit();

    }

    internal void Start()
    {
        // Create the native handle without showing a window on a fallback display.
        new WindowInteropHelper(this).EnsureHandle();
        _started = true;
        PlaceOnMonitor();
        if (!_developerMode)
            _topmostGuard = new TopmostGuard(this, () => !_closed && _monitorAvailable && IsVisible && _activeRequestId is not null,
                () => _activeRequestId);
    }

    internal void RefreshMonitor() => PlaceOnMonitor();
    internal void ShutdownWindow() { if (!_closed) Close(); }

    internal void UpdateOptions(ExperienceOptions next)
    {
        if (_closed) return;
        if (_scheduledFadeOut && (next.MaxVideoPlaybackLength != _options.MaxVideoPlaybackLength ||
            next.TransitionOutSeconds != _options.TransitionOutSeconds))
        {
            ++_playbackVersion;
            var currentOpacity = _surfaceEffect.FadeOpacity;
            _surfaceEffect.BeginAnimation(BlackKeyEffect.FadeOpacityProperty, null);
            _surfaceEffect.FadeOpacity = currentOpacity;
            _scheduledFadeOut = false;
            _awaitingVideoPlayback = true;
        }
        _options = next;
        ApplyBlackKey();
        ApplyKeystone();
        CheckPlaybackLimit();
    }

    internal void PlayVideo(string path, ExperienceRequest request)
    {
        if (_closed) return;
        var requestId = request.RequestId;
        PlaceOnMonitor();
        if (!_monitorAvailable)
        {
            Telemetry.Warning("ExperienceBlockedMonitorUnavailable", new { MonitorName }, requestId);
            return;
        }
        try
        {
            CancelTransparentCompletion();
            ++_playbackVersion;
            _performanceProbe?.Stop(VideoPlayer.Position.TotalSeconds);
            _performanceProbe = null;
            if (_activeRequestId is not null)
                Telemetry.Info("PlaybackReplaced", new { NextRequestId = requestId, ElapsedSeconds = _playbackElapsed.Elapsed.TotalSeconds }, _activeRequestId);
            _activeRequestId = requestId;
            _topmostGuard?.EnsureTopmost("PlaybackRequested", force: true);
            _telemetryState = "Opening";
            _playbackTimer.Stop();
            _playbackElapsed.Reset();
            _awaitingVideoPlayback = false;
            _scheduledFadeOut = false;
            // Cancel the old fade and isolate media callbacks from the previous request.
            _surfaceEffect.BeginAnimation(BlackKeyEffect.FadeOpacityProperty, null);
            _surfaceEffect.FadeOpacity = 0;
            var previousPlayer = VideoPlayer;
            previousPlayer.MediaOpened -= VideoOpened;
            previousPlayer.MediaEnded -= VideoEnded;
            previousPlayer.MediaFailed -= VideoFailed;
            previousPlayer.Close();
            VideoSurface.Children.Remove(previousPlayer);
            VideoPlayer = new MediaElement
            {
                LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Stop,
                Stretch = Stretch.Uniform, IsHitTestVisible = false, IsMuted = request.Mute
            };
            VideoPlayer.MediaOpened += VideoOpened;
            VideoPlayer.MediaEnded += VideoEnded;
            VideoPlayer.MediaFailed += VideoFailed;
            VideoSurface.Children.Add(VideoPlayer);
            VideoPlayer.Source = new Uri(path);
            // Native window opacity stays at 1; the output shader emits alpha 0.
            // This keeps the media render path active before the visible fade.
            VideoPlayer.Play();
            Telemetry.Info("PlaybackRequested", new { Path = path, Bytes = new FileInfo(path).Length, MonitorName, request.Mute }, _activeRequestId);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            Telemetry.Error("PlaybackRequestFailed", ex, new { Value = request.Value, MonitorName }, requestId);
        }
    }

    private void VideoOpened(object sender, RoutedEventArgs e)
    {
        if (!_monitorAvailable || !ReferenceEquals(sender, VideoPlayer)) return;
        ++_playbackVersion; // Cancel a pending fade-out completion.
        Log($"Video opened: {VideoPlayer.NaturalVideoWidth}x{VideoPlayer.NaturalVideoHeight}");
        _telemetryState = "Playing";
        _topmostGuard?.EnsureTopmost("MediaOpened", force: true);
        Telemetry.Info("MediaOpened", new { MonitorName, Width = VideoPlayer.NaturalVideoWidth, Height = VideoPlayer.NaturalVideoHeight,
            DurationSeconds = VideoPlayer.NaturalDuration.HasTimeSpan ? (double?)VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds : null }, _activeRequestId);
        _playbackElapsed.Restart();
        _performanceProbe = new PlaybackPerformanceProbe(_activeRequestId);
        _awaitingVideoPlayback = true;
        _playbackTimer.Start();
    }

    private void CheckPlaybackLimit()
    {
        if (!_playbackElapsed.IsRunning || _finishingPlayback) return;
        var plan = PlaybackTiming.Evaluate(_options.MaxVideoPlaybackLength, _playbackElapsed.Elapsed.TotalSeconds,
            VideoPlayer.NaturalDuration.HasTimeSpan ? VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds : null,
            VideoPlayer.Position.TotalSeconds, _options.TransitionOutSeconds);
        if (plan.RemainingSeconds <= 0)
        {
            FinishPlayback(plan.StopReason);
            return;
        }
        if (!_scheduledFadeOut && plan.ShouldFade)
        {
            _scheduledFadeOut = true;
            _awaitingVideoPlayback = false;
            _telemetryState = "FadingOut";
            var version = _playbackVersion;
            Telemetry.Info("PlaybackFadeOutScheduled", new { plan.StopReason, plan.RemainingSeconds, plan.FadeSeconds }, _activeRequestId);
            FadeTo(0, plan.FadeSeconds, () =>
            {
                if (version == _playbackVersion) FinishPlayback(plan.StopReason);
            });
        }
        if (!_scheduledFadeOut && _awaitingVideoPlayback && VideoPlayer.Position > TimeSpan.Zero)
        {
            _awaitingVideoPlayback = false;
            Telemetry.Info("VideoPlaybackConfirmed", new { MonitorName, PositionSeconds = VideoPlayer.Position.TotalSeconds,
                SurfaceOpacity = _surfaceEffect.FadeOpacity }, _activeRequestId);
            Log($"Video advancing; starting {_options.TransitionInSeconds}-second fade-in.");
            FadeTo(1, _options.TransitionInSeconds);
        }
    }

    private void VideoEnded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, VideoPlayer) || _activeRequestId is null) return;
        Log("Video ended.");
        FinishPlayback("MediaEnded");
    }
    private void VideoFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, VideoPlayer) || _activeRequestId is null) return;
        Telemetry.Error("PlaybackFailed", e.ErrorException, requestId: _activeRequestId);
        ReturnToIdle("MediaFailed");
    }

    private void ReturnToIdle(string reason)
    {
        _scheduledFadeOut = false;
        _telemetryState = "FadingOut";
        _playbackTimer.Stop();
        _awaitingVideoPlayback = false;
        var version = ++_playbackVersion;
        FadeTo(0, _options.TransitionOutSeconds, () =>
        {
            if (version == _playbackVersion) FinishPlayback(reason);
        });
    }

    private void FinishPlayback(string reason)
    {
        if (_finishingPlayback) return;
        _finishingPlayback = true;
        ++_playbackVersion;
        _playbackTimer.Stop();
        _playbackElapsed.Stop();
        _awaitingVideoPlayback = false;
        _scheduledFadeOut = false;
        _surfaceEffect.BeginAnimation(BlackKeyEffect.FadeOpacityProperty, null);
        _surfaceEffect.FadeOpacity = 0;
        _telemetryState = "CompletingTransparentFrame";
        Telemetry.Info("VideoTransparent", new { MonitorName, SurfaceOpacity = _surfaceEffect.FadeOpacity,
            PositionSeconds = VideoPlayer.Position.TotalSeconds }, _activeRequestId);
        // Rendering fires before a scene is submitted. Keep the live player through
        // the following distinct rendering tick so clearing it cannot freeze a visible frame.
        TimeSpan? previousFrame = null;
        var frames = 0;
        _transparentCompletion = (_, args) =>
        {
            if (args is not RenderingEventArgs frame || previousFrame == frame.RenderingTime) return;
            previousFrame = frame.RenderingTime;
            if (++frames < 2) return;
            CancelTransparentCompletion();
            _performanceProbe?.Stop(VideoPlayer.Position.TotalSeconds);
            _performanceProbe = null;
            Telemetry.Info("PlaybackStopped", new { Reason = reason, MonitorName,
                ElapsedSeconds = _playbackElapsed.Elapsed.TotalSeconds, PositionSeconds = VideoPlayer.Position.TotalSeconds,
                SurfaceOpacity = _surfaceEffect.FadeOpacity }, _activeRequestId);
            VideoPlayer.Close();
            _playbackElapsed.Reset();
            _activeRequestId = null;
            _telemetryState = "Idle";
        };
        CompositionTarget.Rendering += _transparentCompletion;
    }

    private void CancelTransparentCompletion()
    {
        if (_transparentCompletion is not null) CompositionTarget.Rendering -= _transparentCompletion;
        _transparentCompletion = null;
        _finishingPlayback = false;
    }

    // Same cubic opacity transitions used by Camera, with configurable timing.
    private void FadeTo(double target, double seconds, Action? completed = null)
    {
        var requestId = _activeRequestId;
        Telemetry.Info("FadeStarted", new { MonitorName, From = _surfaceEffect.FadeOpacity, To = target, Seconds = seconds,
            PositionSeconds = VideoPlayer.Position.TotalSeconds }, requestId);
        var animation = new DoubleAnimation(_surfaceEffect.FadeOpacity, target, TimeSpan.FromSeconds(seconds))
        {
            EasingFunction = new CubicEase { EasingMode = target > 0 ? EasingMode.EaseOut : EasingMode.EaseIn }
        };
        var version = _playbackVersion;
        animation.Completed += (_, _) =>
        {
            if (version != _playbackVersion || requestId != _activeRequestId) return;
            Telemetry.Info("FadeCompleted", new { MonitorName, To = target, Seconds = seconds,
                PositionSeconds = VideoPlayer.Position.TotalSeconds, SurfaceOpacity = _surfaceEffect.FadeOpacity }, requestId);
            completed?.Invoke();
        };
        _surfaceEffect.BeginAnimation(BlackKeyEffect.FadeOpacityProperty, animation);
    }

    private void ApplyKeystone()
    {
        var aspect = ActualHeight > 0 ? ActualWidth / ActualHeight : 0;
        if (_appliedKeystone == _options.Keystone && _meshAspect == aspect) return;
        if (!_options.Keystone.Enabled)
        {
            _appliedKeystone = _options.Keystone;
            _meshAspect = aspect;
            KeystoneOutput.Visibility = Visibility.Collapsed;
            KeystoneViewport.Children.Clear();
            return;
        }
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        // A dense projective mesh avoids the diagonal distortion of a two-triangle quad.
        // Rebuilt only for configuration/display changes; WPF renders the live video brush.
        const int divisions = 64;
        var mesh = new MeshGeometry3D();
        for (var row = 0; row <= divisions; row++)
        for (var column = 0; column <= divisions; column++)
        {
            var u = (double)column / divisions;
            var v = (double)row / divisions;
            var point = _options.Keystone.Map(u, v);
            mesh.Positions.Add(new Point3D(point.X * aspect, 1 - point.Y, 0));
            mesh.TextureCoordinates.Add(new System.Windows.Point(u, v));
        }
        for (var row = 0; row < divisions; row++)
        for (var column = 0; column < divisions; column++)
        {
            var topLeft = row * (divisions + 1) + column;
            var bottomLeft = topLeft + divisions + 1;
            mesh.TriangleIndices.Add(topLeft);
            mesh.TriangleIndices.Add(bottomLeft);
            mesh.TriangleIndices.Add(topLeft + 1);
            mesh.TriangleIndices.Add(topLeft + 1);
            mesh.TriangleIndices.Add(bottomLeft);
            mesh.TriangleIndices.Add(bottomLeft + 1);
        }
        mesh.Freeze();
        var brush = new VisualBrush(VideoSurface) { Stretch = Stretch.Fill };
        var material = new EmissiveMaterial(brush);
        KeystoneViewport.Camera = new OrthographicCamera(
            new Point3D(aspect / 2, 0.5, 2), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), aspect);
        KeystoneViewport.Children.Clear();
        KeystoneViewport.Children.Add(new ModelVisual3D
        {
            Content = new GeometryModel3D(mesh, material) { BackMaterial = material }
        });
        KeystoneOutput.Visibility = Visibility.Visible;
        _appliedKeystone = _options.Keystone;
        _meshAspect = aspect;
        Telemetry.Info("KeystoneMeshConfigured", new { Aspect = aspect, Divisions = divisions });
    }

    private void ApplyBlackKey()
    {
        if (_appliedBlackKey == _options.BlackKey) return;
        // Key the final surface, including the black letterboxing and keystone backing.
        _surfaceEffect.Threshold = _options.BlackKey.Threshold;
        _surfaceEffect.Softness = _options.BlackKey.Softness;
        _surfaceEffect.KeyEnabled = _options.BlackKey.Enabled ? 1 : 0;
        OutputSurface.Effect = _surfaceEffect;
        _appliedBlackKey = _options.BlackKey;
        Telemetry.Info("BlackKeyConfigured", new { _options.BlackKey.Enabled, _options.BlackKey.Threshold,
            _options.BlackKey.Softness, DesktopTransparency = !_developerMode });
    }

    private void PlaceOnMonitor()
    {
        if (_closed || _placingMonitor) return;
        _placingMonitor = true;
        try
        {
        var screens = Forms.Screen.AllScreens;
        var screen = screens.FirstOrDefault(s => string.Equals(s.DeviceName, MonitorName, StringComparison.OrdinalIgnoreCase));
        if (screen is null)
        {
            HideForMonitorFailure("AssignedMonitorUnavailable");
            return;
        }
        var bounds = screen.Bounds;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var placement = $"{screen.DeviceName}:{bounds}:{_developerMode}";
        if (_lastMonitorPlacement == placement && _monitorAvailable)
        {
            if (_started && !IsVisible) Show();
            return;
        }
        var width = _developerMode ? Math.Min(800, bounds.Width) : bounds.Width;
        var height = _developerMode ? Math.Min(600, bounds.Height) : bounds.Height;
        var x = bounds.X + (bounds.Width - width) / 2;
        var y = bounds.Y + (bounds.Height - height) / 2;
        if (!SetWindowPos(handle, new IntPtr(_developerMode ? -2 : -1), x, y, width, height, 0x0010))
            HideForMonitorFailure($"MonitorPlacementFailed:{Marshal.GetLastWin32Error()}");
        else
        {
            _lastMonitorPlacement = placement;
            _monitorAvailable = true;
            if (_monitorFailure is not null)
            {
                Telemetry.Info("MonitorRestored", new { Monitor = screen.DeviceName });
                _telemetryState = "Idle";
            }
            _monitorFailure = null;
            Telemetry.Info("MonitorSelected", new { Monitor = screen.DeviceName, DeveloperMode = _developerMode, x, y, width, height });
            if (_started)
            {
                ShowInTaskbar = true;
                if (!IsVisible) Show();
            }
        }
        }
        finally { _placingMonitor = false; }
    }

    private void HideForMonitorFailure(string reason)
    {
        var failure = $"{MonitorName}:{reason}";
        if (_monitorFailure == failure && !_monitorAvailable) return;
        if (_monitorFailure != failure)
            Telemetry.Warning("MonitorUnavailable", new { AssignedMonitor = MonitorName, Reason = reason, RetrySeconds = 1 });
        _monitorFailure = failure;
        _performanceProbe?.Stop(VideoPlayer.Position.TotalSeconds);
        _performanceProbe = null;
        _monitorAvailable = false;
        _lastMonitorPlacement = null;
        // Cancel playback and animation before hiding so a reconnect cannot reveal stale video.
        ++_playbackVersion;
        _playbackTimer.Stop();
        _playbackElapsed.Reset();
        _awaitingVideoPlayback = false;
        _scheduledFadeOut = false;
        CancelTransparentCompletion();
        _surfaceEffect.BeginAnimation(BlackKeyEffect.FadeOpacityProperty, null);
        _surfaceEffect.FadeOpacity = 0;
        VideoPlayer.Close();
        _activeRequestId = null;
        _telemetryState = "WaitingForMonitor";
        ShowInTaskbar = false;
        if (IsVisible) Hide();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    private static void Log(string message)
    {
        Telemetry.Info("ExperienceDiagnostic", new { Message = message });
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        CancelTransparentCompletion();
        _performanceProbe?.Stop(VideoPlayer.Position.TotalSeconds);
        _topmostGuard?.Dispose();

        _playbackTimer.Stop();
        _playbackElapsed.Reset();

        VideoPlayer.Close();
        base.OnClosed(e);
    }
}
