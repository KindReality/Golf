namespace ExperienceX;

internal static class ScreenPerspectiveCrop
{
    public static (double WidthPercent, double HeightPercent) Calculate(PointOptions points, int displayWidth, int displayHeight)
    {
        if (displayWidth <= 0 || displayHeight <= 0) throw new ArgumentException("The display dimensions must be positive.");
        points.Validate();
        var effective = points.Enabled ? points : new PointOptions();
        var relativeAspect = VideoFramingOptions.ScreenAspect(effective,displayWidth,displayHeight) / ((double)displayWidth/displayHeight);
        return (100 * Math.Min(1,relativeAspect), 100 * Math.Min(1,1/relativeAspect));
    }
}
