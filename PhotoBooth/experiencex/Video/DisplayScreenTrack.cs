using System.Diagnostics;
using System.Runtime.InteropServices;
using PhotoBooth.Diagnostics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ExperienceX;

// One editable screen and overlay on its display's worker. Video owns a decoder; mat owns cached geometry.
internal sealed class DisplayScreenTrack : IDisposable
{
    private readonly string _display;
    private readonly GraphicsResources _graphics;
    private readonly MatLayer _mat;
    private readonly Action _present;
    private readonly MediaEngineSession _media;
    private readonly CalibrationOverlay _overlay = new();
    private readonly PlaybackMetrics _metrics = new();
    private readonly VideoPlaybackTimeline _timeline = new();
    private readonly PointTransformCache _transforms = new();
    private readonly PointTransformCache _overlayTransforms = new();
    private readonly RendererStateMachine _state = new();
    private readonly Stopwatch _opening = new();
    private ExperienceOptions _options;
    private ID3D11Texture2D? _texture;
    private ID3D11ShaderResourceView? _view;
    private int _videoWidth, _videoHeight;
    private double _alpha;
    private string? _source;
    private double _lastPosition;
    private bool _loop;
    private int _loops;
    private readonly Action<int, int> _createVideoTexture;
    public ScreenOptions Screen
    {
        get; private set;
    }
    public string? RequestId
    {
        get; private set;
    }
    public bool IsOpen => _media.IsOpen;
    internal ScreenPlaybackSnapshot Snapshot => new(Screen.Id, Screen.Name, State, _media.Position, _alpha, _metrics.TransferredFrames, RequestId, _loops);
    public RendererState State => _state.Current;
    public bool Animating => _overlay.Animating(_options);
    public long TransformComputations => _transforms.Computations + _overlayTransforms.Computations;
    public DisplayScreenTrack(GraphicsResources graphics, string display, ScreenOptions screen, ExperienceOptions options, Action wake, Action present)
    {
        _graphics = graphics;
        _mat = new(graphics);
        _display = display;
        Screen = screen;
        _options = options;
        _present = present;
        _media = new(wake);
        _createVideoTexture = CreateVideoTexture;
        try
        {
            _overlay.Initialize(graphics);
            _overlay.Configure(options);
        }
        catch { Dispose(); throw; }
        _state.TransitionTo(RendererState.Idle);
    }
    public void Configure(ScreenOptions screen, ExperienceOptions options)
    {
        if (Screen.Type != screen.Type) Complete("ScreenTypeChanged");
        Screen = screen;
        _options = options;
        _overlay.Configure(options);
    }
    public void Show(int point, bool selected)
    {
        _overlay.Show(point);
        _overlay.SelectScreen(selected);
    }
    public void Fade() => _overlay.Fade();
    public void Preview(PointOptions? value) => _overlay.SetPreview(value);
    public void Flash(int mask, bool success) => _overlay.Flash(mask, success);
    public void FlashPoint(int point, bool success) => _overlay.FlashPoint(point, success);
    public bool Matches(PointOptions expected) => Screen.Points == expected && (_overlay.Preview is null || _overlay.Preview == expected);
    public void Open(string path, ExperienceRequest request)
    {
        if (Screen.IsMat) throw new ArgumentException("Mat screens cannot receive playback commands.");
        Complete("Replaced");
        RequestId = request.RequestId;
        _source = request.Source;
        _loop = request.Loop;
        _loops = 0;
        _lastPosition = 0;
        _state.TransitionTo(RendererState.Opening);
        _alpha = 0;
        _timeline.Reset(request.Loop);
        _metrics.Reset();
        _opening.Restart();
        _media.Open(path, request, _graphics.DeviceManager);
        Telemetry.Info("VideoOpening", new
        {
            Path = path,
            DisplayName = _display,
            ScreenName = Screen.Name,
            Muted = request.Mute,
            request.Loop,
            SurfaceOpacity = 0
        }, RequestId);
    }
    public void Tick()
    {
        _media.StartIfReady(_createVideoTexture);
        if (_media.TryGetFrame(out var timestamp) && timestamp != _metrics.LastTimestamp)
        {
            _graphics.Context.PSUnsetShaderResource(0);
            using var surface = _texture!.QueryInterface<IDXGISurface>();
            _media.TransferFrame(surface, _videoWidth, _videoHeight);
            _metrics.Transferred(timestamp);
            if (!_timeline.Started && _media.Position > 0)
            {
                Draw(0);
                _present(); // First advancing frame is submitted while this screen is fully transparent.
                _timeline.Start();
                _metrics.BeginPlayback();
                _state.TransitionTo(RendererState.Playing);
                Telemetry.Info("VideoPlaybackConfirmed", new
                {
                    DisplayName = _display,
                    ScreenName = Screen.Name,
                    PositionSeconds = _media.Position,
                    SurfaceOpacity = 0,
                    FirstFrameTimestamp = timestamp,
                    OpeningMilliseconds = _opening.Elapsed.TotalMilliseconds
                }, RequestId);
            }
        }
        if (!_timeline.Started)
        {
            if (_opening.Elapsed.TotalSeconds > 15)
                throw new TimeoutException("Video did not begin within 15 seconds.");
            Draw(0);
            return;
        }
        var position = _media.Position;
        if (_loop && position < _lastPosition - .05)
        {
            _loops++;
            Telemetry.Info("VideoLoopRepeated", new { DisplayName = _display, ScreenName = Screen.Name, Repetitions = _loops }, RequestId);
        }
        _lastPosition = position;
        var duration = _media.Duration;
        var frame = _timeline.Advance(_options, double.IsFinite(duration) && duration > 0 ? duration : null, _media.Position, _media.Ended);
        if (frame.FadeStarted)
        {
            _state.TransitionTo(RendererState.FadingOut);
            Telemetry.Info("VideoFadeOutStarted", new
            {
                PositionSeconds = _media.Position,
                SurfaceOpacity = _alpha,
                FadeSeconds = frame.FadeSeconds,
                Reason = frame.StopReason
            }, RequestId);
        }
        Draw(frame.Opacity);
        if (frame.Complete)
            Complete(frame.StopReason);
        else if (_metrics.ReportDue)
            Report();
    }
    private void Draw(double opacity)
    {
        _alpha = opacity;
    }
    public void StopPlayback(string? source, string playbackRequestId)
    {
        if (!_media.IsOpen || RequestId != playbackRequestId || !string.Equals((_source ?? "").Trim(), (source ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return;
        if (!_timeline.Started) { Complete("SceneStopped"); return; }
        _timeline.RequestStop(_options.TransitionOutSeconds);
    }
    public void DrawVideo()
    {
        if (Screen.IsMat) return;
        var points = _overlay.Preview ?? Screen.Points;
        var inverse = _alpha > 0 ? _transforms.Get(points) : default;
        _graphics.DrawLayer(_view, _alpha, _options, inverse, _videoWidth, _videoHeight, Screen.EffectiveBlackKey(_options), Screen.SizingMode, points);
    }
    public void DrawMat()
    {
        if (Screen.IsMat) _mat.Draw(_overlay.Preview ?? Screen.Points);
    }
    public void DrawOverlay()
    {
        if (_overlay.Visible(_options))
            _overlay.Draw(_graphics, _options, _display, _overlayTransforms, Screen.Id);
    }
    public void Presented()
    {
        if (_media.IsOpen)
            _metrics.Presented();
    }
    private void CreateVideoTexture(int width, int height)
    {
        _videoWidth = width;
        _videoHeight = height;
        _graphics.Context.PSUnsetShaderResource(0);
        _view?.Dispose();
        _texture?.Dispose();
        _texture = _graphics.Device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription(Vortice.DXGI.Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            Vortice.Direct3D11.BindFlags.RenderTarget | Vortice.Direct3D11.BindFlags.ShaderResource));
        _view = _graphics.Device.CreateShaderResourceView(_texture);
    }
    private void Report() => _metrics.Report(_graphics, _media, _display, RequestId, _alpha, TransformComputations, Screen.Name);
    public void Complete(string reason)
    {
        if (!_media.IsOpen)
        {
            _state.TransitionTo(RendererState.Idle);
            _alpha = 0;
            RequestId = null;
            return;
        }
        Draw(0);
        _present();
        _graphics.CommitAndWait();
        var flushResult = DwmFlush();
        Telemetry.Info("VideoTransparentFramePresented", new
        {
            DisplayName = _display,
            ScreenName = Screen.Name,
            SurfaceOpacity = 0,
            PositionSeconds = _media.Position,
            DwmFlushResult = flushResult
        }, RequestId);
        Report();
        Telemetry.Info("PlaybackStopped", new
        {
            DisplayName = _display,
            ScreenName = Screen.Name,
            SurfaceOpacity = 0,
            PositionSeconds = _media.Position,
            Reason = reason
        }, RequestId);
        _media.Dispose();
        _state.TransitionTo(RendererState.Idle);
        _alpha = 0;
        RequestId = null;
    }
    public void Dispose()
    {
        _media.Dispose();
        _overlay.Dispose();
        _mat.Dispose();
        _graphics.Context.PSUnsetShaderResource(0);
        _view?.Dispose();
        _texture?.Dispose();
    }
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
