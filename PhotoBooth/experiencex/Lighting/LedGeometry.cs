namespace ExperienceX;

internal static class LedGeometry
{
    internal static void Validate(PointOptions points, bool array)
    {
        if (array)
        {
            if (points.Vertices.Count != 2 || points.Vertices.Any(p => p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X is < 0 or > 1 || p.Y is < 0 or > 1) || points.Vertices[0] == points.Vertices[1])
                throw new ArgumentException("An LED array needs two distinct points within its display.");
        }
        else
        {
            points.Validate();
            var p = points.Vertices;
            if (Math.Abs(p[0].X + p[2].X - p[1].X - p[3].X) > 1e-6 || Math.Abs(p[0].Y + p[2].Y - p[1].Y - p[3].Y) > 1e-6)
                throw new ArgumentException("LED matrices require a rectangle, not independent point distortion.");
        }
    }
    internal static PointOptions Initial(bool array) => array ? new()
    {
        Enabled = true, Vertices = [new(0, .5), new(1, .5)]
    } : new() { Enabled = true };

    internal static PointOptions Band(PointOptions line, double thickness, int width, int height)
    {
        var a = Pixel(line.Vertices[0], width, height); var b = Pixel(line.Vertices[1], width, height);
        var dx = b.X - a.X; var dy = b.Y - a.Y; var length = Math.Sqrt(dx * dx + dy * dy);
        double half = Math.Max(width, height) * thickness / 200;
        double nx = -dy / Math.Max(length, 1e-9) * half, ny = dx / Math.Max(length, 1e-9) * half;
        return new() { Enabled = true, Vertices = [Normalized(a.X - nx, a.Y - ny, width, height), Normalized(b.X - nx, b.Y - ny, width, height), Normalized(b.X + nx, b.Y + ny, width, height), Normalized(a.X + nx, a.Y + ny, width, height)] };
    }
    internal static ScreenPoint RotationHandle(PointOptions points, int width, int height)
    {
        var a = Pixel(points.TopLeft, width, height); var b = Pixel(points.TopRight, width, height);
        double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
        double x = (a.X + b.X) / 2 + dy / length * 35, y = (a.Y + b.Y) / 2 - dx / length * 35;
        if (x < 12 || x > width - 13 || y < 12 || y > height - 13)
        { x = (a.X + b.X) / 2 - dy / length * 35; y = (a.Y + b.Y) / 2 + dx / length * 35; }
        return Normalized(Math.Clamp(x, 10, width - 11), Math.Clamp(y, 10, height - 11), width, height);
    }
    internal static PointOptions Resize(PointOptions original, int index, double x, double y, int width, int height)
    {
        int boundary = PointEditing.BoundaryIndex(original, index), opposite = (boundary + 2) % 4;
        var p = original.Vertices.Select(v => Pixel(v, width, height)).ToArray();
        var u = new ScreenPoint(p[1].X - p[0].X, p[1].Y - p[0].Y); double ul = Math.Sqrt(u.X * u.X + u.Y * u.Y);
        u = new(u.X / ul, u.Y / ul); var v = new ScreenPoint(-u.Y, u.X);
        var anchor = p[opposite]; var target = Pixel(new(x, y), width, height);
        double signU = boundary is 0 or 3 ? -1 : 1, signV = boundary is 0 or 1 ? -1 : 1;
        double w = Math.Max(2, ((target.X - anchor.X) * u.X + (target.Y - anchor.Y) * u.Y) * signU);
        double h = Math.Max(2, ((target.X - anchor.X) * v.X + (target.Y - anchor.Y) * v.Y) * signV);
        var tl = new ScreenPoint(anchor.X + (signU < 0 ? -w * u.X : 0) + (signV < 0 ? -h * v.X : 0), anchor.Y + (signU < 0 ? -w * u.Y : 0) + (signV < 0 ? -h * v.Y : 0));
        var next = original with { Enabled = true, Vertices = [Normalized(tl.X,tl.Y,width,height), Normalized(tl.X+w*u.X,tl.Y+w*u.Y,width,height), Normalized(tl.X+w*u.X+h*v.X,tl.Y+w*u.Y+h*v.Y,width,height), Normalized(tl.X+h*v.X,tl.Y+h*v.Y,width,height)] };
        // Shared-point membership relies on exact persisted coordinates, not roundoff-equivalent values.
        if(Math.Abs(next.Vertices[boundary].X-x)+Math.Abs(next.Vertices[boundary].Y-y)<1e-9)
        {
            var exact=next.Vertices.ToArray();exact[boundary]=new(x,y);next=next with{Vertices=exact};
        }
        return Inside(next) ? next : original;
    }
    internal static PointOptions Rotate(PointOptions original, double startX, double startY, double x, double y, int width, int height)
    {
        var center = new ScreenPoint(original.Vertices.Average(p => p.X) * (width-1), original.Vertices.Average(p => p.Y) * (height-1));
        var start = Pixel(new(startX,startY),width,height); var end = Pixel(new(x,y),width,height);
        double angle = Math.Atan2(end.Y-center.Y,end.X-center.X)-Math.Atan2(start.Y-center.Y,start.X-center.X), c=Math.Cos(angle), s=Math.Sin(angle);
        var result = original with { Enabled = true, Vertices = original.Vertices.Select(p => { var q=Pixel(p,width,height); double dx=q.X-center.X,dy=q.Y-center.Y; return Normalized(center.X+c*dx-s*dy,center.Y+s*dx+c*dy,width,height); }).ToArray() };
        return Inside(result) ? result : original;
    }
    private static bool Inside(PointOptions points) => points.Vertices.All(p => p.X is >= 0 and <= 1 && p.Y is >= 0 and <= 1);
    internal static ScreenPoint Pixel(ScreenPoint p, int width, int height) => new(p.X * (width-1), p.Y * (height-1));
    private static ScreenPoint Normalized(double x,double y,int width,int height) => new(x/Math.Max(1,width-1),y/Math.Max(1,height-1));
    internal static bool Contains(ScreenOptions screen,double x,double y,int width,int height) => ScreenSelection.Contains(screen.IsLedArray ? Band(screen.Points,screen.Perspective!.Led!.SamplingThicknessPercent,width,height) : screen.Points,x,y);
}
