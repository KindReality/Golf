using System.Text.Json.Serialization;

namespace ExperienceX;

public sealed record ScreenPoint(double X, double Y);

public sealed record PointOptions
{
    public bool Enabled
    {
        get; init;
    }
    // Boundary order is clockwise. A copied read-only list prevents edits behind the render worker.
    private IReadOnlyList<ScreenPoint> _vertices = Array.AsReadOnly<ScreenPoint>([new(0, 0), new(1, 0), new(1, 1), new(0, 1)]);
    public IReadOnlyList<ScreenPoint> Vertices
    {
        get => _vertices;
        init => _vertices = Array.AsReadOnly((value ?? throw new ArgumentException("Points cannot be null.")).ToArray());
    }
    // Extra video points clip the outline; these four boundary indices retain its keystone anchors.
    private IReadOnlyList<int>? _cornerIndices;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? MappingCornerIndices
    {
        get => _cornerIndices;
        init => _cornerIndices = value is null ? null : Array.AsReadOnly(value.ToArray());
    }
    private int CornerIndex(int corner) => _cornerIndices is null ? corner : _cornerIndices[corner];
    [JsonIgnore] public ScreenPoint TopLeft { get => _vertices[CornerIndex(0)]; init => Set(CornerIndex(0), value); }
    [JsonIgnore] public ScreenPoint TopRight { get => _vertices[CornerIndex(1)]; init => Set(CornerIndex(1), value); }
    [JsonIgnore] public ScreenPoint BottomRight { get => _vertices[CornerIndex(2)]; init => Set(CornerIndex(2), value); }
    [JsonIgnore] public ScreenPoint BottomLeft { get => _vertices[CornerIndex(3)]; init => Set(CornerIndex(3), value); }
    private void Set(int index, ScreenPoint point)
    {
        var copy = _vertices.ToArray();
        copy[index] = point;
        _vertices = Array.AsReadOnly(copy);
    }
    public bool Equals(PointOptions? other) => other is not null && Enabled == other.Enabled && Vertices.SequenceEqual(other.Vertices) &&
        (MappingCornerIndices is null ? other.MappingCornerIndices is null : other.MappingCornerIndices is not null && MappingCornerIndices.SequenceEqual(other.MappingCornerIndices));
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Enabled);
        foreach (var point in Vertices) hash.Add(point);
        if (MappingCornerIndices is not null) foreach (var index in MappingCornerIndices) hash.Add(index);
        return hash.ToHashCode();
    }

    public void Validate()
    {
        ValidatePolygon();
        if (MappingCornerIndices is null && Vertices.Count != 4 || MappingCornerIndices is not null &&
            (MappingCornerIndices.Count != 4 || MappingCornerIndices.Distinct().Count() != 4 || MappingCornerIndices.Any(i => i < 0 || i >= Vertices.Count)))
            throw new ArgumentException("Video outlines require four distinct keystone corner indices.");
        ScreenPoint[] points = [TopLeft, TopRight, BottomRight, BottomLeft];
        for (var i = 0; i < 4; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % 4];
            var c = points[(i + 2) % 4];
            if ((b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X) < 0.000001)
                throw new ArgumentException("The four video mapping corners must form a convex, clockwise quadrilateral with nonzero area.");
        }
        if (Vertices.Count > 4) VideoOutlineGeometry.ValidateProjection(this);
    }

    public void ValidatePolygon()
    {
        var points = Vertices;
        if (points.Count < 3 || points.Any(p => p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X is < 0 or > 1 || p.Y is < 0 or > 1))
            throw new ArgumentException("Screens require at least three finite points within the display (0–1).");
        double area = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            if (Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) < 1e-8)
                throw new ArgumentException("Adjacent points must be distinct.");
            area += a.X * b.Y - b.X * a.Y;
            for (int j = i + 1; j < points.Count; j++)
            {
                if (j == i + 1 || (i == 0 && j == points.Count - 1)) continue;
                if (PolygonGeometry.Intersects(a, b, points[j], points[(j + 1) % points.Count]))
                    throw new ArgumentException("Screen edges cannot cross or touch other edges.");
            }
        }
        if (area < 0.000001)
            throw new ArgumentException("Screen points must form a clockwise polygon with nonzero area.");
    }

    // Projective unit-square to quadrilateral mapping, rather than a bilinear warp.
    public ScreenPoint Map(double u, double v)
    {
        var dx1 = TopRight.X - BottomRight.X;
        var dx2 = BottomLeft.X - BottomRight.X;
        var dx3 = TopLeft.X - TopRight.X + BottomRight.X - BottomLeft.X;
        var dy1 = TopRight.Y - BottomRight.Y;
        var dy2 = BottomLeft.Y - BottomRight.Y;
        var dy3 = TopLeft.Y - TopRight.Y + BottomRight.Y - BottomLeft.Y;
        var determinant = dx1 * dy2 - dx2 * dy1;
        var g = (dx3 * dy2 - dx2 * dy3) / determinant;
        var h = (dx1 * dy3 - dx3 * dy1) / determinant;
        var divisor = g * u + h * v + 1;
        return new(
            ((TopRight.X - TopLeft.X + g * TopRight.X) * u +
             (BottomLeft.X - TopLeft.X + h * BottomLeft.X) * v + TopLeft.X) / divisor,
            ((TopRight.Y - TopLeft.Y + g * TopRight.Y) * u +
             (BottomLeft.Y - TopLeft.Y + h * BottomLeft.Y) * v + TopLeft.Y) / divisor);
    }
}
