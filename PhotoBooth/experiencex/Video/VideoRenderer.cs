using System.Runtime.InteropServices;
using PhotoBooth.Diagnostics;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace ExperienceX;

internal sealed record ScreenPlaybackSnapshot(string Id, string Name, RendererState State, double Position, double Opacity, long Frames, string? RequestId, int Loops = 0, bool NameLocked = false, string? BoundPath = null, double Brightness = 100);

// One worker/device/swap chain per physical display. Screens own independent decoders and textures.
internal sealed class VideoRenderer : IDisposable
{
    private readonly string _display;
    private readonly Action _hideAfterFailure;
    private readonly Action? _beforePresent;
    private readonly Thread _thread;
    private readonly RendererMailbox _commands = new(1024);
    private readonly AutoResetEvent _wake = new(false);
    private readonly GraphicsResources _graphics;
    private readonly CalibrationLayer _calibrationLayer = new();
    private readonly Action _drawCalibration;
    private readonly RendererProgress _progress;
    internal RendererProgress Progress => _progress;
    internal RenderHealth Diagnostics => _progress.Snapshot();
    private readonly bool _toolsOnly;
    private bool _toolsVisible = true, _helpSuppressed;
    private readonly List<DisplayScreenTrack> _tracks = [];
    private readonly VideoStopHistory _stops = new();
    private readonly Dictionary<string,string?> _mediaCaptions = new();
    private volatile ExperienceOptions _options;
    private ScreenMediaCatalog _mediaCatalog;
    private bool _displayAvailable = true;
    private volatile bool _shutdown;
    private int _disposeRequested;
    private volatile RendererState _state = RendererState.Starting;
    private volatile string? _requestId;
    private bool _framePermit, _needsRedraw, _wasAnimating;
    private readonly PresentationDeadline _presentationDeadline = new();
    internal bool Stopped => !_thread.IsAlive;
    internal long TransformComputationCount => _tracks.Sum(t => t.TransformComputations);
    public RendererState State => _state;
    public string? RequestId => _requestId;
    internal Task<ScreenPlaybackSnapshot[]> SnapshotAsync()
    {
        var completion = new TaskCompletionSource<ScreenPlaybackSnapshot[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue("Snapshot", () => completion.TrySetResult(_tracks.Select(t => t.Snapshot).ToArray()), () => completion.TrySetResult([]));
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    public VideoRenderer(nint hwnd, int width, int height, ExperienceOptions options, string display,
        Action hideAfterFailure, Action? beforePresent = null, ScreenMediaCatalog? mediaCatalog = null, bool toolsOnly = false,
        Action<RenderHealth, string>? captureDiagnostics = null)
    {
        _display = display;
        _drawCalibration = DrawCalibration;
        _toolsOnly = toolsOnly;
        _options = options;
        _mediaCatalog = mediaCatalog ?? ScreenMediaCatalog.Empty;
        _hideAfterFailure = hideAfterFailure;
        _beforePresent = beforePresent;
        _progress = new(display, () => _commands.Count, captureDiagnostics ?? ((health, reason) => _ = Task.Run(() => RendererDiagnosticCapture.CaptureAsync(health, reason))),
            stallSeconds: () => RendererProgress.WarningTimeout(_options.RendererRecovery.StallTimeoutSeconds));
        _graphics = new(hwnd, width, height, display, _progress, options.GraphicsDebugLayerEnabled);
        _thread = new(Run)
        {
            IsBackground = true,
            Name = "ExperienceX display " + display
        };
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
    private DisplayScreenTrack? Track(string? id) => id is null ? _tracks.FirstOrDefault() : _tracks.FirstOrDefault(t => t.Screen.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    public void Configure(ExperienceOptions options) => Enqueue("Configure", () => { _options = options; Reconcile(); _needsRedraw = true; });
    internal void ShowTools(bool value) => Enqueue("Tools", () => { _toolsVisible=value; _needsRedraw=true; });
    internal void SuppressHelp(bool value) => Enqueue("HelpVisibility", () =>
    {
        _helpSuppressed = value;
        foreach (var track in _tracks) track.SuppressHelp(value);
        _needsRedraw = true;
    });
    private void Reconcile()
    {
        var screens = _options.ScreensFor(_display);
        foreach (var removed in _tracks.Where(t => !screens.Any(s => s.Id == t.Screen.Id)).ToArray())
        {
            removed.Complete("ScreenRemoved");
            _tracks.Remove(removed);
            removed.Dispose();
            _mediaCaptions.Remove(removed.Screen.Id);
        }
        var ordered = new List<DisplayScreenTrack>();
        foreach (var screen in screens)
        {
            var track = Track(screen.Id);
            if (track is null)
            {
                track = new DisplayScreenTrack(_graphics, _display, screen, _options, () => _wake.Set(), Draw, _progress);
                _tracks.Add(track); // Register ownership immediately, including partial reconciliation failures.
            }
            track.Configure(screen, _options);
            track.SuppressHelp(_helpSuppressed);
            track.SetMediaCaption(_mediaCaptions.GetValueOrDefault(screen.Id));
            if (!_toolsOnly) track.ApplyMediaBinding(_mediaCatalog.Match(screen), _displayAvailable);
            ordered.Add(track);
        }
        _tracks.Clear();
        _tracks.AddRange(ordered);
    }
    public void UpdateMediaCatalog(ScreenMediaCatalog catalog) => Enqueue("MediaCatalog", () =>
    {
        _mediaCatalog = catalog;
        foreach (var track in _tracks)
            if (!_toolsOnly) track.ApplyMediaBinding(catalog.Match(track.Screen), _displayAvailable);
    });
    public void SetDisplayAvailable(bool available) => Enqueue("Availability", () =>
    {
        _displayAvailable = available;
        foreach (var track in _tracks)
            if (available && !_toolsOnly) track.ApplyMediaBinding(_mediaCatalog.Match(track.Screen), true);
            else track.Complete("DisplayUnavailable");
        _needsRedraw = true;
    });
    public void ShowCalibration(int selected, string? id = null, bool selectedScreen = true) => Enqueue("Visibility:" + id, () => { Track(id)?.Show(selected, selectedScreen); _needsRedraw = true; });
    public void FadeCalibration(string? id = null) => Enqueue("Visibility:" + id, () => { Track(id)?.Fade(); _needsRedraw = true; });
    public void ShowJoinTarget(int point, string? id = null) => Enqueue("JoinTarget:" + id, () => { Track(id)?.ShowJoinTarget(point); _needsRedraw = true; });
    public void PreviewPointGroup(IReadOnlyDictionary<string, PointOptions?> values) => Enqueue("PreviewGroup", () =>
    {
        foreach (var entry in values) Track(entry.Key)?.Preview(entry.Value);
        _needsRedraw = true;
    });
    public void PreviewPoints(PointOptions? value, string? id = null) => Enqueue("Preview:" + id, () => { Track(id)?.Preview(value); _needsRedraw = true; });
    internal void PreviewFraming(string id, VideoFramingOptions? value) => Enqueue("Framing:" + id, () => { Track(id)?.PreviewFraming(value); _needsRedraw = true; });
    internal void SetMediaCaption(string id, string? caption) => Enqueue("MediaCaption:"+id,()=>
    {
        if(_mediaCaptions.TryGetValue(id,out var previous)&&previous==caption)return;
        _mediaCaptions[id]=caption; Track(id)?.SetMediaCaption(caption); _needsRedraw=true;
    });
    public void FlashCalibration(int point, bool success, string? id = null) => Enqueue("Flash:" + id, () => { Track(id)?.FlashPoint(point, success); _needsRedraw = true; });
    public void FlashCalibrationPoints(int mask, bool success, string? id = null) => Enqueue("Flash:" + id, () => { Track(id)?.Flash(mask, success); _needsRedraw = true; });
    public void Play(string path, ExperienceRequest request)
    {
        try
        {
            Enqueue("Playback:" + request.ScreenId, () =>
            {
                DisplayScreenTrack? track = null;
                var accepted = false;
                try
                {
                    if (request.VideoPriority?.IsInterrupted == true || _stops.Contains(request.Source, request.RequestId)) return;
                    track = (request.ScreenId is null ? _tracks.FirstOrDefault(t => t.Screen.IsVideo) : Track(request.ScreenId)) ?? throw new ArgumentException("The requested screen is no longer configured.");
                    if (track.NameLocked) { track.ReportIgnoredCommand(request); return; }
                    request.VideoPriority?.WakeWhenInterrupted(() => { try { _wake.Set(); } catch (ObjectDisposedException) { } });
                    track.Open(path, request);
                    accepted = true;
                }
                catch { track?.Complete("Error"); throw; }
                finally { if (!accepted) request.VideoPriority?.Dispose(); }
            }, () =>
            {
                request.VideoPriority?.Dispose();
                Telemetry.Info("VideoRequestSuperseded", new { DisplayName = _display, request.ScreenName }, request.RequestId);
            });
        }
        catch { request.VideoPriority?.Dispose(); throw; }
    }
    public void Stop(string reason) => Enqueue("Stop", () => { foreach (var track in _tracks) track.Complete(reason); });
    public void StopPlayback(string? source, string playbackRequestId) => Enqueue("Cancel:" + source + ":" + playbackRequestId, () =>
    {
        _stops.Record(source, playbackRequestId, _options.RequestDeduplicationSeconds);
        foreach (var track in _tracks) track.StopPlayback(source, playbackRequestId);
    });
    public Task<bool> ConfirmPointsAsync(PointOptions expected, string? id = null)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue("Confirmation:" + id, () =>
        {
            try
            {
                if (Track(id)?.Matches(expected) != true)
                {
                    completion.TrySetResult(false);
                    return;
                }
                Draw();
                _graphics.CommitAndWait();
                completion.TrySetResult(true);
            }
            catch (Exception ex) { completion.TrySetException(ex); throw; }
        }, () => completion.TrySetResult(false));
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }
    public void Resize(int width, int height) => Enqueue("Resize", () => { _graphics.Resize(width, height); _needsRedraw = true; });
    internal void SetPresentationSuspended(bool suspended)
    {
        if (_progress.IsSuspended == suspended) return;
        _progress.Suspend(suspended);
        try { _wake.Set(); } catch (ObjectDisposedException) { }
    }
    private void Run()
    {
        _progress.RegisterThread(Environment.CurrentManagedThreadId, GetCurrentThreadId());
        using var loopOperation = _progress.Begin("RenderLoop");
        bool mediaStarted = false, comStarted = false;
        try
        {
            Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
            comStarted = true;
            MediaFactory.MFStartup().CheckError();
            mediaStarted = true;
            _graphics.Initialize();
            Reconcile();
            Draw();
            _state = RendererState.Idle;
            WaitHandle[] waits = [_wake, _graphics.Latency!];
            while (!_shutdown)
            {
                _progress.Beat();
                for (int count = 0; count < RendererMailbox.Capacity && !_shutdown && _commands.TryTake(out var action); count++)
                    try
                    {
                        using var operation = _progress.Begin("RendererCommand");
                        action();
                    }
                    catch (Exception ex)
                    {
                        if (IsRendererFailure(ex))
                            throw;
                        Telemetry.Error("DisplayRequestFailed", ex, new
                        {
                            DisplayName = _display
                        });
                    }
                if (_shutdown)
                    break;
                foreach (var track in _tracks)
                    if (track.StopInterrupted()) _needsRedraw = true;
                if (_progress.IsSuspended)
                {
                    _presentationDeadline.Reset();
                    using (var operation = _progress.Begin("SuspendedWait")) _wake.WaitOne(100);
                    continue;
                }
                var animating = _tracks.Any(t => _toolsVisible ? t.Animating : t.ContentAnimating);
                var playing = _tracks.Any(t => t.IsVideoOpen);
                if (!playing && !animating)
                {
                    _presentationDeadline.Reset();
                    if (_needsRedraw || _wasAnimating)
                        Draw();
                    var held = _tracks.FirstOrDefault(t => t.IsOpen);
                    _state = held?.State ?? RendererState.Idle;
                    _requestId = held?.RequestId;
                    using (var operation = _progress.Begin("IdleWait")) _wake.WaitOne();
                    continue;
                }
                if (!_framePermit && _graphics.Latency!.WaitOne(0))
                    _framePermit = true;
                if (!_framePermit)
                    using (var operation = _progress.Begin("FramePermitWait"))
                        if (WaitHandle.WaitAny(waits, 100) == 1) _framePermit = true;
                _presentationDeadline.Check(System.Diagnostics.Stopwatch.GetTimestamp(), _framePermit, _progress.IsSuspended);
                if (!_framePermit)
                    continue;
                foreach (var track in _tracks.Where(t => t.IsOpen))
                    try
                    {
                        using var operation = _progress.Begin("VideoTick", track.Screen.Id);
                        track.Tick();
                    }
                    catch (Exception ex)
                    {
                        if (IsRendererFailure(ex))
                            throw;
                        Telemetry.Error("ScreenRenderingFailed", ex, new
                        {
                            DisplayName = _display,
                            ScreenName = track.Screen.Name
                        }, track.RequestId);
                        track.Complete("Error");
                    }
                Draw();
                var active = _tracks.FirstOrDefault(t => t.IsOpen);
                _state = active?.State ?? RendererState.Idle;
                _requestId = active?.RequestId;
            }
            foreach (var track in _tracks)
                track.Complete("Shutdown");
        }
        catch (Exception ex)
        {
            _state = RendererState.Failed;
            Telemetry.Error("RendererFailed", ex, new
            {
                DisplayName = _display
            });
            try
            {
                _hideAfterFailure();
            }
            catch (Exception hideError) { Telemetry.Warning("RendererHideFailed", new { DisplayName = _display, Reason = hideError.Message }); }
        }
        finally
        {
            _commands.Close();
            foreach (var track in _tracks)
                Cleanup(track);
            Cleanup(_calibrationLayer);
            Cleanup(_graphics);
            if (mediaStarted)
                MediaFactory.MFShutdown();
            if (comStarted) CoUninitialize();
            _wake.Dispose();
            _progress.Dispose();
        }
    }
    private void Draw()
    {
        if (_progress.IsSuspended) return;
        using var drawOperation = _progress.Begin("DrawLayers");
        using (var operation = _progress.Begin("PresentationBufferWait"))
            if (!_framePermit && !_graphics.Latency!.WaitOne(2000))
                throw new GraphicsPresentationTimeoutException("DXGI did not make a presentation buffer available.");
        // An exception before Present has not consumed the available buffer. Keep its permit
        // so a recoverable screen/overlay failure cannot strand the next cleanup or request.
        _framePermit = true;
        _graphics.BeginFrame(_toolsOnly ? new Vortice.Mathematics.Color4(0,0,0,0) : DisplayBackground.For(_display, _options));
        // Video and mats share the persisted back-to-front order. G-Force is an external window beneath this surface.
        // Its owned inverse mat is always the bottom internal layer, even after an external config reorder.
        if (!_toolsOnly) foreach (var track in _tracks.Where(t => t.Screen.IsVisualization))
            using (var operation = _progress.Begin("DrawVisualizationMask", track.Screen.Id)) track.DrawMask();
        var cutouts = !_toolsOnly && _tracks.Any(t => t.Screen.IsMat);
        if (cutouts) _graphics.BeginScreenLayers();
        foreach (var track in _tracks.Where(t => !_toolsOnly && !t.Screen.IsVisualization))
        {
            using (var operation = _progress.Begin("DrawVideo", track.Screen.Id)) track.DrawVideo();
            using (var operation = _progress.Begin("DrawMat", track.Screen.Id)) track.DrawMask();
        }
        if (cutouts) _graphics.FinishScreenLayers();
        // Tools are drawn after all content, so overlapping video cannot obscure editing handles.
        _calibrationLayer.Draw(_graphics, _needsRedraw, _toolsVisible && _tracks.Any(t => t.CalibrationVisible), _drawCalibration);
        if (_beforePresent is not null)
            using (var operation = _progress.Begin("BeforePresentCallback")) _beforePresent();
        using (var operation = _progress.Begin("Present")) _graphics.SwapChain.Present(1, PresentFlags.None).CheckError();
        _framePermit = false;
        _progress.Presented();
        _graphics.ReportDebugMessages();
        foreach (var track in _tracks)
            track.Presented();
        _needsRedraw = false;
        _wasAnimating = _tracks.Any(t => _toolsVisible ? t.Animating : t.ContentAnimating);
    }
    private void DrawCalibration()
    {
        foreach (var track in _tracks.Where(t => !t.SelectedScreen))
            using (var operation = _progress.Begin("DrawOverlay", track.Screen.Id)) track.DrawOverlay();
        foreach (var track in _tracks.Where(t => t.SelectedScreen))
            using (var operation = _progress.Begin("DrawOverlay", track.Screen.Id)) track.DrawOverlay();
    }
    private void Cleanup(IDisposable resource)
    {
        try
        {
            resource.Dispose();
        }
        catch (Exception ex) { Telemetry.Warning("RendererCleanupFailed", new { DisplayName = _display, Reason = ex.Message }); }
    }
    private static bool IsDeviceFailure(Exception ex) => unchecked((uint)ex.HResult) is 0x887A0005 or 0x887A0006 or 0x887A0007 or 0x887A0020;
    private static bool IsRendererFailure(Exception ex) => ex is GraphicsPresentationTimeoutException || IsDeviceFailure(ex);
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
        {
            Telemetry.Warning("RendererShutdownTimedOut", new
            {
                DisplayName = _display,
                TimeoutSeconds = 5
            });
            _progress.ShutdownTimedOut();
            // Give the external helper a bounded opportunity to capture before process exit.
            try { RendererDiagnosticCapture.CaptureAsync(_progress.Snapshot(), "ShutdownTimedOut").Wait(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { Telemetry.Warning("RendererShutdownDiagnosticFailed", new { DisplayName = _display, Reason = ex.Message }); }
        }
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
