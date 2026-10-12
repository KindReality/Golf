namespace ExperienceX;

internal readonly record struct PointHandle(IPointSurface Surface, int Index);

// A drag owns immutable originals and a shared draft for every editable screen on its display.
// Group membership is derived from exact coordinates, so it survives saving and restarting.
internal sealed class PointSnapping
{
    internal sealed record SnapUndo(Dictionary<IPointSurface, PointOptions> Drafts, HashSet<PointHandle> Moving,
        PointHandle[] Joined, double OffsetX, double OffsetY);
    private readonly Func<PointHandle, PointHandle, bool> _canJoin;
    private readonly Dictionary<IPointSurface, bool> _polygons;
    private readonly Stack<SnapUndo> _snaps = new();
    internal Dictionary<IPointSurface, PointOptions> Originals
    {
        get;
    }
    internal Dictionary<IPointSurface, PointOptions> Drafts
    {
        get; private set;
    }
    internal HashSet<PointHandle> Moving { get; private set; } = [];
    internal IEnumerable<PointHandle> Handles => Drafts.SelectMany(entry =>
        PointEditing.Points(entry.Value).Select((_, index) => new PointHandle(entry.Key, index)));

    internal PointSnapping(Dictionary<IPointSurface, PointOptions> originals, Dictionary<IPointSurface, bool> polygons,
        Func<PointHandle, PointHandle, bool> canJoin)
    {
        Originals = originals;
        Drafts = new(originals);
        _polygons = polygons;
        _canJoin = canJoin;
    }
    internal ScreenPoint Position(PointHandle handle) => PointEditing.Points(Drafts[handle.Surface])[handle.Index];
    internal void Select(PointHandle handle)
    {
        var point = Position(handle);
        Moving = Handles.Where(other => Position(other) == point && _canJoin(handle, other)).ToHashSet();
        Moving.Add(handle);
        _snaps.Clear();
    }
    internal void Detach(PointHandle handle)
    {
        Moving = [handle];
        _snaps.Clear();
    }
    internal bool Current(ExperienceOptions options) => Originals.All(entry =>
        options.PointsFor(entry.Key.DisplayName, entry.Key.ScreenId) == entry.Value &&
        Definition(options, entry.Key) is { IsVisualization: false } && (Definition(options, entry.Key)?.IsMat == true) == _polygons[entry.Key]);
    private static ScreenOptions? Definition(ExperienceOptions options, IPointSurface surface) =>
        options.ScreensFor(surface.DisplayName).FirstOrDefault(s => surface.ScreenId is null || s.Id == surface.ScreenId);

    internal SnapUndo? Move(PointHandle selected, double x, double y, double percent, double offsetX, double offsetY, bool snap = true)
    {
        if (!Moving.Contains(selected))
            Select(selected);
        var old = Position(selected);
        var target = new ScreenPoint(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
        var moved = Candidate(Moving, target);
        if (!Valid(moved))
        {
            double low = 0, high = 1;
            for (int step = 0; step < 40; step++)
            {
                var t = (low + high) / 2;
                if (Valid(Candidate(Moving, new(old.X + (target.X - old.X) * t, old.Y + (target.Y - old.Y) * t))))
                    low = t;
                else
                    high = t;
            }
            target = new(old.X + (target.X - old.X) * low, old.Y + (target.Y - old.Y) * low);
            moved = Candidate(Moving, target);
        }
        Drafts = moved;
        if (!snap || target == old)
            return null;
        var radius = PointEditing.HitRadius(selected.Surface.PixelWidth, selected.Surface.PixelHeight, percent);
        var candidates = Handles.Where(h => !Moving.Contains(h) && _canJoin(selected, h))
            .Select(h => (Handle: h, Distance: Distance(target, Position(h), selected.Surface)))
            .Where(h => h.Distance <= radius * radius).OrderBy(h => h.Distance).ToArray();
        foreach (var candidate in candidates)
        {
            var destination = Position(candidate.Handle);
            var joined = Handles.Where(h => !Moving.Contains(h) && Position(h) == destination && Moving.All(m => _canJoin(m, h))).ToArray();
            if (joined.Length == 0)
                continue;
            var group = Moving.Concat(joined).ToHashSet();
            var snapped = Candidate(group, destination);
            // Never collapse a screen or partially snap a linked group.
            if (!Valid(snapped))
                continue;
            var undo = new SnapUndo(new(Drafts), new(Moving), joined, offsetX, offsetY);
            _snaps.Push(undo);
            Moving = group;
            Drafts = snapped;
            return undo;
        }
        return null;
    }
    internal SnapUndo? UndoSnap()
    {
        if (!_snaps.TryPop(out var undo))
            return null;
        Drafts = new(undo.Drafts);
        Moving = new(undo.Moving);
        return undo;
    }
    internal static double Distance(ScreenPoint a, ScreenPoint b, IPointSurface surface) =>
        Math.Pow((a.X - b.X) * Math.Max(1, surface.PixelWidth - 1), 2) + Math.Pow((a.Y - b.Y) * Math.Max(1, surface.PixelHeight - 1), 2);
    private Dictionary<IPointSurface, PointOptions> Candidate(IEnumerable<PointHandle> handles, ScreenPoint point)
    {
        var result = new Dictionary<IPointSurface, PointOptions>(Drafts);
        foreach (var group in handles.GroupBy(h => h.Surface))
        {
            var shape = Drafts[group.Key];
            var vertices = shape.Vertices.ToArray();
            foreach (var handle in group)
                vertices[PointEditing.BoundaryIndex(shape, handle.Index)] = point;
            if (!vertices.SequenceEqual(shape.Vertices))
                result[group.Key] = shape with
                {
                    Enabled = true,
                    Vertices = vertices
                };
        }
        return result;
    }
    private bool Valid(Dictionary<IPointSurface, PointOptions> shapes)
    {
        try
        {
            foreach (var entry in shapes)
                if (_polygons[entry.Key])
                    entry.Value.ValidatePolygon();
                else
                    entry.Value.Validate();
            return true;
        }
        catch (ArgumentException) { return false; }
    }
}
