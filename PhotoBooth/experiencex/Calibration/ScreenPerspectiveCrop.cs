namespace ExperienceX;

internal static class ScreenPerspectiveCrop
{
    public static (double WidthPercent, double HeightPercent) Calculate(PointOptions points, int displayWidth, int displayHeight)
    {
        if (displayWidth <= 0 || displayHeight <= 0) throw new ArgumentException("The display dimensions must be positive.");
        points.Validate();
        var effective = points.Enabled ? points : new PointOptions();
        double Edge(ScreenPoint a, ScreenPoint b) => Math.Sqrt(Math.Pow((a.X-b.X)*displayWidth,2)+Math.Pow((a.Y-b.Y)*displayHeight,2));
        var horizontal = (Edge(effective.TopLeft,effective.TopRight)+Edge(effective.BottomLeft,effective.BottomRight))/2;
        var vertical = (Edge(effective.TopLeft,effective.BottomLeft)+Edge(effective.TopRight,effective.BottomRight))/2;
        var relativeAspect = horizontal / vertical / ((double)displayWidth/displayHeight);
        return (100 * Math.Min(1,relativeAspect), 100 * Math.Min(1,1/relativeAspect));
    }
}
