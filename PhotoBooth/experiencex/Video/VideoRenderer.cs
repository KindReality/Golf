using System.Diagnostics;
using System.Runtime.InteropServices;
using PhotoBooth.Diagnostics;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace ExperienceX;

// Coordinates scheduling. Graphics, media, overlays and metrics belong to this worker.
internal sealed class VideoRenderer : IDisposable
{
    private readonly string _monitor;
    private readonly Action _hideAfterFailure;
    private readonly Action? _beforePresent;
    private readonly Thread _thread;
    private readonly RendererMailbox _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly GraphicsResources _graphics;
    private readonly MediaEngineSession _media;
    private readonly Action<int, int> _createVideoTexture;
    private readonly CalibrationOverlay _overlay = new();
    private readonly PlaybackMetrics _metrics = new();
    private readonly VideoPlaybackTimeline _timeline = new();
    private readonly KeystoneTransformCache _transforms = new();
    private readonly RendererStateMachine _state = new();
    private readonly Stopwatch _opening = new();
    private ExperienceOptions _options;
    private Vortice.Mathematics.Color4 _background;
    private volatile bool _shutdown;
    private int _disposeRequested;
    private bool _framePermit, _needsRedraw, _overlayWasAnimating;
    private double _alpha;
    private int _videoWidth, _videoHeight;
    internal bool Stopped => !_thread.IsAlive;
    internal long TransformComputationCount => _transforms.Computations;
    public RendererState State => _state.Current;
    public string? RequestId
    {
        get; private set;
    }

