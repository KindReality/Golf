using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace Controller.Windows;

internal enum ToolbarGlyph { Power, Chevron, Minimize, Play, Return, Configure, Edit }

// Draw small vector glyphs at the current DPI rather than depending on an installed icon font.
internal sealed class ToolbarIconButton : Button
{
    private ToolbarGlyph _glyph;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal ToolbarGlyph Glyph
    {
        get => _glyph;
        set { if (_glyph != value) { _glyph = value; Invalidate(); } }
    }
    internal ToolbarIconButton(ToolbarGlyph glyph, string description)
    {
        _glyph = glyph; Text = ""; AccessibleName = description; AccessibleRole = AccessibleRole.PushButton;
        AutoSize = false; Size = new(42, 40); Margin = new(3, 2, 3, 2);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DarkTheme.Button(this); Padding = Padding.Empty; ForeColor = DarkTheme.Accent;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics; var state = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TranslateTransform(ClientSize.Width / 2f, ClientSize.Height / 2f);
            var scale = Math.Min(DeviceDpi / 96f, Math.Min(ClientSize.Width, ClientSize.Height) / 26f);
            graphics.ScaleTransform(scale, scale);
            var color = Enabled ? ForeColor : Color.FromArgb(92, 117, 132);
            using var pen = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            using var brush = new SolidBrush(color);
            switch (_glyph)
            {
                case ToolbarGlyph.Power:
                    graphics.DrawArc(pen, -8, -7, 16, 16, -50, 280);
                    graphics.DrawLine(pen, 0, -10, 0, -1); break;
                case ToolbarGlyph.Chevron:
                    graphics.DrawLines(pen, [new PointF(-4, -2), new(0, 2), new(4, -2)]); break;
                case ToolbarGlyph.Minimize:
                    graphics.DrawLine(pen, -7, 3, 7, 3); break;
                case ToolbarGlyph.Play:
                    graphics.FillPolygon(brush, [new PointF(-5, -9), new(9, 0), new(-5, 9)]); break;
                case ToolbarGlyph.Edit:
                    graphics.DrawPolygon(pen, [new PointF(-8, 8), new(-6, 1), new(5, -10), new(10, -5), new(-1, 6)]);
                    graphics.DrawLine(pen, 3, -8, 8, -3);
                    graphics.DrawLine(pen, -6, 1, -1, 6); break;
                case ToolbarGlyph.Return:
                    using (var path = new GraphicsPath())
                    {
                        path.AddLine(8, 7, 8, 0); path.AddBezier(8, 0, 8, -6, 4, -6, -1, -6); path.AddLine(-1, -6, -8, -6);
                        graphics.DrawPath(pen, path);
                    }
                    graphics.DrawLines(pen, [new PointF(-3, -11), new(-8, -6), new(-3, -1)]); break;
                case ToolbarGlyph.Configure:
                    for (var i = 0; i < 3; i++)
                    {
                        var x = -7 + 7 * i; var y = i == 1 ? 4 : -3;
                        graphics.DrawLine(pen, x, -9, x, y - 3); graphics.DrawLine(pen, x, y + 3, x, 9);
                        graphics.DrawEllipse(pen, x - 2.5f, y - 2.5f, 5, 5);
                    }
                    break;
            }
        }
        finally { graphics.Restore(state); }
    }
}
