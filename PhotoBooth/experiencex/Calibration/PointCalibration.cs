using PhotoBooth.Diagnostics;
using Forms = System.Windows.Forms;

namespace ExperienceX;

internal sealed class PointCalibration(Func<ExperienceOptions> options,
    Func<IEnumerable<IPointSurface>> windows, PointConfigurationStore store, Func<Task> reload) : IAsyncDisposable
{
    private readonly AsyncTaskScope _tasks = new("Calibration");
    private bool _stopping;
    public void RequestFinish(IPointSurface window) => _tasks.Run(_ => FinishAsync(window), "Save calibration");
    private readonly Dictionary<string, PointHitCycle> _cycles = new(StringComparer.OrdinalIgnoreCase);
    private IPointSurface? _active;
    private PointOptions? _original, _draft;
    private int _selected;
    public IPointSurface? SelectedScreen
    {
        get; private set;
    }
    private static string Identity(IPointSurface surface) => surface.DisplayName + "/" + surface.ScreenId;
    private PointOptions Coordinates(ExperienceOptions settings, IPointSurface surface) => settings.PointsFor(surface.DisplayName, surface.ScreenId);
    private bool IsMat(IPointSurface surface) => options().ScreensFor(surface.DisplayName).FirstOrDefault(s => s.Id == surface.ScreenId)?.IsMat == true;
    public void SelectScreen(IPointSurface? surface)
    {
        Cancel("ScreenSelectionChanged");
        SelectedScreen = surface;
        foreach (var item in windows())
        {
            item.SelectScreen(item == surface);
            item.ShowCalibration(-1);
        }
    }

    private bool _saving;
    private string? _localSaveDisplay;
    private PointOptions? _localSaveCoordinates;
    private double _dragOffsetX, _dragOffsetY;
    private bool _draggingScreen;
    private readonly Dictionary<string, List<PointOptions>> _undo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _revisions = new(StringComparer.OrdinalIgnoreCase);
    private void Show(IPointSurface owner, int selected)
    {
        SelectedScreen = owner;
        foreach (var window in windows())
        {
            window.SelectScreen(window == owner);
            window.ShowCalibration(window == owner ? selected : -1);
        }
    }
    public bool Begin(IPointSurface window, double x, double y, int? chosenPoint = null)
    {
        if (_stopping || !options().ConfigurationMode || _saving)
            return false;
        Cancel("NewSelection");
        var original = Coordinates(options(), window);
        if (!_cycles.TryGetValue(Identity(window), out var cycle))
            _cycles[Identity(window)] = cycle = new();
        var selected = chosenPoint ?? cycle.Pick(PointEditing.Hits(original, x, y, window.PixelWidth, window.PixelHeight,
            options().PointHitDiameterPercent));
        Show(window, selected);
        if (selected < 0)
            return false;
        _active = window;
        _original = original;
        _draft = original;
        _selected = selected;
        _draggingScreen = false;
        var point = PointEditing.Points(original)[selected];
        _dragOffsetX = point.X - x;
        _dragOffsetY = point.Y - y;
        Telemetry.Info("ScreenPointSelected", new
        {
            window.DisplayName,
            window.ScreenName,
            Point = selected + 1
        });
        return true;
    }
    public bool BeginScreenDrag(IPointSurface window, double x, double y)
    {
        if (_stopping || !options().ConfigurationMode || _saving)
            return false;
        Cancel("NewSelection");
        var original = Coordinates(options(), window);
        if (!ScreenSelection.Contains(original, x, y))
            return false;
        Show(window, -1);
        _active = window;
        _original = _draft = original;
        _selected = -1;
        _draggingScreen = true;
        _dragOffsetX = x;
        _dragOffsetY = y;
        Telemetry.Info("ScreenDragStarted", new { window.DisplayName, window.ScreenName, window.ScreenId });
        return true;
    }
    public void Move(IPointSurface window, double x, double y)
    {
        if (_draggingScreen)
        {
            if (_active != window || _original is null || _saving)
                return;
            _draft = PointEditing.Translate(_original, x - _dragOffsetX, y - _dragOffsetY);
            window.PreviewPoints(_draft);
        }
        else
            MovePoint(window, x + _dragOffsetX, y + _dragOffsetY);
    }
    private void MovePoint(IPointSurface window, double x, double y)
    {
        if (_active != window || _draft is null || _saving)
            return;
        _draft = PointEditing.Move(_draft, _selected, x, y, IsMat(window));
        window.PreviewPoints(_draft);
    }
    public async Task<bool> FinishAsync(IPointSurface window, bool remember = true)
    {
        if (_stopping)
            return false;
        foreach (var item in windows())
            item.FadeCalibration();
        if (_active != window || _original is null || _draft is null || _saving)
            return false;
        var original = _original;
        var edited = _draft;
        var selected = _selected;
        _active = null;
        _original = null;
        _draft = null;
        _draggingScreen = false;
        window.ReleaseCalibrationCapture();
        if (edited == original)
        {
            window.PreviewPoints(null);
            return false;
        }
        _saving = true;
        _localSaveDisplay = Identity(window);
        _localSaveCoordinates = edited;
        try
        {
            await store.SaveAsync(window.DisplayName, original, edited, window.ScreenId);
            if (_stopping)
                return false;
            await reload();
            if (_stopping)
                return false;
            window.PreviewPoints(null);
            if (!await window.ConfirmCalibrationAsync(edited) || _stopping || Coordinates(options(), window) != edited)
            {
                Telemetry.Warning("PointConfirmationSuperseded", new
                {
                    window.DisplayName
                });
                return false;
            }
            if (remember)
            {
                if (!_undo.TryGetValue(Identity(window), out var history))
                    _undo[Identity(window)] = history = [];
                history.Add(original);
                if (history.Count > 20)
                    history.RemoveAt(0);
            }
            if (selected < 0)
                window.FlashCalibrationPoints(IsMat(window) ? -1 : 15);
            else
                window.FlashCalibration(selected, true);
            Telemetry.Info("PointsSaved", new
            {
                window.DisplayName,
                window.ScreenName,
                Point = selected < 0 ? (int?)null : selected + 1,
                Coordinates = edited
            });
            return true;
        }
        catch (PointEditConflictException ex)
        {
            window.PreviewPoints(null);
            await reload();
            Telemetry.Warning("PointEditCancelled", new
            {
                window.DisplayName,
                window.ScreenName,
                Reason = ex.Message
            });
        }
        catch (Exception ex)
        {
            if (_stopping)
                return false;
            window.PreviewPoints(null);
            window.FlashCalibration(Math.Max(0, selected), false);
            Telemetry.Error("PointsSaveFailed", ex, new
            {
                window.DisplayName
            });
        }
        finally { _saving = false; _localSaveDisplay = null; _localSaveCoordinates = null; }
        return false;
    }
    public void Cancel(string reason)
    {
        var active = _active;
        _active = null;
        _original = null;
        _draft = null;
        _draggingScreen = false;
        if (active is null)
            return;
        active.ReleaseCalibrationCapture();
        active.PreviewPoints(null);
        foreach (var window in windows())
            window.FadeCalibration();
        Telemetry.Info("PointEditCancelled", new
        {
            active.DisplayName,
            active.ScreenName,
            Reason = reason
        });
    }
    public void ConfigurationChanged(ExperienceOptions next)
    {
        if (!next.ConfigurationMode)
            SelectScreen(null);
        if (SelectedScreen is not null && !next.ScreensFor(SelectedScreen.DisplayName).Any(s => s.Id == SelectedScreen.ScreenId))
            SelectScreen(null);
        if (!next.ConfigurationMode || (_active is not null && Coordinates(next, _active) != _original))
            Cancel(next.ConfigurationMode ? "RemoteCoordinatesChanged" : "ConfigurationModeDisabled");
    }
    public Task ConfigurationAppliedAsync(ExperienceOptions previous, ExperienceOptions next)
    {
        if (_stopping)
            return Task.CompletedTask;
        var confirmations = new List<Task>();
        foreach (var window in windows())
        {
            // Creating initial display defaults is not a remote calibration save.
            if (!previous.DisplayScreens.Keys.Any(name => string.Equals(name, window.DisplayName, StringComparison.OrdinalIgnoreCase)))
                continue;
            var beforeScreen = previous.ScreensFor(window.DisplayName).FirstOrDefault(s => window.ScreenId is null || s.Id == window.ScreenId);
            var afterScreen = next.ScreensFor(window.DisplayName).FirstOrDefault(s => window.ScreenId is null || s.Id == window.ScreenId);
            if (beforeScreen is null || afterScreen is null)
                continue;
            var renamed = beforeScreen.Name != afterScreen.Name;
            var before = Coordinates(previous, window);
            var after = Coordinates(next, window);
            if ((!renamed && before == after) || (string.Equals(_localSaveDisplay, Identity(window), StringComparison.OrdinalIgnoreCase) && _localSaveCoordinates == after))
                continue;
            _undo.Remove(Identity(window));
            var revision = _revisions.GetValueOrDefault(Identity(window)) + 1;
            _revisions[Identity(window)] = revision;
            var oldPoints = PointEditing.Points(before);
            var newPoints = PointEditing.Points(after);
            var mask = 0;
            for (int i = 0; i < Math.Min(4, Math.Min(oldPoints.Length, newPoints.Length)); i++)
                if (oldPoints[i] != newPoints[i])
                    mask |= 1 << i;
            if (mask == 0)
                mask = 15; // Enabled-only changes confirm at all four corners.
            if (afterScreen.IsMat) mask = -1;
            confirmations.Add(ConfirmRemoteAsync(window, after, mask, revision));
        }
        return Task.WhenAll(confirmations);
    }
    private async Task ConfirmRemoteAsync(IPointSurface window, PointOptions coordinates, int mask, long revision)
    {
        try
        {
            if (!await window.ConfirmCalibrationAsync(coordinates) || _stopping || _revisions.GetValueOrDefault(Identity(window)) != revision || Coordinates(options(), window) != coordinates)
                return;
            window.FlashCalibrationPoints(mask);
            Telemetry.Info("PointsRemoteChangeApplied", new
            {
                window.DisplayName,
                window.ScreenName,
                Points = Enumerable.Range(0, coordinates.Vertices.Count).Where(i => mask == -1 || (i < 32 && (mask & (1 << i)) != 0)).Select(i => i + 1).ToArray(),
                Coordinates = coordinates
            });
        }
        catch (Exception ex) { Telemetry.Error("PointsRenderConfirmationFailed", ex, new { window.DisplayName }); }
    }
    public async Task ResetAsync(IPointSurface window)
    {
        if (!options().ConfigurationMode || _saving)
            return;
        Cancel("Reset");
        _active = window;
        _original = Coordinates(options(), window);
        _draft = new();
        _selected = -1;
        window.PreviewPoints(_draft);
        await FinishAsync(window);
    }
    public async Task InsertPointAsync(IPointSurface window, int edge, ScreenPoint point)
    {
        if (_stopping || !options().ConfigurationMode || _saving || !IsMat(window)) return;
        Cancel("InsertPoint");
        var original = Coordinates(options(), window);
        if (edge < 0 || edge >= original.Vertices.Count) return;
        var points = original.Vertices.ToList();
        points.Insert(edge + 1, point);
        var edited = original with { Enabled = true, Vertices = points };
        try { edited.ValidatePolygon(); } catch (ArgumentException) { return; }
        Show(window, edge + 1);
        _active = window; _original = original; _draft = edited; _selected = edge + 1;
        window.PreviewPoints(edited);
        await FinishAsync(window);
    }
    public void RequestInsertPoint(IPointSurface window, int edge, ScreenPoint point) =>
        _tasks.Run(_ => InsertPointAsync(window, edge, point), "Insert mat point");
    public async Task UndoAsync(IPointSurface window)
    {
        if (!options().ConfigurationMode || _saving)
            return;
        if (_active is not null)
        {
            Cancel("UndoDraft");
            return;
        }
        if (!_undo.TryGetValue(Identity(window), out var history) || history.Count == 0)
            return;
        var restore = history[^1];
        _active = window;
        _original = Coordinates(options(), window);
        _draft = restore;
        _selected = -1;
        window.PreviewPoints(restore);
        if (await FinishAsync(window, false))
            history.RemoveAt(history.Count - 1);
    }
    public async Task<bool> DeleteSelectedAsync()
    {
        var window = SelectedScreen;
        if (_stopping || !options().ConfigurationMode || _saving || window?.ScreenId is null)
            return false;
        var expected = options().ScreensFor(window.DisplayName).FirstOrDefault(s => s.Id == window.ScreenId);
        if (expected is null)
            return false;
        Cancel("ScreenDeleted");
        _saving = true;
        try
        {
            await store.DeleteScreenAsync(window.DisplayName, expected);
            if (_stopping)
                return false;
            await reload();
            if (_stopping)
                return false;
            if (SelectedScreen == window)
                SelectScreen(null);
            _undo.Remove(Identity(window));
            _revisions.Remove(Identity(window));
            _cycles.Remove(Identity(window));
            Telemetry.Info("ScreenDeleted", new { window.DisplayName, ScreenName = expected.Name, expected.Id });
            return true;
        }
        catch (PointEditConflictException ex)
        {
            await reload();
            Telemetry.Warning("ScreenDeletionCancelled", new { window.DisplayName, window.ScreenId, Reason = ex.Message });
        }
        catch (Exception ex) { Telemetry.Error("ScreenDeletionFailed", ex, new { window.DisplayName, window.ScreenId }); }
        finally { _saving = false; }
        return false;
    }
    public bool Key(IPointSurface window, Forms.Keys key)
    {
        if (_stopping || !options().ConfigurationMode || _saving)
            return false;
        var code = key & Forms.Keys.KeyCode;
        if (key == Forms.Keys.Delete)
        {
            _tasks.Run(_ => DeleteSelectedAsync(), "Delete selected screen");
            return true;
        }
        if ((key & (Forms.Keys.Control | Forms.Keys.Alt)) == 0 && code == Forms.Keys.Z)
        {
            _tasks.Run(_ => UndoAsync(window), "Undo calibration");
            return true;
        }
        if ((key & (Forms.Keys.Control | Forms.Keys.Alt)) == 0 && code == Forms.Keys.R)
        {
            _tasks.Run(_ => ResetAsync(window), "Reset calibration");
            return true;
        }
        if (code == Forms.Keys.Escape)
        {
            Cancel("Escape");
            foreach (var w in windows())
                w.FadeCalibration();
            return true;
        }
        if (code == Forms.Keys.Enter)
        {
            RequestFinish(window);
            return true;
        }
        if (code is not (Forms.Keys.Tab or Forms.Keys.Left or Forms.Keys.Right or Forms.Keys.Up or Forms.Keys.Down))
            return false;
        if (_active is not null && _active != window)
            Cancel("KeyboardScreenChanged");
        var existed = _active == window;
        if (!existed)
        {
            Cancel("KeyboardDisplayChanged");
            _active = window;
            _original = Coordinates(options(), window);
            _draft = _original;
            _selected = 0;
        }
        if (code == Forms.Keys.Tab)
        {
            var backwards = (key & Forms.Keys.Shift) != 0;
            var count = _draft!.Vertices.Count;
            _selected = existed && _selected >= 0 ? (_selected + (backwards ? count - 1 : 1)) % count : (backwards ? count - 1 : 0);
            _draggingScreen = false;
            Show(window, _selected);
            return true;
        }
        _draggingScreen = false;
        if (_selected < 0)
            _selected = 0;
        var p = PointEditing.Points(_draft!)[_selected];
        var step = (key & Forms.Keys.Shift) != 0 ? 10 : 1;
        var dx = code == Forms.Keys.Left ? -step : code == Forms.Keys.Right ? step : 0;
        var dy = code == Forms.Keys.Up ? -step : code == Forms.Keys.Down ? step : 0;
        MovePoint(window, p.X + (double)dx / Math.Max(1, window.PixelWidth - 1), p.Y + (double)dy / Math.Max(1, window.PixelHeight - 1));
        Show(window, _selected);
        return true;
    }
    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        Cancel("Shutdown");
        await _tasks.DisposeAsync();
    }
}
