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
    private readonly List<DisplayScreenTrack> _tracks = [];
    private readonly VideoStopHistory _stops = new();
    private ExperienceOptions _options;
    private ScreenMediaCatalog _mediaCatalog;
    private bool _displayAvailable = true;
    private volatile bool _shutdown;
    private int _disposeRequested;
    private volatile RendererState _state = RendererState.Starting;
    private volatile string? _requestId;
    private bool _framePermit, _needsRedraw, _wasAnimating;
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
        Action hideAfterFailure, Action? beforePresent = null, ScreenMediaCatalog? mediaCatalog = null)
    {
        _display = display;
        _options = options;
        _mediaCatalog = mediaCatalog ?? ScreenMediaCatalog.Empty;
        _hideAfterFailure = hideAfterFailure;
        _beforePresent = beforePresent;
        _graphics = new(hwnd, width, height, display);
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
    private void Reconcile()
    {
        var screens = _options.ScreensFor(_display);
        foreach (var removed in _tracks.Where(t => !screens.Any(s => s.Id == t.Screen.Id)).ToArray())
        {
            removed.Complete("ScreenRemoved");
            _tracks.Remove(removed);
            removed.Dispose();
        }
        var ordered = new List<DisplayScreenTrack>();
        foreach (var screen in screens)
        {
            var track = Track(screen.Id);
            if (track is null)
            {
                track = new DisplayScreenTrack(_graphics, _display, screen, _options, () => _wake.Set(), Draw);
                _tracks.Add(track); // Register ownership immediately, including partial reconciliation failures.
            }
            track.Configure(screen, _options);
            track.ApplyMediaBinding(_mediaCatalog.Match(screen), _displayAvailable);
            ordered.Add(track);
        }
        _tracks.Clear();
        _tracks.AddRange(ordered);
    }
    public void UpdateMediaCatalog(ScreenMediaCatalog catalog) => Enqueue("MediaCatalog", () =>
    {
        _mediaCatalog = catalog;
        foreach (var track in _tracks)
            track.ApplyMediaBinding(catalog.Match(track.Screen), _displayAvailable);
    });
    public void SetDisplayAvailable(bool available) => Enqueue("Availability", () =>
    {
        _displayAvailable = available;
        foreach (var track in _tracks)
            if (available) track.ApplyMediaBinding(_mediaCatalog.Match(track.Screen), true);
            else track.Complete("DisplayUnavailable");
        _needsRedraw = true;
    });
    public void ShowCalibration(int selected, string? id = null, bool selectedScreen = true) => Enqueue("Visibility:" + id, () => { Track(id)?.Show(selected, selectedScreen); _needsRedraw = true; });
    public void FadeCalibration(string? id = null) => Enqueue("Visibility:" + id, () => { Track(id)?.Fade(); _needsRedraw = true; });
    public void PreviewPointGroup(IReadOnlyDictionary<string, PointOptions?> values) => Enqueue("PreviewGroup", () =>
    {
        foreach (var entry in values) Track(entry.Key)?.Preview(entry.Value);
        _needsRedraw = true;
    });
    public void PreviewPoints(PointOptions? value, string? id = null) => Enqueue("Preview:" + id, () => { Track(id)?.Preview(value); _needsRedraw = true; });
    public void FlashCalibration(int point, bool success, string? id = null) => Enqueue("Flash:" + id, () => { Track(id)?.FlashPoint(point, success); _needsRedraw = true; });
    public void FlashCalibrationPoints(int mask, bool success, string? id = null) => Enqueue("Flash:" + id, () => { Track(id)?.Flash(mask, success); _needsRedraw = true; });
    public void Play(string path, ExperienceRequest request) => Enqueue("Playback:" + request.ScreenId, () =>
    {
        if (_stops.Contains(request.Source, request.RequestId)) return;
        var track = (request.ScreenId is null ? _tracks.FirstOrDefault(t => t.Screen.IsVideo) : Track(request.ScreenId)) ?? throw new ArgumentException("The requested screen is no longer configured.");
        if (track.NameLocked) { track.ReportIgnoredCommand(request); return; }
        try
        {
            track.Open(path, request);
        }
        catch { track.Complete("Error"); throw; }
    }, () => Telemetry.Info("VideoRequestSuperseded", new { DisplayName = _display, request.ScreenName }, request.RequestId));
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
    private void Run()
    {
        bool mediaStarted = false;
        CoInitializeEx(0, 0);
        try
        {
            MediaFactory.MFStartup().CheckError();
            mediaStarted = true;
            _graphics.Initialize();
            Reconcile();
            Draw();
            _state = RendererState.Idle;
            WaitHandle[] waits = [_wake, _graphics.Latency!];
            while (!_shutdown)
            {
                for (int count = 0; count < RendererMailbox.Capacity && !_shutdown && _commands.TryTake(out var action); count++)
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        if (IsDeviceFailure(ex))
                            throw;
                        Telemetry.Error("DisplayRequestFailed", ex, new
                        {
                            DisplayName = _display
                        });
                    }
                if (_shutdown)
                    break;
                var animating = _tracks.Any(t => t.Animating);
                var playing = _tracks.Any(t => t.IsVideoOpen);
                if (!playing && !animating)
                {
                    if (_needsRedraw || _wasAnimating)
                        Draw();
                    var held = _tracks.FirstOrDefault(t => t.IsOpen);
                    _state = held?.State ?? RendererState.Idle;
                    _requestId = held?.RequestId;
                    _wake.WaitOne();
                    continue;
                }
                if (!_framePermit && _graphics.Latency!.WaitOne(0))
                    _framePermit = true;
                if (!_framePermit && WaitHandle.WaitAny(waits, 100) == 1)
                    _framePermit = true;
                if (!_framePermit)
                    continue;
                foreach (var track in _tracks.Where(t => t.IsOpen))
                    try
                    {
                        track.Tick();
                    }
                    catch (Exception ex)
                    {
                        if (IsDeviceFailure(ex))
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
            Cleanup(_graphics);
            if (mediaStarted)
                MediaFactory.MFShutdown();
            CoUninitialize();
            _wake.Dispose();
        }
    }
    private void Draw()
    {
        if (!_framePermit && !_graphics.Latency!.WaitOne(2000))
            throw new TimeoutException("DXGI did not make a presentation buffer available.");
        _framePermit = false;
        _graphics.BeginFrame(DisplayBackground.For(_display, _options));
        foreach (var track in _tracks)
            track.DrawVideo();
        // Masks are always above content, independent of configuration/selection order.
        foreach (var track in _tracks)
            track.DrawMat();
        // Tools are drawn after all content, so overlapping video cannot obscure editing handles.
        foreach (var track in _tracks.Where(t => !t.SelectedScreen)) track.DrawOverlay();
        foreach (var track in _tracks.Where(t => t.SelectedScreen)) track.DrawOverlay();
        _beforePresent?.Invoke();
        _graphics.SwapChain.Present(1, PresentFlags.None).CheckError();
        foreach (var track in _tracks)
            track.Presented();
        _needsRedraw = false;
        _wasAnimating = _tracks.Any(t => t.Animating);
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
                DisplayName = _display,
                TimeoutSeconds = 5
            });
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
