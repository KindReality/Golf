using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ExperienceX;

internal static class ScreenNameLabel
{
    public static PointF Center(PointOptions k, int width, int height) => new(
        (float)(k.Vertices.Average(p => p.X) * (width - 1)),
        (float)(k.Vertices.Average(p => p.Y) * (height - 1)));
    public static float FontSize(int width, int height) => Math.Clamp(Math.Min(width, height) * .04f, 20, 48);
    public static Size Measure(string text, int width, int height)
    {
        using var image = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(image);
        using var font = new Font("Segoe UI", FontSize(width, height), FontStyle.Regular, GraphicsUnit.Pixel);
        var size = graphics.MeasureString(text, font);
        return new(Math.Min(Math.Max(1, width), (int)Math.Ceiling(size.Width) + 12), (int)Math.Ceiling(size.Height) + 8);
    }
    public static Rectangle Bounds(string text, PointOptions k, int width, int height)
    {
        var size = Measure(text, width, height);
        var center = Center(k, width, height);
        return new((int)(center.X - size.Width / 2f), (int)(center.Y - size.Height / 2f), size.Width, size.Height);
    }
    public static (byte[] Pixels, int Width, int Height, int OffsetY) Create(string text, int width, int height, string? subtitle=null)
    {
        var nameSize = Measure(text, width, height);
        var hasSubtitle=!string.IsNullOrEmpty(subtitle);
        var extra=hasSubtitle ? (int)Math.Ceiling(FontSize(width,height)*1.1f)+6 : 0;
        var size = new Size(hasSubtitle ? Math.Max(nameSize.Width,Math.Min(width-20,Math.Max(200,subtitle!.Length*(int)(FontSize(width,height)*.5f)))) : nameSize.Width,nameSize.Height+extra);
        using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Segoe UI", FontSize(width, height), FontStyle.Regular, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(Color.FromArgb(240, 255, 20, 36)))
        using (var subtitleFont=new Font("Segoe UI",FontSize(width,height)*.72f,FontStyle.Regular,GraphicsUnit.Pixel))
        using (var subtitleBrush=new SolidBrush(Color.FromArgb(240,255,20,36)))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            graphics.Clear(Color.Transparent);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString(text, font, brush, new RectangleF(0, 0, size.Width, nameSize.Height), format);
            if(hasSubtitle)
            {
                format.Trimming=StringTrimming.EllipsisPath;format.FormatFlags=StringFormatFlags.NoWrap;
                graphics.DrawString(subtitle,subtitleFont,subtitleBrush,new RectangleF(0,nameSize.Height,size.Width,extra),format);
            }
        }
        var pixels = new byte[size.Width * size.Height * 4];
        var data = bitmap.LockBits(new(0, 0, size.Width, size.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < size.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * size.Width * 4, size.Width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        for (int i = 0; i < pixels.Length; i += 4)
            for (int c = 0; c < 3; c++)
                pixels[i + c] = (byte)((pixels[i + c] * pixels[i + 3] + 127) / 255);
        return (pixels, size.Width, size.Height,extra/2);
    }
}
