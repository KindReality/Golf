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
    private readonly Dictionary<IPointSurface,bool> _rectangles;
    private readonly Dictionary<IPointSurface, string>? _types;
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
        Func<PointHandle, PointHandle, bool> canJoin, Dictionary<IPointSurface,bool>? rectangles = null, Dictionary<IPointSurface, string>? types = null)
    {
        Originals = originals;
        Drafts = new(originals);
        _polygons = polygons;
        _rectangles = rectangles ?? originals.Keys.ToDictionary(k=>k,_=>false);
        _canJoin = canJoin;
        _types = types;
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
         Definition(options, entry.Key) is not null && (Definition(options, entry.Key)?.IsPolygon == true) == _polygons[entry.Key] &&
         (_types is null || _types[entry.Key] == Definition(options, entry.Key)?.Type) &&
         (Definition(options,entry.Key) is {IsLed:true,IsLedArray:false}) == _rectangles[entry.Key]);
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
    // Edges translate two distinct endpoint groups; they never snap those groups onto one another.
    internal void MoveEdge(IPointSurface owner, int edge, double dx, double dy)
    {
        var shape = Originals[owner];
        var endpoints = new[] { edge, (edge + 1) % shape.Vertices.Count };
        var anchors = endpoints.Select(index => new PointHandle(owner, PointEditing.BoundaryIndex(shape, index))).ToArray();
        Moving = anchors.SelectMany(anchor => Handles.Where(other =>
            PointEditing.Points(Originals[other.Surface])[other.Index] == PointEditing.Points(shape)[anchor.Index] && _canJoin(anchor, other))).ToHashSet();
        if (_rectangles[owner])
        {
            var a = shape.Vertices[endpoints[0]]; var b = shape.Vertices[endpoints[1]];
            var width = Math.Max(1, owner.PixelWidth - 1); var height = Math.Max(1, owner.PixelHeight - 1);
            var nx = -(b.Y - a.Y) * height; var ny = (b.X - a.X) * width;
            var distance = (dx * width * nx + dy * height * ny) / (nx * nx + ny * ny);
            dx = nx * distance / width; dy = ny * distance / height;
        }
        var points = Moving.Select(h => PointEditing.Points(Originals[h.Surface])[h.Index]).ToArray();
        if (_rectangles[owner])
        {
            double limit = 1;
            foreach (var point in points)
            {
                if (dx > 0) limit = Math.Min(limit, (1 - point.X) / dx); else if (dx < 0) limit = Math.Min(limit, -point.X / dx);
                if (dy > 0) limit = Math.Min(limit, (1 - point.Y) / dy); else if (dy < 0) limit = Math.Min(limit, -point.Y / dy);
            }
            dx *= limit; dy *= limit;
        }
        else
        {
            dx = Math.Clamp(dx, -points.Min(p => p.X), 1 - points.Max(p => p.X));
            dy = Math.Clamp(dy, -points.Min(p => p.Y), 1 - points.Max(p => p.Y));
        }
        Dictionary<IPointSurface, PointOptions> CandidateAt(double amount)
        {
            if (amount == 0 || dx == 0 && dy == 0) return new(Originals);
            var result = new Dictionary<IPointSurface, PointOptions>(Originals);
            foreach (var group in Moving.GroupBy(h => h.Surface))
            {
                var original = Originals[group.Key]; var handles = group.ToArray();
                if (_rectangles[group.Key] && handles.Length == 1)
                {
                    var before = PointEditing.Points(original)[handles[0].Index];
                    var target = new ScreenPoint(before.X + dx * amount, before.Y + dy * amount);
                    var resized = LedGeometry.Resize(original, handles[0].Index, target.X, target.Y, group.Key.PixelWidth, group.Key.PixelHeight);
                    result[group.Key] = Distance(PointEditing.Points(resized)[handles[0].Index], target, group.Key) < 1e-8 ? resized
                        : original with { Vertices = [new(double.NaN, 0), .. original.Vertices.Skip(1)] };
                }
                else
                {
                    var vertices = original.Vertices.ToArray();
                    foreach (var handle in handles) { var index = PointEditing.BoundaryIndex(original, handle.Index); vertices[index] = new(vertices[index].X + dx * amount, vertices[index].Y + dy * amount); }
                    result[group.Key] = dx == 0 && dy == 0 ? original : original with { Enabled = true, Vertices = vertices };
                }
            }
            return result;
        }
        var candidate = CandidateAt(1);
        if (!Valid(candidate))
        {
            double low = 0, high = 1;
            for (var iteration = 0; iteration < 40; iteration++) { var middle = (low + high) / 2; if (Valid(CandidateAt(middle))) low = middle; else high = middle; }
            candidate = CandidateAt(low);
        }
        Drafts = candidate;
    }
    internal static double Distance(ScreenPoint a, ScreenPoint b, IPointSurface surface) =>
        Math.Pow((a.X - b.X) * Math.Max(1, surface.PixelWidth - 1), 2) + Math.Pow((a.Y - b.Y) * Math.Max(1, surface.PixelHeight - 1), 2);
    private Dictionary<IPointSurface, PointOptions> Candidate(IEnumerable<PointHandle> handles, ScreenPoint point)
    {
        var result = new Dictionary<IPointSurface, PointOptions>(Drafts);
        foreach (var group in handles.GroupBy(h => h.Surface))
        {
            var shape = Drafts[group.Key];
            if(_rectangles[group.Key])
            {
                var groupHandles=group.ToArray();
                var resized=LedGeometry.Resize(shape,groupHandles[0].Index,point.X,point.Y,group.Key.PixelWidth,group.Key.PixelHeight);
                // A joined point must reach the same coordinate in every constrained shape.
                if(groupHandles.Length!=1 || Distance(PointEditing.Points(resized)[groupHandles[0].Index],point,group.Key)>1e-8)
                    resized=shape with{Vertices=[new(double.NaN,0),..shape.Vertices.Skip(1)]};
                result[group.Key]=resized;
                continue;
            }
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
                if (_rectangles[entry.Key])
                    LedGeometry.Validate(entry.Value,false);
                else if (entry.Value.Vertices.Count == 2)
                    LedGeometry.Validate(entry.Value,true);
                else if (_polygons[entry.Key])
                    entry.Value.ValidatePolygon();
                else
                    entry.Value.Validate();
            return true;
        }
        catch (ArgumentException) { return false; }
    }
}
