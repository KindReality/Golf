using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ExperienceX;

internal static class CalibrationLabel
{
    public static string Text(ExperienceOptions options, string monitor, KeystoneOptions k, int width, int height)
    {
        var aliases = options.MonitorAliases.Where(p => string.Equals(p.Value.Trim(), monitor, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key);
        var heading = string.Join(" / ", aliases);
        var lines = new List<string> { string.IsNullOrEmpty(heading) ? monitor : $"{heading}   [{monitor}]", $"{width} x {height}   |   Correction {(k.Enabled ? "on" : "off")}" };
        string[] names = ["1 UL", "2 UR", "3 BL", "4 BR"];
        var points = KeystoneEditing.Points(k);
        for (int i = 0; i < 4; i++)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{names[i]}   X {points[i].X:0.0000}   Y {points[i].Y:0.0000}   ({points[i].X * (width - 1):0}, {points[i].Y * (height - 1):0} px)"));
        lines.Add("Tab: select   Arrows: 1px   Shift: 10px");
        lines.Add("Enter: save   Esc: cancel   Z: undo   R: reset");
        lines.Add("C: exit configuration   T: background   X: close");
        return string.Join('\n', lines);
    }
    public static (byte[] Pixels, int Width, int Height) Create(string text)
    {
        const int width = 490, height = 192;
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Point))
        using (var background = new SolidBrush(Color.FromArgb(150, 8, 15, 24)))
        using (var brush = new SolidBrush(Color.FromArgb(235, 210, 238, 255)))
        {
            graphics.Clear(Color.Transparent);
            graphics.FillRectangle(background, 0, 0, width, height);
            graphics.DrawString(text, font, brush, new RectangleF(10, 8, width - 20, height - 16));
        }
        var pixels = new byte[width * height * 4];
        var data = bitmap.LockBits(new(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * width * 4, width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        for (int i = 0; i < pixels.Length; i += 4)
            for (int channel = 0; channel < 3; channel++)
                pixels[i + channel] = (byte)((pixels[i + channel] * pixels[i + 3] + 127) / 255);
        return (pixels, width, height);
    }
}
