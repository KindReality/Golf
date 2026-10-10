namespace ExperienceX;

internal static class PolygonGeometry
{
    public static double Cross(ScreenPoint a, ScreenPoint b, ScreenPoint c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    public static bool Intersects(ScreenPoint a, ScreenPoint b, ScreenPoint c, ScreenPoint d)
    {
        if (Math.Max(a.X, b.X) < Math.Min(c.X, d.X) - 1e-10 || Math.Max(c.X, d.X) < Math.Min(a.X, b.X) - 1e-10 ||
            Math.Max(a.Y, b.Y) < Math.Min(c.Y, d.Y) - 1e-10 || Math.Max(c.Y, d.Y) < Math.Min(a.Y, b.Y) - 1e-10) return false;
        return Cross(a, b, c) * Cross(a, b, d) <= 1e-12 && Cross(c, d, a) * Cross(c, d, b) <= 1e-12;
    }
    public static (int Edge, ScreenPoint Point, double Distance) ClosestEdge(PointOptions shape, double x, double y, int width, int height)
    {
        double best = double.PositiveInfinity;
        int edge = -1;
        var point = new ScreenPoint(x, y);
        for (int i = 0; i < shape.Vertices.Count; i++)
        {
            var a = shape.Vertices[i]; var b = shape.Vertices[(i + 1) % shape.Vertices.Count];
            double dx = (b.X - a.X) * Math.Max(1, width - 1), dy = (b.Y - a.Y) * Math.Max(1, height - 1);
            var t = Math.Clamp(((x - a.X) * Math.Max(1, width - 1) * dx + (y - a.Y) * Math.Max(1, height - 1) * dy) / Math.Max(1e-12, dx * dx + dy * dy), 0, 1);
            var projected = new ScreenPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
            double distance = Math.Sqrt(Math.Pow((x - projected.X) * Math.Max(1, width - 1), 2) + Math.Pow((y - projected.Y) * Math.Max(1, height - 1), 2));
            if (distance < best) { best = distance; edge = i; point = projected; }
        }
        return (edge, point, best);
    }
    // Ear clipping runs only when points change. Rendering uses cached GPU triangles.
    public static ScreenPoint[] Triangles(PointOptions shape)
    {
        shape.ValidatePolygon();
        var vertices = shape.Vertices;
        var remaining = Enumerable.Range(0, vertices.Count).ToList();
        var triangles = new List<ScreenPoint>();
        // Points inserted on straight edges must stay editable, but are redundant for triangulation.
        for (int i = remaining.Count - 1; i >= 0 && remaining.Count > 3; i--)
            if (Math.Abs(Cross(vertices[remaining[(i + remaining.Count - 1) % remaining.Count]], vertices[remaining[i]], vertices[remaining[(i + 1) % remaining.Count]])) < 1e-12)
                remaining.RemoveAt(i);
        while (remaining.Count > 3)
        {
            bool clipped = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                var a = vertices[remaining[(i + remaining.Count - 1) % remaining.Count]];
                var b = vertices[remaining[i]]; var c = vertices[remaining[(i + 1) % remaining.Count]];
                if (Cross(a, b, c) <= 1e-12) continue;
                if (remaining.Any(j => vertices[j] != a && vertices[j] != b && vertices[j] != c &&
                    Cross(a, b, vertices[j]) >= -1e-12 && Cross(b, c, vertices[j]) >= -1e-12 && Cross(c, a, vertices[j]) >= -1e-12)) continue;
                triangles.AddRange([a, b, c]); remaining.RemoveAt(i); clipped = true; break;
            }
            if (!clipped) throw new ArgumentException("The screen polygon could not be triangulated.");
        }
        triangles.AddRange(remaining.Select(i => vertices[i]));
        return triangles.ToArray();
    }
}
