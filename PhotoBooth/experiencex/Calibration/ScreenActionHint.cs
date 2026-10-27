using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ExperienceX;

internal static class ScreenActionHint
{
    private const string CommonScreen = "Hold Click + move: screen\nTab / Shift+Tab: next / previous item\nDouble-click inside: select points\nDouble-click edge: add point\nEnter: screen properties\nL: lock\nR: reset points\nCtrl+Z: undo movement\n[ / ]: bottom / top\nDelete: delete screen";
    private const string MediaMovement = "Hold Shift + move: move media\nHold Ctrl + move: zoom media\nHold Alt + move: rotate media";
    private const string Video = "↑ / ↓: media / folder\n←: parent folder\n→: enter folder\nP: perspective\nK: chroma key\n+ / −: brightness\nCtrl+R: reset video";
    private const string Mat = "M: normal / negative";
    // Shared groups always precede type-specific groups, including when wrapping rows.
    private static readonly (string Heading, string Body)[] VideoGroups = [("Screen", CommonScreen), ("Movement", MediaMovement), ("Video", Video)];
    private static readonly (string Heading, string Body)[] MatGroups = [("Screen", CommonScreen), ("Mat", Mat)];
    private static IReadOnlyList<(string Heading, string Body)> Groups(string type) => type switch
    {
        "video" => VideoGroups,
        "mat" => MatGroups,
        "locked" => [("Locked", "L: unlock\nTab / Shift+Tab: next / previous item")],
        _ => throw new ArgumentException("This screen type does not have an action HUD.", nameof(type))
    };
    internal static string TextFor(string type) => string.Join("\n", Groups(type).Select(group => group.Heading + "\n" + group.Body));
    internal static (byte[] Pixels, int Width, int Height) Create(int width, int height, string type = "video")
    {
        using var measure = new Bitmap(1,1);
        using var canvas = Graphics.FromImage(measure);
        using var font = new Font("Segoe UI", Math.Clamp(Math.Min(width,height)*.018f,10,22),FontStyle.Regular,GraphicsUnit.Pixel);
        using var headingFont = new Font(font, FontStyle.Bold);
        const int paddingX = 16, paddingY = 8, columnGap = 28, rowGap = 16, headingGap = 5;
        var availableWidth = Math.Max(1, width - 20 - paddingX * 2);
        var headingHeight = (int)Math.Ceiling(headingFont.GetHeight(canvas));
        var blocks = new List<(string Heading, string Body, int X, int Y, int Width, int Height)>();
        int x = 0, y = 0, rowHeight = 0, contentWidth = 0;
        foreach (var group in Groups(type))
        {
            var body = canvas.MeasureString(group.Body, font, availableWidth);
            var heading = canvas.MeasureString(group.Heading, headingFont, availableWidth);
            var blockWidth = Math.Min(availableWidth, (int)Math.Ceiling(Math.Max(body.Width, heading.Width)));
            var blockHeight = headingHeight + headingGap + (int)Math.Ceiling(body.Height);
            if (x > 0 && x + blockWidth > availableWidth) { x = 0; y += rowHeight + rowGap; rowHeight = 0; }
            blocks.Add((group.Heading, group.Body, x, y, blockWidth, blockHeight));
            contentWidth = Math.Max(contentWidth, x + blockWidth);
            x += blockWidth + columnGap;
            rowHeight = Math.Max(rowHeight, blockHeight);
        }
        var size = new Size(Math.Min(Math.Max(1,width-20),contentWidth+paddingX*2), y+rowHeight+paddingY*2);
        using var bitmap = new Bitmap(size.Width,size.Height,PixelFormat.Format32bppArgb);
        using(var graphics=Graphics.FromImage(bitmap))
        using(var background=new SolidBrush(Color.FromArgb(190,8,20,36)))
        using(var foreground=new SolidBrush(Color.FromArgb(235,140,210,255)))
        using(var headingBrush=new SolidBrush(Color.FromArgb(255,175,225,255)))
        using(var format=new StringFormat{Alignment=StringAlignment.Near,LineAlignment=StringAlignment.Near})
        {
            graphics.Clear(Color.Transparent);
            graphics.FillRectangle(background,0,0,size.Width,size.Height);
            graphics.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            foreach (var block in blocks)
            {
                graphics.DrawString(block.Heading,headingFont,headingBrush,new RectangleF(paddingX+block.X,paddingY+block.Y,block.Width,headingHeight+headingGap),format);
                graphics.DrawString(block.Body,font,foreground,new RectangleF(paddingX+block.X,paddingY+block.Y+headingHeight+headingGap,block.Width,block.Height-headingHeight-headingGap),format);
            }
        }
        var pixels=new byte[size.Width*size.Height*4];
        var data=bitmap.LockBits(new(0,0,size.Width,size.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try { for(int row=0;row<size.Height;row++)Marshal.Copy(data.Scan0+row*data.Stride,pixels,row*size.Width*4,size.Width*4); }
        finally { bitmap.UnlockBits(data); }
        for(int i=0;i<pixels.Length;i+=4)for(int c=0;c<3;c++)pixels[i+c]=(byte)((pixels[i+c]*pixels[i+3]+127)/255);
        return(pixels,size.Width,size.Height);
    }
}
