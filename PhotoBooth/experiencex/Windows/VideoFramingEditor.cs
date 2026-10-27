using PhotoBooth.Diagnostics;
using Forms = System.Windows.Forms;

namespace ExperienceX;

// One gesture at a time; previews stay local and saves merge only the framing fields.
internal sealed class VideoFramingEditor(
    Action<string, VideoFramingOptions?> preview,
    Func<ScreenOptions, VideoFramingOptions, CancellationToken, Task> save,
    Action<string, bool> flash,
    Func<ScreenOptions, double>? screenAspect = null) : IAsyncDisposable
{
    private readonly AsyncTaskScope _tasks = new("Video framing");
    private readonly Forms.Timer _debounce = new() { Interval = 350 };
    private ScreenOptions? _original;
    private VideoFramingOptions? _draft, _panView;
    private ScreenPoint? _panStart;
    private ScreenPoint? _zoomStart;
    private double _zoomDisplayWidth, _zoomDisplayHeight;
    private ScreenPoint? _zoomAnchor;
    private VideoFramingOptions? _zoomView;
    private ScreenPoint? _rotateStart;
    private VideoFramingOptions? _rotateView;
    private double _rotateDisplayWidth, _rotateDisplayHeight, _aspect = 1;
    private bool _saving, _disposed;
    private Task _saveTask = Task.CompletedTask;
    internal bool Panning => _panStart is not null;
    internal bool Zooming => _zoomStart is not null;
    internal bool Rotating => _rotateStart is not null;
    internal bool Dragging => Panning || Zooming || Rotating;
    internal bool Busy => _saving;
    internal VideoFramingOptions? Draft => _draft;

    private bool Begin(ScreenOptions screen)
    {
        if (screen.IsLocked) return false;
        if (_disposed || _saving || !screen.IsVideo) return false;
        if (_original is not null && _original.Id != screen.Id) { Flush(); return false; }
        if (_original is null) { _original = screen; _draft = screen.Framing; _aspect = screenAspect?.Invoke(screen) ?? 1; }
        return true;
    }
    internal bool Wheel(ScreenOptions screen, double x, double y, int delta)
    {
        if (Dragging || delta == 0 || !VideoFramingOptions.TryPointer(screen.Points, x, y, out var pointer) || !Begin(screen)) return false;
        pointer = new(Math.Clamp(pointer.X, 0, 1), Math.Clamp(pointer.Y, 0, 1));
        return ChangeZoom(screen, pointer, delta / 120.0);
    }
    internal bool KeyboardZoom(ScreenOptions screen, ScreenPoint pointer, int direction)
    {
        if (Dragging || !Begin(screen)) return false;
        if (direction != 0) return ChangeZoom(screen, pointer, direction);
        return PreviewZoom(screen, new());
    }
    private bool ChangeZoom(ScreenOptions screen, ScreenPoint pointer, double steps) =>
        PreviewZoom(screen, _draft!.ZoomAt(pointer, steps, _aspect));
    private bool PreviewZoom(ScreenOptions screen, VideoFramingOptions changed)
    {
        if (changed == _draft) return false;
        _draft = changed; preview(screen.Id, changed);
        _debounce.Stop(); _debounce.Tick -= FinishWheel; _debounce.Tick += FinishWheel; _debounce.Start();
        return true;
    }
    internal bool BeginPan(ScreenOptions screen, double x, double y)
    {
        if (!VideoFramingOptions.TryPointer(screen.Points, x, y, out var pointer) || !Begin(screen)) return false;
        _debounce.Stop();
        if (_draft!.Zoom == 1) { Flush(); return false; }
        _panStart = pointer; _panView = _draft;
        return true;
    }
    internal bool BeginZoom(ScreenOptions screen, double x, double y, double displayWidth, double displayHeight)
    {
        if (!VideoFramingOptions.TryPointer(screen.Points, x, y, out var pointer) || !Begin(screen)) return false;
        _debounce.Stop();
        _zoomStart = new(x, y); _zoomDisplayWidth = displayWidth; _zoomDisplayHeight = displayHeight; _zoomView = _draft;
        _zoomAnchor = new(Math.Clamp(pointer.X, 0, 1), Math.Clamp(pointer.Y, 0, 1));
        return true;
    }
    internal void MoveDrag(double x, double y)
    {
        if (_rotateStart is not null && _original is not null)
        {
            var distance = (x - _rotateStart.X) * _rotateDisplayWidth + (_rotateStart.Y - y) * _rotateDisplayHeight;
            _draft = Math.Abs(distance) < 1e-6 ? _rotateView : _rotateView!.Rotate(distance * .45);
            preview(_original.Id, _draft);
        }
        else if (_zoomStart is not null && _original is not null)
        {
            // Right/up contribute positively; left/down negatively. Equal logical
            // pixel travel on either axis has the same sensitivity, including at high DPI.
            var distance = (x - _zoomStart.X) * _zoomDisplayWidth + (_zoomStart.Y - y) * _zoomDisplayHeight;
            var steps = distance / 200 * Math.Log(2) / Math.Log(1.1);
            _draft = Math.Abs(distance) < 1e-6 ? _zoomView : _zoomView!.ZoomAt(_zoomAnchor!, steps, _aspect);
            preview(_original.Id, _draft);
        }
        else MovePan(x, y);
    }
    internal void MovePan(double x, double y)
    {
        if (_panStart is null || _original is null || !VideoFramingOptions.TryPointer(_original.Points, x, y, out var pointer)) return;
        _draft = _panView!.Pan(_panStart, pointer, _aspect);
        preview(_original.Id, _draft);
    }
    internal void EndDrag()
    {
        _panStart = null; _panView = null; _zoomStart = null; _zoomView = null; _zoomAnchor = null;
        _rotateStart = null; _rotateView = null; Flush();
    }
    internal bool BeginRotate(ScreenOptions screen, double x, double y, double displayWidth, double displayHeight)
    {
        if (!VideoFramingOptions.TryPointer(screen.Points, x, y, out _) || !Begin(screen)) return false;
        _debounce.Stop();
        _rotateStart = new(x, y); _rotateView = _draft;
        _rotateDisplayWidth = displayWidth; _rotateDisplayHeight = displayHeight;
        return true;
    }
    private void FinishWheel(object? sender, EventArgs e) => Flush();
    internal void Flush()
    {
        _debounce.Stop();
        if (_disposed || _saving || Dragging || _original is null || _draft is null) return;
        if (_draft == _original.Framing) { Cancel("Unchanged"); return; }
        var original = _original; var draft = _draft;
        _saving = true;
        var task = _saveTask = SaveAsync(original, draft);
        _tasks.Run(_ => task, "Save video framing");
    }
    private async Task SaveAsync(ScreenOptions original, VideoFramingOptions draft)
    {
        try
        {
            await save(original, draft, _tasks.Token);
            if (!_disposed) flash(original.Id, true);
        }
        catch (OperationCanceledException) when (_tasks.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Telemetry.Error("VideoFramingSaveFailed", ex, new { ScreenId = original.Id, ScreenName = original.Name });
            if (!_disposed) flash(original.Id, false);
        }
        finally
        {
            _saving = false;
            if (_original == original) Cancel("GestureFinished");
        }
    }
    internal void Configure(ExperienceOptions options, string display)
    {
        if (_original is null) return;
        var current = options.ScreensFor(display).FirstOrDefault(s => s.Id == _original.Id);
        if (!options.ConfigurationMode || current is null || !current.IsVideo || current.IsLocked || current.Points != _original.Points ||
            current.Perspective != _original.Perspective || current.Framing != _original.Framing && !(_saving && current.Framing == _draft))
            Cancel("ConfigurationChanged");
    }
    internal void Cancel(string reason)
    {
        _debounce.Stop();
        if (_original is not null) preview(_original.Id, null);
        _original = null; _draft = null; _panStart = null; _panView = null;
        _zoomStart = null; _zoomView = null; _zoomAnchor = null;
        _rotateStart = null; _rotateView = null;
    }
    internal async Task FinishAsync()
    {
        if (Dragging) EndDrag(); else Flush();
        await _saveTask;
    }
    public async ValueTask DisposeAsync()
    {
        await FinishAsync();
        Stop(); await _tasks.DisposeAsync();
    }
    internal void Stop()
    {
        if (_disposed) return;
        _disposed = true; Cancel("Shutdown"); _debounce.Dispose(); _tasks.Cancel();
    }
}
