using System.Numerics;

namespace ExperienceX;

internal static class VideoOutlineGeometry
{
    internal static void ValidateProjection(PointOptions shape)
    {
        var inverse = VideoTransform.InverseQuad(shape);
        // A projective horizon cannot cross the outline; otherwise expansion could become infinite.
        double Divisor(ScreenPoint p) => inverse.Item3.X*p.X + inverse.Item3.Y*p.Y + inverse.Item3.Z;
        var sign = Math.Sign(Divisor(shape.TopLeft));
        if (shape.Vertices.Any(p => Divisor(p)*sign < 1e-6))
            throw new ArgumentException("The video outline cannot cross its keystone mapping's horizon.");
    }
    internal static (double X, double Y, double Width, double Height) Extent(PointOptions shape, (Vector4, Vector4, Vector4) inverse)
    {
        if (shape.Vertices.Count == 4 || !shape.Enabled) return (0,0,1,1);
        var (a,b,c) = inverse;
        var points = shape.Vertices.Select(p =>
        {
            var w=c.X*p.X+c.Y*p.Y+c.Z;
            return new ScreenPoint((a.X*p.X+a.Y*p.Y+a.Z)/w,(b.X*p.X+b.Y*p.Y+b.Z)/w);
        }).ToArray();
        var x=Math.Min(0,points.Min(p=>p.X)); var y=Math.Min(0,points.Min(p=>p.Y));
        return (x,y,Math.Max(1,points.Max(p=>p.X))-x,Math.Max(1,points.Max(p=>p.Y))-y);
    }
    // Average physical edge proportions, expanded in the keystone plane to cover the full outline.
    internal static double Aspect(PointOptions shape, double width, double height)
    {
        var aspect = VideoFramingOptions.QuadAspect(shape,width,height);
        if (shape.Vertices.Count == 4) return aspect;
        var extent = Extent(shape,VideoTransform.InverseQuad(shape));
        return aspect*extent.Width/extent.Height;
    }
    internal static VideoFramingOptions Cover(PointOptions shape, VideoFramingOptions framing, double aspect)
    {
        if (shape.Vertices.Count == 4) return framing;
        var (a,b,c)=VideoTransform.Inverse(shape);
        var rotation=framing.SamplingRotation(aspect);
        var zoom=framing.Zoom;
        foreach(var p in shape.Vertices)
        {
            var w=c.X*p.X+c.Y*p.Y+c.Z;
            var x=(a.X*p.X+a.Y*p.Y+a.Z)/w-.5;
            var y=(b.X*p.X+b.Y*p.Y+b.Z)/w-.5;
            var u=rotation.X*x+rotation.Y*y; var v=rotation.Z*x+rotation.W*y;
            zoom=Math.Max(zoom,Math.Max(u<0?-u/framing.CenterX:u/(1-framing.CenterX),v<0?-v/framing.CenterY:v/(1-framing.CenterY)));
        }
        return framing with { Zoom=zoom };
    }
}
