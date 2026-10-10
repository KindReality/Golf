namespace ExperienceX;

internal static class PointEditing
{
    // Interaction order deliberately differs from polygon winding: UL, UR, BL, BR.
    public static ScreenPoint[] Points(PointOptions k) => k.Vertices.Count == 4 ? [k.TopLeft, k.TopRight, k.BottomLeft, k.BottomRight] : k.Vertices.ToArray();
    public static int BoundaryIndex(PointOptions k, int index) => k.Vertices.Count == 4 ? (index == 2 ? 3 : index == 3 ? 2 : index) : index;
    public static double HitRadius(int width, int height, double diameterPercent) =>
        Math.Sqrt((double)width * width + (double)height * height) * diameterPercent / 200;
    public static int[] Hits(PointOptions k, double x, double y, int width, int height, double percent)
    {
        var radius = HitRadius(width, height, percent);
        return Points(k).Select((p, i) => (p, i)).Where(t =>
            Math.Pow((t.p.X - x) * Math.Max(1, width - 1), 2) + Math.Pow((t.p.Y - y) * Math.Max(1, height - 1), 2) <= radius * radius)
            .Select(t => t.i).ToArray();
    }
    private static PointOptions Set(PointOptions k, int index, ScreenPoint p)
    {
        var points = k.Vertices.ToArray();
        points[BoundaryIndex(k, index)] = p;
        return k with { Vertices = points };
    }
    public static PointOptions Move(PointOptions k, int index, double x, double y, bool polygon = false)
    {
        var original = Points(k)[index];
        var target = new ScreenPoint(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
        bool Valid(PointOptions test)
        {
            try
            {
                if (polygon) test.ValidatePolygon(); else test.Validate();
                return true;
            }
            catch (ArgumentException) { return false; }
        }
        var candidate = Set(k, index, target);
        if (!Valid(candidate))
        {
            double low = 0, high = 1;
            for (int step = 0; step < 40; step++)
            {
                var t = (low + high) / 2;
                var trial = Set(k, index, new(original.X + (target.X - original.X) * t, original.Y + (target.Y - original.Y) * t));
                if (Valid(trial))
                    low = t;
                else
                    high = t;
            }
            candidate = Set(k, index, new(original.X + (target.X - original.X) * low, original.Y + (target.Y - original.Y) * low));
        }
        return candidate == k ? k : candidate with
        {
            Enabled = true
        };
    }
    public static PointOptions Translate(PointOptions k, double dx, double dy)
    {
        var points = Points(k);
        dx = Math.Clamp(dx, -points.Min(p => p.X), 1 - points.Max(p => p.X));
        dy = Math.Clamp(dy, -points.Min(p => p.Y), 1 - points.Max(p => p.Y));
        if (dx == 0 && dy == 0)
            return k;
        ScreenPoint Shift(ScreenPoint point) => new(point.X + dx, point.Y + dy);
        return k with
        {
            Enabled = true,
            Vertices = k.Vertices.Select(Shift).ToArray()
        };
    }
}

internal sealed class PointHitCycle
{
    private int[] _last = [];
    private int _selected = -1;
    public int Pick(int[] candidates)
    {
        if (candidates.Length == 0)
        {
            _last = [];
            return _selected = -1;
        }
        var position = candidates.SequenceEqual(_last) ? Array.IndexOf(candidates, _selected) : -1;
        _selected = candidates[(position + 1) % candidates.Length];
        _last = candidates;
        return _selected;
    }
}
