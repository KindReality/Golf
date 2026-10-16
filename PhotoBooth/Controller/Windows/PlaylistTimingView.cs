using Controller.Configuration;
using System.Drawing.Drawing2D;

namespace Controller.Windows;

// A dispatch overview, not a media-duration timeline: nested commands can delay later items.
internal sealed class PlaylistTimingView : Control
{
    private PlaylistOptions? _playlist;
    private int _selected;
    private readonly List<(Rectangle Bounds, int Index)> _hits = [];
    internal Action<int>? Selected;
    internal PlaylistTimingView() { DoubleBuffered = true; Dock = DockStyle.Fill; BackColor = DarkTheme.Background; AccessibleName = "Playlist timing overview"; }
    internal void Set(PlaylistOptions playlist, int selected) { _playlist = playlist; _selected = selected; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); _hits.Clear(); if (_playlist is null || Width < 100 || Height < 40) return;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = new Font("Segoe UI", 8);
        using var accent = new Pen(Color.FromArgb(110, DarkTheme.Accent));
        using var fill = new SolidBrush(DarkTheme.Selection);
        using var bright = new SolidBrush(Color.FromArgb(95, 164, 220));
        var ambient = _playlist.Type == "ambient";
        TextRenderer.DrawText(g, ambient ? "INDEPENDENT INTERVALS  /  EACH ITEM HAS ITS OWN CLOCK" : "DISPATCH OVERVIEW  /  MINIMUM OFFSETS, NOT VIDEO DURATIONS", font, new Rectangle(12, 4, Width - 24, 20), DarkTheme.Muted);
        var left = 34; var width = Width - left - 20;
        var items = _playlist.Items; double elapsed = 0;
        var positions = items.Select(i => { if (i.Enabled) elapsed += i.DelayMilliseconds; return elapsed; }).ToArray();
        var maximum = ambient ? Math.Max(1, items.Select(i => i.MaxIntervalMilliseconds ?? 0).DefaultIfEmpty().Max()) : Math.Max(1000, elapsed);
        for (var i = 0; i <= 4; i++)
        {
            var x = left + width * i / 4; g.DrawLine(accent, x, 30, x, Height - 22);
            TextRenderer.DrawText(g, (maximum * i / 4000d).ToString("0.###") + "s", font, new Point(Math.Min(Width - 48, x - 10), Height - 20), DarkTheme.Muted);
        }
        if (ambient)
        {
            var start = Math.Max(0, _selected - 1);
            for (var i = start; i < Math.Min(items.Length, start + 3); i++)
            {
                var item = items[i]; var y = 34 + (i - start) * 16;
                var x = left + (int)((double)width * (item.MinIntervalMilliseconds ?? 0) / maximum);
                var end = left + (int)((double)width * (item.MaxIntervalMilliseconds ?? 0) / maximum);
                var r = new Rectangle(x, y, Math.Max(6, end - x), 10); g.FillRectangle(i == _selected ? bright : fill, r); g.DrawRectangle(accent, r);
                _hits.Add((Rectangle.Inflate(r, 4, 3), i));
            }
        }
        else for (var i = 0; i < items.Length; i++)
        {
            var x = left + (int)(width * positions[i] / maximum); var y = 33 + i % 3 * 16;
            var r = new Rectangle(Math.Min(x, Width - 30), y, 22, 14);
            g.FillRectangle(i == _selected ? bright : fill, r); g.DrawRectangle(accent, r);
            TextRenderer.DrawText(g, (i + 1).ToString(), font, r, items[i].Enabled ? DarkTheme.Text : DarkTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            _hits.Add((r, i));
        }
    }
    protected override void OnMouseDown(MouseEventArgs e)
    { base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return; var hit = _hits.LastOrDefault(h => h.Bounds.Contains(e.Location)); if (hit.Bounds != Rectangle.Empty) Selected?.Invoke(hit.Index); }
}
