using System.Numerics;

namespace ExperienceX;

internal static class VideoSizing
{
    public static Vector4 ContentScale(string mode, int videoWidth, int videoHeight, int targetWidth, int targetHeight, PointOptions? points = null)
    {
        if (mode == "stretch") return new(1, 1, 0, 0);
        var videoAspect = (float)videoWidth / videoHeight;
        var targetAspect = (float)targetWidth / targetHeight;
        if (points?.Enabled == true)
        {
            // Use the selected screen's physical proportions, including rotated screens.
            // A keystone quadrilateral uses the average lengths of its opposite edges.
            float Edge(ScreenPoint a, ScreenPoint b) => Vector2.Distance(new((float)a.X * targetWidth, (float)a.Y * targetHeight),
                new((float)b.X * targetWidth, (float)b.Y * targetHeight));
            targetAspect = (Edge(points.TopLeft, points.TopRight) + Edge(points.BottomLeft, points.BottomRight)) /
                (Edge(points.TopLeft, points.BottomLeft) + Edge(points.TopRight, points.BottomRight));
        }
        return new(Math.Min(1, videoAspect / targetAspect), Math.Min(1, targetAspect / videoAspect), 0, 0);
    }
}
