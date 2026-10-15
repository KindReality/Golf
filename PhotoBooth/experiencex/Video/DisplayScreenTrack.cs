using System.Diagnostics;
using System.Runtime.InteropServices;
using PhotoBooth.Diagnostics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ExperienceX;

// One editable screen and overlay on its display's worker. Video owns a decoder; outlines use cached triangles.
internal sealed class DisplayScreenTrack : IDisposable
{
    private readonly string _display;
    private readonly GraphicsResources _graphics;
    private readonly MaskLayer _mask;
    private readonly PolygonMesh _videoOutline;
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
    private ScreenMediaBinding? _binding;
    private VideoPriorityLease? _priority;
    private VideoLoopIdentity? _loopIdentity;
    private bool _stillImage;
    private string? _mediaCaption, _playbackMedia;
    internal void SetMediaCaption(string? caption) => _mediaCaption=caption;
    private VideoFramingOptions? _framingPreview;
    private readonly VideoOutlineCache _outlineCache = new();
    private long _retryAt;
    private readonly Action<int, int> _createVideoTexture;
    public ScreenOptions Screen
    {
        get; private set;
    }
    public string? RequestId
    {
        get; private set;
    }
    public bool IsOpen => _media.IsOpen || _stillImage;
    public bool IsVideoOpen => _media.IsOpen;
    public bool NameLocked => _binding is not null;
    internal ScreenPlaybackSnapshot Snapshot => new(Screen.Id, Screen.Name, State, _media.Position, _alpha, _stillImage ? 1 : _metrics.TransferredFrames, RequestId, _loops, NameLocked, _binding?.Path, Screen.Brightness);
    public RendererState State => _state.Current;
    public bool Animating => _overlay.Animating(_options) || _stillImage && _alpha < 1;
    internal bool ContentAnimating => _stillImage && _alpha < 1;
    public long TransformComputations => _transforms.Computations + _overlayTransforms.Computations;
    public DisplayScreenTrack(GraphicsResources graphics, string display, ScreenOptions screen, ExperienceOptions options, Action wake, Action present)
    {
        _graphics = graphics;
        _mask = new(graphics);
        _videoOutline = new(graphics);
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
        if (screen.Framing != Screen.Framing || !options.ConfigurationMode) _framingPreview = null;
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
    internal void SuppressHelp(bool value) => _overlay.SuppressHelp(value);
    public void ShowJoinTarget(int point) => _overlay.ShowJoinTarget(point);
    internal void PreviewFraming(VideoFramingOptions? value) => _framingPreview = value;
    public void Preview(PointOptions? value) => _overlay.SetPreview(value);
    public void Flash(int mask, bool success) => _overlay.Flash(mask, success);
    public void FlashPoint(int point, bool success) => _overlay.FlashPoint(point, success);
    public bool Matches(PointOptions expected) => Screen.Points == expected && (_overlay.Preview is null || _overlay.Preview == expected);
    public void ApplyMediaBinding(ScreenMediaBinding? binding, bool available)
    {
        if (_binding != binding)
        {
            Complete(binding is null ? "ScreenMediaUnlocked" : "ScreenMediaChanged");
            _binding = binding;
            _retryAt = 0;
            Telemetry.Info(binding is null ? "ScreenMediaUnlocked" : "ScreenMediaLocked", new
            {
                DisplayName = _display, ScreenName = Screen.Name, ScreenId = Screen.Id,
                Path = binding?.Path, binding?.IsImage, binding?.Error
            });
            if (binding?.Error is { } error)
                Telemetry.Warning("ScreenMediaBindingAmbiguous", new { DisplayName = _display, ScreenName = Screen.Name, Reason = error });
        }
        if (!available || IsOpen || binding?.Path is not { } path || Stopwatch.GetTimestamp() < _retryAt) return;
        var request = new ExperienceRequest
        {
            Experience = "video", Value = binding.RelativePath ?? System.IO.Path.GetFileName(path), Loop = true, Mute = true,
            Source = _options.EffectiveDeviceName, TargetDevice = _options.EffectiveDeviceName,
            RequestId = Guid.NewGuid().ToString("N"), ScreenId = Screen.Id, ScreenName = Screen.Name
        };
        Telemetry.SetRequestContext(request.RequestId, request.LogContext(path));
        try
        {
            if (binding.IsImage) OpenImage(path, request);
            else Open(path, request);
            Telemetry.Info("ScreenMediaPlaybackStarted", new { DisplayName = _display, ScreenName = Screen.Name, Path = path, binding.IsImage, Loop = !binding.IsImage }, RequestId);
        }
        catch (Exception ex)
        {
            Complete("Error");
            Telemetry.Error("ScreenMediaPlaybackFailed", ex, new { DisplayName = _display, ScreenName = Screen.Name, Path = path, RetrySeconds = 5 }, request.RequestId);
            // Device failures must still reach the renderer's existing recovery path.
            if (unchecked((uint)ex.HResult) is 0x887A0005 or 0x887A0006 or 0x887A0007 or 0x887A0020) throw;
        }
    }
    public void ReportIgnoredCommand(ExperienceRequest request) => Telemetry.Info("VideoCommandIgnoredScreenLocked", new
    {
        DisplayName = _display, ScreenId = Screen.Id, ScreenName = Screen.Name,
        BoundPath = _binding?.Path, RequestedVideo = request.Value, request.Command
    }, request.RequestId);
    private void OpenImage(string path, ExperienceRequest request)
    {
        Complete("Replaced");
        _playbackMedia=request.Value;
        RequestId = request.RequestId;
        _source = request.Source;
        _state.TransitionTo(RendererState.Opening);
        var image = StillImagePixels.Read(path);
        _graphics.Context.PSUnsetShaderResource(0);
        _view?.Dispose(); _view = null;
        _texture?.Dispose(); _texture = null;
        _texture = _graphics.Device.CreateTexture2D<byte>(image.Bgra, Vortice.DXGI.Format.B8G8R8A8_UNorm,
            (uint)image.Width, (uint)image.Height, 1, 1, BindFlags.ShaderResource);
        _view = _graphics.Device.CreateShaderResourceView(_texture);
        _videoWidth = image.Width; _videoHeight = image.Height;
        _stillImage = true;
        _loops = 0;
        _timeline.Reset(true);
        Draw(0); _present();
        _timeline.Start();
        _state.TransitionTo(RendererState.Playing);
        Telemetry.Info("ImagePlaybackConfirmed", new { DisplayName = _display, ScreenName = Screen.Name, Path = path, image.Width, image.Height, SurfaceOpacity = 0 }, RequestId);
    }
    public void Open(string path, ExperienceRequest request)
    {
        if (!Screen.IsVideo) throw new ArgumentException("Only video screens receive playback commands.");
        var looping = VideoLoopIdentity.For(path, _display, request);
        if (_media.IsOpen && !_timeline.Stopping && looping is not null && _loopIdentity == looping)
        {
            var previousRequestId = RequestId;
            var previousPriority = _priority;
            _priority = request.VideoPriority;
            previousPriority?.Dispose();
            RequestId = request.RequestId;
            _source = request.Source;
            Telemetry.Info("VideoLoopContinued", new
            {
                Path = path, DisplayName = _display, ScreenName = Screen.Name,
                PreviousRequestId = previousRequestId, PositionSeconds = _media.Position, SurfaceOpacity = _alpha, request.IsAmbient
            }, RequestId);
            return;
        }
        Complete("Replaced");
        _priority = request.VideoPriority;
        _loopIdentity = looping;
        _playbackMedia=request.Value;
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
            request.IsAmbient,
            SurfaceOpacity = 0
        }, RequestId);
    }
    public void Tick()
    {
        if (StopInterrupted()) return;
        if (_stillImage)
        {
            Draw(_timeline.Advance(_options, null, 0, false).Opacity);
            return;
        }
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
        if (NameLocked)
        {
            if (RequestId == playbackRequestId && string.Equals(_source, source, StringComparison.OrdinalIgnoreCase))
                Telemetry.Info("VideoStopIgnoredScreenLocked", new { DisplayName = _display, ScreenName = Screen.Name, BoundPath = _binding?.Path, PlaybackRequestId = playbackRequestId }, RequestId);
            return;
        }
        if (!_media.IsOpen || RequestId != playbackRequestId || !string.Equals((_source ?? "").Trim(), (source ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return;
        if (!_timeline.Started) { Complete("SceneStopped"); return; }
        _timeline.RequestStop(_options.TransitionOutSeconds);
    }
    public void DrawVideo()
    {
        if (!Screen.IsVideo) return;
        var points = _overlay.Preview ?? Screen.Points;
        var inverse = _alpha > 0 ? _transforms.Get(points) : default;
        var (aspect,framing) = _outlineCache.Get(points,_graphics.Width,_graphics.Height,_framingPreview ?? Screen.Framing);
        var polygon = points.Vertices.Count > 4;
        if (polygon) _videoOutline.Configure(points);
        _graphics.DrawLayer(_view, _alpha, _options, inverse, Screen.EffectiveBlackKey(_options), _stillImage, Screen.Brightness,
            Screen.Perspective?.Rectangle, framing,aspect,polygon ? _videoOutline : null);
    }
    public void DrawMask()
    {
        if (Screen.IsMask) _mask.Draw(_overlay.Preview ?? Screen.Points, Screen.IsVisualization || Screen.IsNegative, cutout: Screen.IsMat);
    }
    internal bool SelectedScreen => _overlay.SelectedScreen;
    public void DrawOverlay()
    {
        _overlay.SetMediaCaption(Screen.IsVideo ? _mediaCaption ?? _binding?.RelativePath ?? Screen.MediaFile ?? _playbackMedia : null);
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
    internal bool StopInterrupted()
    {
        if (_priority?.IsInterrupted != true) return false;
        Complete("NormalVideoPriority");
        return true;
    }
    public void Complete(string reason)
    {
        try { CompleteCore(reason); }
        finally
        {
            _priority?.Dispose();
            _priority = null;
            _loopIdentity = null;
        }
    }
    private void CompleteCore(string reason)
    {
        if (reason == "Error") _retryAt = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        if (!IsOpen)
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
        if (!_stillImage) Report();
        Telemetry.Info("PlaybackStopped", new
        {
            DisplayName = _display,
            ScreenName = Screen.Name,
            SurfaceOpacity = 0,
            PositionSeconds = _media.Position,
            Reason = reason
        }, RequestId);
        _media.Dispose();
        _stillImage = false;
        _state.TransitionTo(RendererState.Idle);
        _alpha = 0;
        RequestId = null;
    }
    public void Dispose()
    {
        _priority?.Dispose();
        _priority = null;
        _media.Dispose();
        _overlay.Dispose();
        _mask.Dispose();
        _videoOutline.Dispose();
        _graphics.Context.PSUnsetShaderResource(0);
        _view?.Dispose();
        _texture?.Dispose();
    }
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