    public VideoRenderer(nint hwnd, int width, int height, ExperienceOptions options, string monitor,
        Action hideAfterFailure, Action? beforePresent = null)
    {
        _monitor = monitor;
        _options = options;
        _background = MonitorBackground.For(monitor, options);
        _hideAfterFailure = hideAfterFailure;
        _beforePresent = beforePresent;
        _graphics = new(hwnd, width, height, monitor);
        _media = new(() => _wake.Set());
        _createVideoTexture = CreateVideoTexture;
        _thread = new Thread(Run) { IsBackground = true, Name = "ExperienceX video " + monitor };
        _thread.Start();
    }
    private void Enqueue(string key, Action action, Action? replaced = null)
    {
        if (_shutdown || Stopped)
        {
            replaced?.Invoke();
            return;
        }
        _commands.Post(key, action, replaced);
        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException) { replaced?.Invoke(); }
    }
    public void Configure(ExperienceOptions options) => Enqueue("Configure", () =>
    {
        _options = options;
        var background = MonitorBackground.For(_monitor, options);
        if (_background != background)
            Telemetry.Info("MonitorBackgroundChanged", new
            {
                MonitorName = _monitor,
                Red = background.R,
                Green = background.G,
                Blue = background.B,
                Alpha = background.A
            });
        _background = background;
        _overlay.Configure(options);
        _needsRedraw = true;
    });
    public void ShowCalibration(int selected) => Enqueue("Visibility", () => { _overlay.Show(selected); _needsRedraw = true; });
    public void FadeCalibration() => Enqueue("Visibility", () => { _overlay.Fade(); _needsRedraw = true; });
    public void PreviewKeystone(KeystoneOptions? preview) => Enqueue("Preview", () => { _overlay.SetPreview(preview); _needsRedraw = true; });
    public void FlashCalibration(int point, bool success) => FlashCalibrationPoints(1 << point, success);
    public void FlashCalibrationPoints(int mask, bool success) => Enqueue("Flash", () => { _overlay.Flash(mask, success); _needsRedraw = true; });
    public void Play(string path, ExperienceRequest request) => Enqueue("Playback", () => Open(path, request),
        () => Telemetry.Info("VideoRequestSuperseded", new { MonitorName = _monitor }, request.RequestId));
    public void Stop(string reason) => Enqueue("Playback", () => Complete(reason));
    public Task<bool> ConfirmKeystoneAsync(KeystoneOptions expected)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue("Confirmation", () =>
        {
            try
            {
                if (_options.KeystoneFor(_monitor) != expected || (_overlay.Preview is not null && _overlay.Preview != expected))
                {
                    completion.TrySetResult(false);
                    return;
                }
                Draw(_alpha);
                _graphics.CommitAndWait();
                completion.TrySetResult(true);
            }
            catch (Exception ex) { completion.TrySetException(ex); throw; }
        }, () => completion.TrySetResult(false));
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }
    public void Resize(int width, int height) => Enqueue("Resize", () =>
    {
        if (_graphics.Width == width && _graphics.Height == height)
            return;
        _graphics.Resize(width, height);
        Draw(0);
    });
    private void Run()
    {
        bool mediaStarted = false;
        CoInitializeEx(0, 0);
        try
        {
            MediaFactory.MFStartup().CheckError();
            mediaStarted = true;
            _graphics.Initialize();
            _overlay.Initialize(_graphics);
            Draw(0);
            _state.TransitionTo(RendererState.Idle);
            WaitHandle[] waits = [_wake, _graphics.Latency!];
            while (!_shutdown)
            {
                for (int count = 0; count < RendererMailbox.Capacity && !_shutdown && _commands.TryTake(out var action); count++)
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        if (IsDeviceFailure(ex))
                            throw;
                        Telemetry.Error("VideoRequestFailed", ex, new
                        {
                            MonitorName = _monitor
                        }, RequestId);
                        Complete("Error");
                    }
                }
                if (_shutdown)
                    break;
                if (!_media.IsOpen && !_overlay.Animating(_options))
                {
                    if (_needsRedraw || _overlayWasAnimating)
                        Draw(0);
                    _wake.WaitOne();
                    continue;
                }
                if (!_framePermit && _graphics.Latency!.WaitOne(0))
                    _framePermit = true;
                if (!_framePermit && WaitHandle.WaitAny(waits, 100) == 1)
                    _framePermit = true;
                if (!_framePermit)
                    continue;
                try
                {
                    if (_media.IsOpen)
                        Tick();
                    else
                        Draw(0);
                }
                catch (Exception ex)
                {
                    if (IsDeviceFailure(ex))
                        throw;
                    Telemetry.Error("VideoRenderingFailed", ex, new
                    {
                        MonitorName = _monitor
                    }, RequestId);
                    Complete("Error");
                }
            }
            if (_media.IsOpen)
                Complete("Shutdown");
        }
        catch (Exception ex)
        {
            _state.TransitionTo(RendererState.Failed);
            Telemetry.Error("RendererFailed", ex, new
            {
                MonitorName = _monitor
            });
            try
            {
                _hideAfterFailure();
            }
            catch (Exception hideError) { Telemetry.Warning("RendererHideFailed", new { MonitorName = _monitor, Reason = hideError.Message }); }
        }
        finally
        {
            _commands.Close();
            Cleanup(_media, "MediaEngineCleanupFailed");
            Cleanup(_overlay, "CalibrationResourceCleanupFailed");
            Cleanup(_graphics, "GraphicsResourceCleanupFailed");
            if (mediaStarted)
                MediaFactory.MFShutdown();
            CoUninitialize();
            _wake.Dispose();
        }
    }
    private void Cleanup(IDisposable resource, string failure)
    {
        try
        {
            resource.Dispose();
        }
        catch (Exception ex) { Telemetry.Warning(failure, new { MonitorName = _monitor, Reason = ex.Message }); }
    }
    private static bool IsDeviceFailure(Exception ex) => unchecked((uint)ex.HResult) is 0x887A0005 or 0x887A0006 or 0x887A0007 or 0x887A0020;
    private void Open(string path, ExperienceRequest request)
    {
        Complete("Replaced");
        RequestId = request.RequestId;
        _state.TransitionTo(RendererState.Opening);
        _alpha = 0;
        _timeline.Reset();
        _metrics.Reset();
        _opening.Restart();
        _media.Open(path, request, _graphics.DeviceManager);
        Telemetry.Info("VideoOpening", new
        {
            Path = path,
            MonitorName = _monitor,
            Muted = request.Mute,
            SurfaceOpacity = 0
        }, RequestId);
    }
    private void Tick()
    {
        _media.StartIfReady(_createVideoTexture);
        if (_media.TryGetFrame(out var timestamp) && timestamp != _metrics.LastTimestamp)
        {
            _graphics.Context.PSUnsetShaderResource(0);
            using var surface = _graphics.VideoTexture!.QueryInterface<IDXGISurface>();
            _media.TransferFrame(surface, _videoWidth, _videoHeight);
            _metrics.Transferred(timestamp);
            if (!_timeline.Started && _media.Position > 0)
            {
                Draw(0);
                _timeline.Start();
                _metrics.BeginPlayback();
                _state.TransitionTo(RendererState.Playing);
                Telemetry.Info("VideoPlaybackConfirmed", new
                {
                    MonitorName = _monitor,
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
        if (!_framePermit && !_graphics.Latency!.WaitOne(2000))
            throw new TimeoutException("DXGI did not make a presentation buffer available.");
        _framePermit = false;
        var inverse = opacity > 0 ? _transforms.Get(_overlay.Preview ?? _options.KeystoneFor(_monitor)) : default;
        _graphics.DrawVideo(opacity, _options, inverse, _videoWidth, _videoHeight, _background);
        _overlayWasAnimating = _overlay.Animating(_options);
        if (_overlay.Visible(_options))
            _overlay.Draw(_graphics, _options, _monitor, _transforms);
        _beforePresent?.Invoke();
        _graphics.SwapChain.Present(1, PresentFlags.None).CheckError();
        _needsRedraw = false;
        _alpha = opacity;
        _metrics.Presented();
    }
    private void CreateVideoTexture(int width, int height)
    {
        _videoWidth = width;
        _videoHeight = height;
        _graphics.CreateVideoTexture(width, height);
    }
    private void Report() => _metrics.Report(_graphics, _media, _monitor, RequestId, _alpha, _transforms.Computations);
    private void Complete(string reason)
    {
        if (!_media.IsOpen)
            return;
        Draw(0);
        _graphics.CommitAndWait();
        var flushResult = DwmFlush();
        Telemetry.Info("VideoTransparentFramePresented", new
        {
            MonitorName = _monitor,
            SurfaceOpacity = 0,
            PositionSeconds = _media.Position,
            DwmFlushResult = flushResult
        }, RequestId);
        Report();
        Telemetry.Info("PlaybackStopped", new
        {
            MonitorName = _monitor,
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
        if (Interlocked.Exchange(ref _disposeRequested, 1) == 0)
        {
            _shutdown = true;
            try
            {
                _wake.Set();
            }
            catch (ObjectDisposedException) { }
        }
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(5)))
            Telemetry.Warning("RendererShutdownTimedOut", new
            {
                MonitorName = _monitor,
                TimeoutSeconds = 5
            });
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
