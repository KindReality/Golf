using System.Numerics;
namespace ExperienceX;

internal static class VideoTransform
{
    // Invert the projective mapping: the pixel shader maps output pixels back into video UVs.
    public static (Vector4, Vector4, Vector4) Inverse(KeystoneOptions k)
    {
        if (!k.Enabled)
            return (new(1, 0, 0, 0), new(0, 1, 0, 0), new(0, 0, 1, 0));
        var a = k.TopLeft;
        var b = k.TopRight;
        var c = k.BottomRight;
        var d = k.BottomLeft;
        double dx1 = b.X - c.X, dx2 = d.X - c.X, dx3 = a.X - b.X + c.X - d.X;
        double dy1 = b.Y - c.Y, dy2 = d.Y - c.Y, dy3 = a.Y - b.Y + c.Y - d.Y;
        double det = dx1 * dy2 - dx2 * dy1, g = (dx3 * dy2 - dx2 * dy3) / det, h = (dx1 * dy3 - dx3 * dy1) / det;
        var m = new Matrix4x4((float)(b.X - a.X + g * b.X), (float)(d.X - a.X + h * d.X), (float)a.X, 0,
            (float)(b.Y - a.Y + g * b.Y), (float)(d.Y - a.Y + h * d.Y), (float)a.Y, 0, (float)g, (float)h, 1, 0, 0, 0, 0, 1);
        if (!Matrix4x4.Invert(m, out var inv))
            throw new ArgumentException("Keystone transform is singular.");
        return (new(inv.M11, inv.M12, inv.M13, 0), new(inv.M21, inv.M22, inv.M23, 0), new(inv.M31, inv.M32, inv.M33, 0));
    }
}
