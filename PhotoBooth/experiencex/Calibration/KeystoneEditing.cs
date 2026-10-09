namespace ExperienceX;

internal static class KeystoneEditing
{
    // Interaction order deliberately differs from polygon winding: UL, UR, BL, BR.
    public static KeystonePoint[] Points(KeystoneOptions k) => [k.TopLeft, k.TopRight, k.BottomLeft, k.BottomRight];
    public static double HitRadius(int width, int height, double diameterPercent) =>
        Math.Sqrt((double)width * width + (double)height * height) * diameterPercent / 200;
    public static int[] Hits(KeystoneOptions k, double x, double y, int width, int height, double percent)
    {
        var radius = HitRadius(width, height, percent);
        return Points(k).Select((p, i) => (p, i)).Where(t =>
            Math.Pow((t.p.X - x) * Math.Max(1, width - 1), 2) + Math.Pow((t.p.Y - y) * Math.Max(1, height - 1), 2) <= radius * radius)
            .Select(t => t.i).ToArray();
    }
    private static KeystoneOptions Set(KeystoneOptions k, int index, KeystonePoint p) => index switch
    {
        0 => k with { TopLeft = p },
        1 => k with { TopRight = p },
        2 => k with { BottomLeft = p },
        3 => k with { BottomRight = p },
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
    public static KeystoneOptions Move(KeystoneOptions k, int index, double x, double y)
    {
        var original = Points(k)[index];
        var target = new KeystonePoint(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
        bool Valid(KeystoneOptions test)
        {
            try
            {
                test.Validate();
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
}

internal sealed class KeystoneHitCycle
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
