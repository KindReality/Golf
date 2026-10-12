using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace ExperienceX;

internal sealed record StillImagePixels(int Width, int Height, byte[] Bgra)
{
    public static StillImagePixels Read(string path)
    {
        MediaPath.EnsureLocalPath(path);
        // Release the file after decoding, so media synchronization can replace it.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var source = Image.FromStream(stream);
        if (source.Width > 16384 || source.Height > 16384 || (long)source.Width * source.Height > 64_000_000)
            throw new NotSupportedException("Image exceeds the supported texture size.");
        // Premultiply before GPU filtering so transparent image edges do not develop dark halos.
        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        }
        var pixels = new byte[checked(bitmap.Width * bitmap.Height * 4)];
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (int y = 0; y < bitmap.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * bitmap.Width * 4, bitmap.Width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        return new(bitmap.Width, bitmap.Height, pixels);
    }
}
