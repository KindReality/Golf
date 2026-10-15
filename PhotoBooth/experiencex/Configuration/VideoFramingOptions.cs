namespace ExperienceX;

// Coordinates are relative to the screen's selected perspective, not the complete media.
public sealed record VideoFramingOptions
{
    public double Zoom { get; init; } = 1;
    public double CenterX { get; init; } = .5;
    public double CenterY { get; init; } = .5;
    public double RotationDegrees { get; init; }
    public const double MaximumZoom = 100;

    public void Validate()
    {
        var half = .5 / Zoom;
        if (!double.IsFinite(Zoom) || Zoom < 1 || Zoom > MaximumZoom ||
            !double.IsFinite(CenterX) || !double.IsFinite(CenterY) || !double.IsFinite(RotationDegrees) ||
            CenterX < half - 1e-10 || CenterX > 1 - half + 1e-10 ||
            CenterY < half - 1e-10 || CenterY > 1 - half + 1e-10)
            throw new ArgumentException("Video framing requires zoom from 1 to 100 and a center that keeps the view inside its perspective.");
    }
    internal static VideoFramingOptions Bounded(double zoom, double x, double y, double rotationDegrees = 0)
    {
        zoom = Math.Clamp(zoom, 1, MaximumZoom);
        var half = .5 / zoom;
        return new() { Zoom = zoom, CenterX = Math.Clamp(x, half, 1 - half), CenterY = Math.Clamp(y, half, 1 - half), RotationDegrees = rotationDegrees };
    }
    internal VideoFramingOptions ZoomAt(ScreenPoint pointer, double wheelNotches, double aspect = 1)
    {
        var zoom = Math.Clamp(Zoom * Math.Pow(1.1, wheelNotches), 1, MaximumZoom);
        var offset = SourceOffset(pointer.X - .5, pointer.Y - .5, aspect);
        return Bounded(zoom, CenterX + offset.X * (1 / Zoom - 1 / zoom),
            CenterY + offset.Y * (1 / Zoom - 1 / zoom), RotationDegrees);
    }
    internal VideoFramingOptions Pan(ScreenPoint start, ScreenPoint pointer, double aspect = 1)
    {
        var offset = SourceOffset(pointer.X - start.X, pointer.Y - start.Y, aspect);
        return Bounded(Zoom, CenterX - offset.X / Zoom, CenterY - offset.Y / Zoom, RotationDegrees);
    }
    internal VideoFramingOptions Rotate(double degrees) => this with
    {
        RotationDegrees = NormalizeAngle(RotationDegrees + degrees)
    };
    private static double NormalizeAngle(double degrees) => ((degrees % 360 + 540) % 360) - 180;
    // Inverse rotation in the screen's pixel proportions; screen geometry stays fixed.
    internal System.Numerics.Vector4 SamplingRotation(double aspect = 1)
    {
        var radians = NormalizeAngle(RotationDegrees) * Math.PI / 180;
        var c = Math.Cos(radians); var s = Math.Sin(radians);
        return new((float)c, (float)(s / aspect), (float)(-s * aspect), (float)c);
    }
    private ScreenPoint SourceOffset(double x, double y, double aspect)
    {
        var radians = NormalizeAngle(RotationDegrees) * Math.PI / 180;
        var c = Math.Cos(radians); var s = Math.Sin(radians);
        return new(c * x + s * y / aspect, -s * x * aspect + c * y);
    }
    internal static double ScreenAspect(PointOptions points, double width, double height)
        => VideoOutlineGeometry.Aspect(points, width, height);
    internal static double QuadAspect(PointOptions points, double width, double height)
    {
        if (!points.Enabled) return width / height;
        double Distance(ScreenPoint a, ScreenPoint b) => Math.Sqrt(Math.Pow((b.X - a.X) * width, 2) + Math.Pow((b.Y - a.Y) * height, 2));
        return Math.Clamp((Distance(points.TopLeft, points.TopRight) + Distance(points.BottomLeft, points.BottomRight)) /
            Math.Max(1e-6, Distance(points.TopLeft, points.BottomLeft) + Distance(points.TopRight, points.BottomRight)), 1e-6, 1e6);
    }

    internal PhotoBooth.Networking.PerspectiveRectangle Crop(PhotoBooth.Networking.PerspectiveRectangle? perspective)
    {
        var source = perspective ?? new();
        return new()
        {
            X = source.X + (CenterX - .5 / Zoom) * source.Width,
            Y = source.Y + (CenterY - .5 / Zoom) * source.Height,
            Width = source.Width / Zoom, Height = source.Height / Zoom
        };
    }
    internal static bool TryPointer(PointOptions points, double x, double y, out ScreenPoint pointer)
    {
        var (a, b, c) = VideoTransform.Inverse(points);
        var divisor = c.X * x + c.Y * y + c.Z;
        pointer = new((a.X * x + a.Y * y + a.Z) / divisor, (b.X * x + b.Y * y + b.Z) / divisor);
        return Math.Abs(divisor) > 1e-6 && double.IsFinite(pointer.X) && double.IsFinite(pointer.Y);
    }
}
