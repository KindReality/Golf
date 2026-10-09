namespace ExperienceX;

public sealed record KeystonePoint(double X, double Y);

public sealed record KeystoneOptions
{
    public bool Enabled
    {
        get; init;
    }
    public KeystonePoint TopLeft { get; init; } = new(0, 0);
    public KeystonePoint TopRight { get; init; } = new(1, 0);
    public KeystonePoint BottomRight { get; init; } = new(1, 1);
    public KeystonePoint BottomLeft { get; init; } = new(0, 1);

    public void Validate()
    {
        KeystonePoint[] points = [TopLeft, TopRight, BottomRight, BottomLeft];
        if (points.Any(p => p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y) ||
            p.X is < 0 or > 1 || p.Y is < 0 or > 1))
            throw new ArgumentException("Keystone corners must have finite X and Y values between 0 and 1.");
        for (var i = 0; i < 4; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % 4];
            var c = points[(i + 2) % 4];
            if ((b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X) < 0.000001)
                throw new ArgumentException("Keystone corners must form a convex, clockwise quadrilateral with nonzero area.");
        }
    }

    // Projective unit-square to quadrilateral mapping, rather than a bilinear warp.
    public KeystonePoint Map(double u, double v)
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
