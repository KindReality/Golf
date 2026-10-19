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
    private readonly RendererProgress? _progress;
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
    private string? _path;
    private (string Path, ExperienceRequest Request)? _background;
    private PhotoBooth.Networking.CommandPriority _currentPriority;
    private bool _priorityEndsAtLoop;
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
    public DisplayScreenTrack(GraphicsResources graphics, string display, ScreenOptions screen, ExperienceOptions options, Action wake, Action present, RendererProgress? progress = null)
    {
        _graphics = graphics;
        _mask = new(graphics);
        _videoOutline = new(graphics);
        _display = display;
        Screen = screen;
        _options = options;
        _progress = progress;
        _present = present;
        _media = new(wake, progress, screen.Id);
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
        path = System.IO.Path.GetFullPath(path);
        if (request.Loop) _background = (path, request with { VideoPriority = null });
        var sameVideo = _media.IsOpen && !_media.Ended && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase);
        if ((request.VideoPriority is null && request.PriorityCoordinator is not null) || (IsOpen && request.EffectivePriority < _currentPriority))
        {
            if (sameVideo && request.Loop) ContinueLoop();
            request.VideoPriority?.Dispose();
            Telemetry.Info(request.Loop ? "VideoBackgroundRemembered" : "VideoRequestSkippedLowerPriority", new { DisplayName = _display, ScreenName = Screen.Name, Path = path, Priority = request.EffectivePriority }, request.RequestId);
            var blockedBy = request.BlockingPriority;
            if (IsOpen && (blockedBy is null || _currentPriority > blockedBy)) blockedBy = _currentPriority;
            if (blockedBy is { } activePriority) request.PriorityRejected?.Invoke(request, _display, Screen.Name, activePriority);
            return;
        }
        if (sameVideo)
        {
            _progress?.ContinueVideo(Screen.Id, request);
            var previousRequestId = RequestId;
            var previousPriority = _priority;
            _priority = request.VideoPriority;
            previousPriority?.Dispose();
            _currentPriority = request.EffectivePriority;
            _media.Engine!.Muted = request.Mute;
            if (request.Loop) ContinueLoop();
            _timeline.Continue(_loop);
            if (State == RendererState.FadingOut) _state.TransitionTo(RendererState.Playing);
            _priorityEndsAtLoop = _loop && _currentPriority > PhotoBooth.Networking.CommandPriority.Low;
            RequestId = request.RequestId;
            _source = request.Source;
            Telemetry.Info("VideoPlaybackContinued", new
            {
                Path = path, DisplayName = _display, ScreenName = Screen.Name,
                PreviousRequestId = previousRequestId, PositionSeconds = _media.Position, SurfaceOpacity = _alpha, Loop = _loop, Priority = _currentPriority, Muted = request.Mute
            }, RequestId);
            return;
        }
        Complete("Replaced");
        _priority = request.VideoPriority;
        _currentPriority = request.EffectivePriority;
        _path = path;
        _priorityEndsAtLoop = false;
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
        _progress?.StartVideo(Screen, path, request, _options.SoftwareVideoDecoding);
        _media.Open(path, request, _graphics.DeviceManager, _options.SoftwareVideoDecoding);
        Telemetry.Info("VideoOpening", new
        {
            Path = path,
            DisplayName = _display,
            ScreenName = Screen.Name,
            Muted = request.Mute,
            _options.SoftwareVideoDecoding,
            FrameTransfer = _options.SoftwareVideoDecoding ? "SoftwareWicUpload" : "DxgiSurface",
            request.Loop,
            request.IsAmbient,
            Priority = _currentPriority,
            SurfaceOpacity = 0
        }, RequestId);
    }
    private void ContinueLoop()
    {
        _loop = true;
        if (_media.Engine is { } engine) engine.Loop = true;
        _timeline.Continue(true);
        _priorityEndsAtLoop = _currentPriority > PhotoBooth.Networking.CommandPriority.Low;
        if (State == RendererState.FadingOut) _state.TransitionTo(RendererState.Playing);
    }
    private void ResumeBackground()
    {
        if (_background is not { } background || NameLocked) return;
        var request = background.Request;
        var lease = request.PriorityCoordinator?.TryBegin(PhotoBooth.Networking.CommandPriority.Low, VideoPlaybackIdentity.For(background.Path, _display, request));
        if (request.PriorityCoordinator is not null && lease is null) return;
        try
        {
            Open(background.Path, request with { VideoPriority = lease });
            Telemetry.Info("VideoBackgroundResumed", new { DisplayName = _display, ScreenName = Screen.Name, Path = background.Path }, request.RequestId);
        }
        catch { lease?.Dispose(); throw; }
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
            _media.TransferFrame(_graphics, _texture!, _videoWidth, _videoHeight);
            _metrics.Transferred(timestamp);
            _progress?.FrameTransferred(Screen.Id);
            if (!_timeline.Started && _media.Position > 0)
            {
                Draw(0);
                _present(); // First advancing frame is submitted while this screen is fully transparent.
                _timeline.Start();
                _metrics.BeginPlayback();
                _state.TransitionTo(RendererState.Playing);
                _progress?.Playing(Screen.Id);
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
        _progress?.Position(Screen.Id, position);
        if (_loop && position < _lastPosition - .05)
        {
            _loops++;
            Telemetry.Info("VideoLoopRepeated", new { DisplayName = _display, ScreenName = Screen.Name, Repetitions = _loops }, RequestId);
            if (_priorityEndsAtLoop)
            {
                _priority?.Dispose(); _priority = null;
                _currentPriority = PhotoBooth.Networking.CommandPriority.Low; _priorityEndsAtLoop = false;
                var previousPath = _path;
                ResumeBackground();
                if (!string.Equals(_path, previousPath, StringComparison.OrdinalIgnoreCase)) return;
            }
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
        if (_background is { } background && background.Request.RequestId == playbackRequestId && string.Equals(background.Request.Source, source, StringComparison.OrdinalIgnoreCase))
        {
            _background = null;
            if (_media.IsOpen && _currentPriority > PhotoBooth.Networking.CommandPriority.Low)
            {
                _loop = false; _priorityEndsAtLoop = false;
                _media.Engine!.Loop = false; _timeline.Continue(false);
            }
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
    internal bool CalibrationVisible => _overlay.Visible(_options);
    public void DrawOverlay()
    {
        _overlay.SetMediaCaption(Screen.IsVideo ? _mediaCaption ?? _binding?.RelativePath ?? Screen.MediaFile ?? _playbackMedia : null);
        if (_overlay.Visible(_options))
            _overlay.Draw(_graphics, _options, _display, _overlayTransforms, Screen.Id, Screen);
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
    private void Report()
    {
        using var operation = _progress?.Begin("PlaybackStatistics", Screen.Id);
        _metrics.Report(_graphics, _media, _display, RequestId, _alpha, TransformComputations, Screen.Name);
    }
    internal bool StopInterrupted()
    {
        if (_priority?.IsInterrupted != true) return false;
        Complete("HigherPriorityVideo");
        return true;
    }
    public void Complete(string reason)
    {
        var failedPath = _path;
        if (reason is not ("Replaced" or "HigherPriorityVideo" or "MediaEnded" or "Error")) _background = null;
        using var operation = _progress?.Begin("CompletePlayback:"+reason, Screen.Id);
        _progress?.Stopping(Screen.Id);
        try { CompleteCore(reason); }
        finally
        {
            if (!IsOpen) _progress?.EndVideo(Screen.Id);
            _priority?.Dispose();
            _priority = null;
            _path = null;
        }
        // A bad foreground clip must not strand a valid remembered background. Never retry
        // the failing background itself here; persistent decoder/device errors use recovery.
        if (reason == "MediaEnded" || reason == "Error" && _background is { } background &&
            !string.Equals(background.Path, failedPath, StringComparison.OrdinalIgnoreCase)) ResumeBackground();
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
        try
        {
            Draw(0);
            // A hidden/disconnected window or a failed GPU must not be asked to present cleanup.
            if (reason is not ("Error" or "DisplayUnavailable" or "Shutdown") && _graphics.CanPresent)
            {
                _present();
                _graphics.CommitAndWait();
                int flushResult;
                using (var operation = _progress?.Begin("DwmFlush", Screen.Id)) flushResult = DwmFlush();
                Telemetry.Info("VideoTransparentFramePresented", new
                {
                    DisplayName = _display, ScreenName = Screen.Name, SurfaceOpacity = 0,
                    PositionSeconds = _lastPosition, DwmFlushResult = flushResult
                }, RequestId);
                if (!_stillImage) Report();
            }
        }
        finally
        {
            // Always attempt decoder cleanup, even if presentation or statistics failed.
            // A native shutdown that itself hangs is handled by process-level recovery.
            try { _media.Dispose(); }
            finally
            {
                Telemetry.Info("PlaybackStopped", new
                {
                    DisplayName = _display, ScreenName = Screen.Name, SurfaceOpacity = 0,
                    PositionSeconds = _lastPosition, Reason = reason
                }, RequestId);
                _stillImage = false;
                _state.TransitionTo(RendererState.Idle);
                _alpha = 0;
                RequestId = null;
            }
        }
    }
    public void Dispose()
    {
        using var operation = _progress?.Begin("DisposeScreen", Screen.Id);
        _priority?.Dispose();
        _priority = null;
        _media.Dispose();
        _overlay.Dispose();
        _mask.Dispose();
        _videoOutline.Dispose();
        _graphics.Context.PSUnsetShaderResource(0);
        _view?.Dispose();
        _texture?.Dispose();
        _progress?.EndVideo(Screen.Id);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
