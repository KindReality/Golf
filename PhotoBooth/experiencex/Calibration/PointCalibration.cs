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
    private PointHandle? _focusedPoint;
    private PointSnapping? _pointDrag;
    private readonly Dictionary<PointHandle, HashSet<PointHandle>> _detached = new();
    private readonly HashSet<IPointSurface> _previewed = [];
    private sealed record Change(IPointSurface Surface, PointOptions Before, PointOptions After);
    private sealed record UndoFrame(Change[] Changes);
    private Change[]? _restoreChanges;
    private readonly Dictionary<string, PointOptions> _localSaves = new(StringComparer.OrdinalIgnoreCase);
    public IPointSurface? SelectedScreen
    {
        get; private set;
    }
    public bool HasSelectedPoint(IPointSurface surface) => _focusedPoint?.Surface == surface;
    private static string Identity(IPointSurface surface) => surface.DisplayName + "/" + surface.ScreenId;
    private PointOptions Coordinates(ExperienceOptions settings, IPointSurface surface) => settings.PointsFor(surface.DisplayName, surface.ScreenId);
    private static ScreenOptions? Definition(ExperienceOptions settings, IPointSurface surface) => settings.ScreensFor(surface.DisplayName).FirstOrDefault(s => surface.ScreenId is null || s.Id.Equals(surface.ScreenId, StringComparison.OrdinalIgnoreCase));
    private bool IsMask(IPointSurface surface) => Definition(options(), surface)?.IsMask == true;
    private bool IsVisualization(IPointSurface surface) => Definition(options(), surface)?.IsVisualization == true;
    private bool IsLedMatrix(IPointSurface surface) => Definition(options(), surface) is { IsLed: true, IsLedArray: false };
    private bool _rotatingLed;
    public void SelectScreen(IPointSurface? surface)
    {
        Cancel("ScreenSelectionChanged");
        _focusedPoint = null;
        SelectedScreen = surface;
        foreach (var item in windows())
        {
            item.SelectScreen(item == surface);
            item.ShowCalibration(-1);
        }
    }

    private bool _saving;
    internal bool Busy => _saving;
    internal bool Editing => _active is not null;
    private double _dragOffsetX, _dragOffsetY;
    private bool _draggingScreen;
    private bool _draggingEdge;
    private int _edge;
    private readonly Dictionary<string, List<UndoFrame>> _undo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _revisions = new(StringComparer.OrdinalIgnoreCase);
    private bool CanJoin(PointHandle a, PointHandle b) => a == b ||
        (a.Surface != b.Surface && string.Equals(a.Surface.DisplayName, b.Surface.DisplayName, StringComparison.OrdinalIgnoreCase) &&
         !(_detached.TryGetValue(a, out var first) && first.Contains(b)) && !(_detached.TryGetValue(b, out var second) && second.Contains(a)));
    private PointSnapping NewPointDrag(IPointSurface owner)
    {
        var surfaces = (IsVisualization(owner) ? new[] { owner } : windows().Where(w => w.DisplayName.Equals(owner.DisplayName, StringComparison.OrdinalIgnoreCase) && !IsVisualization(w)).Append(owner)).Distinct().ToArray();
        return new(surfaces.ToDictionary(w => w, w => Coordinates(options(), w)), surfaces.ToDictionary(w => w, IsMask), CanJoin, surfaces.ToDictionary(w=>w,IsLedMatrix), surfaces.ToDictionary(w => w, w => Definition(options(), w)?.Type ?? "video"));
    }
    private void Show(IPointSurface owner, int selected)
    {
        SelectedScreen = owner;
        _focusedPoint = selected >= 0 ? new(owner, selected) : null;
        var group = selected >= 0 && selected < Coordinates(options(), owner).Vertices.Count ? _pointDrag ?? NewPointDrag(owner) : null;
        if (group is not null && _pointDrag is null)
            group.Select(new(owner, selected));
        foreach (var window in windows())
        {
            window.SelectScreen(window == owner);
            var point = group?.Moving.Where(h => h.Surface == window).Select(h => (int?)h.Index).FirstOrDefault();
            window.ShowCalibration(point ?? (window == owner ? selected : -1));
        }
        var target = group is not null && selected >= 0 ? group.JoinTarget(new(owner, selected), options().PointHitDiameterPercent) : null;
        foreach (var window in windows()) window.ShowJoinTarget(target?.Surface == window ? target.Value.Index : -1);
    }
    private void PreviewGroup()
    {
        if (_pointDrag is null || _active is null)
            return;
        var previews = new Dictionary<IPointSurface, PointOptions?>();
        foreach (var entry in _pointDrag.Drafts)
        {
            var changed = entry.Value != _pointDrag.Originals[entry.Key] || entry.Key == _active;
            previews[entry.Key] = changed ? entry.Value : null;
            if (changed)
                _previewed.Add(entry.Key);
            else
                _previewed.Remove(entry.Key);
        }
        _active.PreviewPointGroup(previews);
        _draft = _pointDrag.Drafts[_active];
    }
    private void ClearPreviews()
    {
        if (_previewed.FirstOrDefault() is { } owner)
            owner.PreviewPointGroup(_previewed.ToDictionary(w => w, _ => (PointOptions?)null));
        _previewed.Clear();
    }
    private void ForgetUndo(string identity)
    {
        foreach (var history in _undo.Values)
            history.RemoveAll(frame => frame.Changes.Any(c => Identity(c.Surface).Equals(identity, StringComparison.OrdinalIgnoreCase)));
    }
    private void ForgetDetachment(IPointSurface surface)
    {
        foreach (var entry in _detached.Where(e => e.Key.Surface == surface || e.Value.Any(h => h.Surface == surface)).ToArray())
            _detached.Remove(entry.Key);
    }
    public bool DisconnectPoint()
    {
        if (_focusedPoint is not { } selected || _saving || _stopping || !options().ConfigurationMode)
            return false;
        var group = _pointDrag ?? NewPointDrag(selected.Surface);
        if (_pointDrag is null)
            group.Select(selected);
        var others = group.Moving.Where(h => h != selected).ToHashSet();
        if (others.Count != 0)
        {
            _detached[selected] = others;
            group.Detach(selected);
            Show(selected.Surface, selected.Index);
            Telemetry.Info("PointDisconnected", new
            {
                selected.Surface.DisplayName,
                selected.Surface.ScreenName,
                Point = selected.Index + 1,
                LinkedPoints = others.Count
            });
        }
        return true; // A selected point must never fall through to deleting its screen.
    }
    public bool Begin(IPointSurface window, double x, double y, int? chosenPoint = null)
    {
        if (_stopping || !options().ConfigurationMode || _saving)
            return false;
        if (IsVisualization(window) && SelectedScreen != window)
        {
            SelectScreen(window);
            return false;
        }
        Cancel("NewSelection");
        var original = Coordinates(options(), window);
        if (!_cycles.TryGetValue(Identity(window), out var cycle))
            _cycles[Identity(window)] = cycle = new();
        var selected = chosenPoint ?? cycle.Pick(PointEditing.Hits(original, x, y, window.PixelWidth, window.PixelHeight,
            options().PointHitDiameterPercent));
        if (selected >= 0)
        {
            _pointDrag = NewPointDrag(window);
            _pointDrag.Select(new(window, selected));
        }
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
        if (IsVisualization(window))
        {
            SelectScreen(window);
            return false;
        }
        Cancel("NewSelection");
        var original = Coordinates(options(), window);
        if (!(Definition(options(), window) is { IsLed: true } led ? LedGeometry.Contains(led,x,y,window.PixelWidth,window.PixelHeight) : ScreenSelection.Contains(original, x, y)))
            return false;
        Show(window, -1);
        _active = window;
        _original = _draft = original;
        _selected = -1;
        _draggingScreen = true;
        _dragOffsetX = x;
        _dragOffsetY = y;
        Telemetry.Info("ScreenDragStarted", new
        {
            window.DisplayName,
            window.ScreenName,
            window.ScreenId
        });
        return true;
    }
    public bool BeginEdgeDrag(IPointSurface window, int edge, double x, double y)
    {
        if (_stopping || _saving || !options().ConfigurationMode || IsVisualization(window) && SelectedScreen != window) return false;
        var original = Coordinates(options(), window);
        if (edge < 0 || edge >= original.Vertices.Count) return false;
        Cancel("EdgeDrag"); _pointDrag = NewPointDrag(window); Show(window, -1);
        _active = window; _original = _draft = original; _selected = -1;
        _edge = edge; _draggingEdge = true; _dragOffsetX = x; _dragOffsetY = y;
        Telemetry.Info("ScreenEdgeSelected", new { window.DisplayName, window.ScreenName, Edge = edge + 1 });
        return true;
    }
    public void Move(IPointSurface window, double x, double y)
    {
        if (_draggingEdge)
        {
            if (_active != window || _pointDrag is null || _saving) return;
            _pointDrag.MoveEdge(window, _edge, x - _dragOffsetX, y - _dragOffsetY); PreviewGroup(); return;
        }
        if (_rotatingLed)
        {
            if (_active != window || _original is null || _saving) return;
            _draft = LedGeometry.Rotate(_original,_dragOffsetX,_dragOffsetY,x,y,window.PixelWidth,window.PixelHeight);
            window.PreviewPoints(_draft);
            return;
        }
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
        _pointDrag ??= NewPointDrag(window);
        var selected = new PointHandle(window, _selected);
        _pointDrag.Move(selected, x, y);
        foreach (var entry in _detached.ToArray())
        {
            if (!_pointDrag.Drafts.ContainsKey(entry.Key.Surface))
                continue;
            var point = _pointDrag.Position(entry.Key);
            var radius = PointEditing.HitRadius(window.PixelWidth, window.PixelHeight, options().PointHitDiameterPercent);
            if (entry.Value.All(other => !_pointDrag.Drafts.ContainsKey(other.Surface) || PointSnapping.Distance(point, _pointDrag.Position(other), window) > radius * radius))
                _detached.Remove(entry.Key);
        }
        PreviewGroup();
        Show(window, _selected);
    }
    private void JoinSelected(IPointSurface window)
    {
        if (_focusedPoint is not { } focus || focus.Surface != window || _draggingScreen || _draggingEdge || _rotatingLed) return;
        if (_active is null)
        {
            _active = window; _original = _draft = Coordinates(options(), window); _selected = focus.Index;
            _dragOffsetX = _dragOffsetY = 0;
            _pointDrag = NewPointDrag(window); _pointDrag.Select(focus);
        }
        if (_active != window || _pointDrag is null) return;
        var selected = new PointHandle(window, _selected);
        var undo = _pointDrag.Join(selected, options().PointHitDiameterPercent, _dragOffsetX, _dragOffsetY);
        if (undo is not null)
        {
            var snapped = _pointDrag.Position(selected);
            var before = PointEditing.Points(undo.Drafts[window])[_selected];
            _dragOffsetX += snapped.X - before.X;
            _dragOffsetY += snapped.Y - before.Y;
            Telemetry.Info("PointsJoined", new
            {
                window.DisplayName,
                window.ScreenName,
                Point = _selected + 1,
                LinkedPoints = _pointDrag.Moving.Count,
                Position = snapped
            });
        }
        PreviewGroup();
        Show(window, _selected);
    }
    public async Task<bool> FinishAsync(IPointSurface window, bool remember = true)
    {
        if (_stopping)
            return false;
        foreach (var item in windows())
        { item.ShowJoinTarget(-1); item.FadeCalibration(); }
        if (_active != window || _original is null || _draft is null || _saving)
            return false;
        var original = _original;
        var edited = _draft;
        var selected = _selected;
        var changes = _restoreChanges ?? (_pointDrag is null ? [new Change(window, original, edited)] :
            _pointDrag.Drafts.Where(entry => entry.Value != _pointDrag.Originals[entry.Key]).Select(entry => new Change(entry.Key, _pointDrag.Originals[entry.Key], entry.Value)).ToArray());
        changes = changes.Where(c => c.Before != c.After).ToArray();
        _restoreChanges = null;
        _pointDrag = null;
        _active = null;
        _original = null;
        _draft = null;
        _draggingScreen = false;
        _draggingEdge = false;
        window.ReleaseCalibrationCapture();
        _rotatingLed = false;
        if (changes.Length == 0)
        {
            ClearPreviews();
            window.PreviewPoints(null);
            if (_focusedPoint is { } focus)
                Show(focus.Surface, focus.Index);
            return false;
        }
        _saving = true;
        foreach (var change in changes)
            _localSaves[Identity(change.Surface)] = change.After;
        try
        {
            await store.SaveManyAsync(window.DisplayName, changes.Select(c => new ScreenPointChange(c.Surface.ScreenId, c.Before, c.After)).ToArray());
            if (_stopping)
                return false;
            await reload();
            if (_stopping)
                return false;
            ClearPreviews();
            window.PreviewPoints(null);
            var confirmed = await Task.WhenAll(changes.Select(c => c.Surface.ConfirmCalibrationAsync(c.After)));
            if (confirmed.Any(applied => !applied) || _stopping || changes.Any(c => Coordinates(options(), c.Surface) != c.After))
            {
                Telemetry.Warning("PointConfirmationSuperseded", new
                {
                    window.DisplayName
                });
                return false;
            }
            if (remember)
            {
                var frame = new UndoFrame(changes);
                foreach (var change in changes)
                {
                    if (!_undo.TryGetValue(Identity(change.Surface), out var history))
                        _undo[Identity(change.Surface)] = history = [];
                    history.Add(frame);
                    if (history.Count > 20)
                        history.RemoveAt(0);
                }
            }
            if (_focusedPoint is { } focus)
                Show(focus.Surface, focus.Index);
            foreach (var change in changes)
                if (change.Surface == window && selected >= 0)
                    window.FlashCalibration(selected, true);
                else
                    change.Surface.FlashCalibrationPoints(IsMask(change.Surface) || change.After.Vertices.Count > 4 ? -1 : 15);
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
        finally { _saving = false; _localSaves.Clear(); ClearPreviews(); }
        return false;
    }
    public void Cancel(string reason)
    {
        foreach (var window in windows()) window.ShowJoinTarget(-1);
        _rotatingLed = false;
        var active = _active;
        _active = null;
        _original = null;
        _draft = null;
        _pointDrag = null;
        _restoreChanges = null;
        _focusedPoint = null;
        ClearPreviews();
        _draggingScreen = false;
        _draggingEdge = false;
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
        {
            _detached.Clear();
            SelectScreen(null);
        }
        bool Exists(PointHandle handle) => Definition(next, handle.Surface) is { } screen && handle.Index < screen.Points.Vertices.Count;
        foreach (var entry in _detached.ToArray())
        {
            entry.Value.RemoveWhere(h => !Exists(h));
            if (!Exists(entry.Key) || entry.Value.Count == 0)
                _detached.Remove(entry.Key);
        }
        foreach (var history in _undo.Values)
            history.RemoveAll(frame => frame.Changes.Any(c => Definition(next, c.Surface) is null));
        if (_focusedPoint is { } focused && focused.Index >= Coordinates(next, focused.Surface).Vertices.Count)
            _focusedPoint = null;
        if (SelectedScreen is not null && !next.ScreensFor(SelectedScreen.DisplayName).Any(s => SelectedScreen.ScreenId is null || s.Id == SelectedScreen.ScreenId))
            SelectScreen(null);
        if (!next.ConfigurationMode || (_active is not null && (Coordinates(next, _active) != _original || _pointDrag?.Current(next) == false)))
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
            if (afterScreen is null)
            {
                ForgetUndo(Identity(window));
                ForgetDetachment(window);
                continue;
            }
            if (beforeScreen is null)
                continue;
            if (beforeScreen.Points.Vertices.Count != afterScreen.Points.Vertices.Count)
            {
                if (_focusedPoint?.Surface == window)
                    _focusedPoint = null;
                ForgetDetachment(window);
            }
            var renamed = beforeScreen.Name != afterScreen.Name;
            var before = Coordinates(previous, window);
            var after = Coordinates(next, window);
            if ((!renamed && before == after) || (_localSaves.TryGetValue(Identity(window), out var local) && local == after))
                continue;
            ForgetUndo(Identity(window));
            if (before != after)
                ForgetDetachment(window);
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
            if (afterScreen.IsMask || after.Vertices.Count > 4)
                mask = -1;
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
        _draft = Definition(options(), window) is { IsLed: true } led ? LedGeometry.Initial(led.IsLedArray) : new();
        _selected = -1;
        window.PreviewPoints(_draft);
        await FinishAsync(window);
    }
    public async Task InsertPointAsync(IPointSurface window, int edge, ScreenPoint point)
    {
        if (_stopping || !options().ConfigurationMode || _saving || Definition(options(),window) is not { SupportsPointInsertion: true } definition)
            return;
        Cancel("InsertPoint");
        var original = Coordinates(options(), window);
        if (edge < 0 || edge >= original.Vertices.Count)
            return;
        PointOptions edited;
        try
        {
            edited = PointEditing.Insert(original,edge,point,definition.IsVideo);
            definition.ValidatePoints(edited);
        }
        catch (ArgumentException) { return; }
        Show(window, edge + 1);
        _active = window;
        _original = original;
        _draft = edited;
        _selected = edge + 1;
        window.PreviewPoints(edited);
        if (await FinishAsync(window)) Show(window, edge + 1);
    }
    public void RequestInsertPoint(IPointSurface window, int edge, ScreenPoint point) =>
        _tasks.Run(_ => InsertPointAsync(window, edge, point), "Insert screen point");
    public async Task UndoAsync(IPointSurface window, bool movementOnly = false)
    {
        if (!options().ConfigurationMode || _saving)
            return;
        if (_active is not null)
        {
            if (movementOnly)
            {
                Cancel("UndoMovementDraft");
                return;
            }
            if (_pointDrag?.UndoSnap() is { } snap && _focusedPoint is { } focus)
            {
                _detached[focus] = snap.Joined.ToHashSet();
                _dragOffsetX = snap.OffsetX;
                _dragOffsetY = snap.OffsetY;
                ClearPreviews();
                PreviewGroup();
                Show(focus.Surface, focus.Index);
                Telemetry.Info("PointSnapUndone", new
                {
                    window.DisplayName,
                    window.ScreenName
                });
            }
            else
                Cancel("UndoDraft");
            return;
        }
        if (!_undo.TryGetValue(Identity(window), out var history) || history.Count == 0)
            return;
        var restore = history[^1];
        if (restore.Changes.Any(c => Coordinates(options(), c.Surface) != c.After))
        {
            ForgetUndo(Identity(window));
            return;
        }
        _active = window;
        _original = Coordinates(options(), window);
        _draft = restore.Changes.First(c => Identity(c.Surface) == Identity(window)).Before;
        _restoreChanges = restore.Changes.Select(c => new Change(c.Surface, c.After, c.Before)).ToArray();
        foreach (var change in _restoreChanges)
            ForgetDetachment(change.Surface);
        _selected = -1;
        foreach (var change in _restoreChanges)
        {
            change.Surface.PreviewPoints(change.After);
            _previewed.Add(change.Surface);
        }
        if (await FinishAsync(window, false))
            foreach (var affected in _undo.Values)
                affected.Remove(restore);
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
            ForgetUndo(Identity(window));
            _revisions.Remove(Identity(window));
            _cycles.Remove(Identity(window));
            Telemetry.Info("ScreenDeleted", new
            {
                window.DisplayName,
                ScreenName = expected.Name,
                expected.Id
            });
            return true;
        }
        catch (PointEditConflictException ex)
        {
            await reload();
            Telemetry.Warning("ScreenDeletionCancelled", new
            {
                window.DisplayName,
                window.ScreenId,
                Reason = ex.Message
            });
        }
        catch (Exception ex) { Telemetry.Error("ScreenDeletionFailed", ex, new { window.DisplayName, window.ScreenId }); }
        finally { _saving = false; }
        return false;
    }
    public async Task MoveSelectedLayerAsync(bool front)
    {
        var selected = SelectedScreen;
        if (_stopping || _saving || !options().ConfigurationMode || selected?.ScreenId is null || IsVisualization(selected)) return;
        if (_active is not null)
        {
            if (_draft != _original)
            {
                if (!await FinishAsync(_active)) return;
            }
            else Cancel("LayerOrder");
        }
        if (_stopping || SelectedScreen != selected || !options().ConfigurationMode) return;
        _saving = true;
        try
        {
            await store.MoveScreenLayerAsync(selected.DisplayName, selected.ScreenId, front);
            await reload();
            Telemetry.Info("ScreenLayerChanged", new { selected.DisplayName, selected.ScreenName, selected.ScreenId, Position = front ? "Top" : "Bottom" });
        }
        catch (Exception ex) { Telemetry.Error("ScreenLayerChangeFailed", ex, new { selected.DisplayName, selected.ScreenId }); }
        finally { _saving = false; }
    }
    public bool Key(IPointSurface window, Forms.Keys key)
    {
        if (_stopping || !options().ConfigurationMode || _saving)
            return false;
        if (key is Forms.Keys.OemOpenBrackets or Forms.Keys.OemCloseBrackets)
        {
            _tasks.Run(_ => MoveSelectedLayerAsync(key == Forms.Keys.OemCloseBrackets), "Move screen layer");
            return true;
        }
        var code = key & Forms.Keys.KeyCode;
        if (key == Forms.Keys.J) { JoinSelected(window); return true; }
        if (key == Forms.Keys.Delete)
        {
            if (DisconnectPoint())
                return true;
            _tasks.Run(_ => DeleteSelectedAsync(), "Delete selected screen");
            return true;
        }
        if (IsVisualization(window) && SelectedScreen != window)
            return false;
        if (key == (Forms.Keys.Control | Forms.Keys.Z))
        {
            _tasks.Run(_ => UndoAsync(window, movementOnly: true), "Undo point movement");
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
            var focusedIndex = _focusedPoint is { } focus && focus.Surface == window ? focus.Index : 0;
            Cancel("KeyboardDisplayChanged");
            _active = window;
            _original = Coordinates(options(), window);
            _draft = _original;
            _selected = focusedIndex;
            _dragOffsetX = _dragOffsetY = 0;
            _pointDrag = NewPointDrag(window);
            _pointDrag.Select(new(window, _selected));
        }
        if (code == Forms.Keys.Tab)
        {
            var backwards = (key & Forms.Keys.Shift) != 0;
            var count = _draft!.Vertices.Count;
            _selected = existed && _selected >= 0 ? (_selected + (backwards ? count - 1 : 1)) % count : (backwards ? count - 1 : 0);
            _draggingScreen = false;
            _draggingEdge = false;
            _pointDrag?.Select(new(window, _selected));
            Show(window, _selected);
            return true;
        }
        _draggingScreen = false;
        if (_draggingEdge) { _draggingEdge = false; _selected = Math.Max(0, _selected); _pointDrag?.Select(new(window, _selected)); }
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
    internal bool BeginRotation(IPointSurface window, double x, double y)
    {
        if (!options().ConfigurationMode || _saving || _stopping || !IsLedMatrix(window)) return false;
        Cancel("RotateLEDMatrix"); Show(window,-1);
        _active=window; _original=_draft=Coordinates(options(),window); _selected=-1;
        _rotatingLed=true; _dragOffsetX=x; _dragOffsetY=y;
        return true;
    }
}
