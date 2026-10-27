using Controller.Diagnostics;
using static Controller.Windows.ShowEditorControls;

namespace Controller.Windows;

internal sealed class LiveActivityPanel : UserControl
{
    private readonly ActivityLog _log;
    private readonly ListBox _feed = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 52,
        BackColor = DarkTheme.Surface, ForeColor = DarkTheme.Text, BorderStyle = BorderStyle.None, IntegralHeight = false, AccessibleName = "Live activity feed" };
    private readonly Dictionary<string, Button> _filters = [];
    private ActivityEntry? _latest;
    private string _filter = "All";
    private readonly ToolTip _tips = new();
    private ActivityEntry? _hovered;
    internal LiveActivityPanel(ActivityLog log)
    {
        _log = log; Dock = DockStyle.Fill; BackColor = DarkTheme.Surface; Font = new("Segoe UI", 9);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.Absolute, 30)); layout.RowStyles.Add(new(SizeType.Absolute, 30)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new(SizeType.Percent, 100)); heading.ColumnStyles.Add(new(SizeType.Absolute, 54));
        heading.Controls.Add(new Label { Text = "LIVE ACTIVITY", Dock = DockStyle.Fill, ForeColor = DarkTheme.Accent, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        var clear = Button("Clear", () => { _log.Clear(); RefreshState(force: true); }); clear.AccessibleName = "Clear live activity";
        LiveControls.StyleButton(clear);
        clear.AutoSize = false; clear.Dock = DockStyle.Fill; clear.Padding = new(2);
        heading.Controls.Add(clear, 1, 0); layout.Controls.Add(heading, 0, 0);
        var filters = Toolbar(); filters.WrapContents = false;
        foreach (var text in new[] { "All", "Commands", "Events" })
        {
            var button = Button(text, () => { _filter = text; RefreshState(force: true); });
            LiveControls.StyleButton(button);
            _filters[text] = button; filters.Controls.Add(button);
        }
        layout.Controls.Add(filters, 0, 1); layout.Controls.Add(_feed, 0, 2); Controls.Add(layout);
        _feed.DrawItem += DrawEntry;
        _feed.SizeChanged += (_, _) => _feed.Invalidate();
        _feed.MouseMove += (_, e) =>
        {
            var index = _feed.IndexFromPoint(e.Location);
            var entry = index >= 0 ? (ActivityEntry)_feed.Items[index] : null;
            if (entry == _hovered) return;
            _hovered = entry; _tips.SetToolTip(_feed, entry?.Message);
        };
    }
    internal void RefreshState(bool force = false)
    {
        var entries = _log.Snapshot();
        if (!force && entries.FirstOrDefault() == _latest) return;
        _latest = entries.FirstOrDefault(); var top = _feed.Items.Count == 0 ? 0 : _feed.TopIndex;
        _feed.BeginUpdate(); _feed.Items.Clear();
        foreach (var entry in entries.Where(e => _filter == "All" || e.Kind == (_filter == "Commands" ? "Command" : "Event"))) _feed.Items.Add(entry);
        if (_feed.Items.Count > 0) _feed.TopIndex = Math.Min(top, _feed.Items.Count - 1);
        _feed.EndUpdate(); foreach (var pair in _filters) LiveControls.StyleButton(pair.Value, pair.Key == _filter);
    }
    private void DrawEntry(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var entry = (ActivityEntry)_feed.Items[e.Index]; var bounds = e.Bounds;
        using var fill = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? DarkTheme.Selection : DarkTheme.Surface);
        e.Graphics.FillRectangle(fill, bounds);
        var color = entry.Level == "Error" ? DarkTheme.Error : entry.Kind == "Event" ? DarkTheme.Warning : entry.Kind == "Command" ? DarkTheme.Accent : DarkTheme.Muted;
        TextRenderer.DrawText(e.Graphics, entry.At.ToString("HH:mm:ss"), Font, new Rectangle(bounds.X + 3, bounds.Y + 3, 58, 18), DarkTheme.Muted);
        TextRenderer.DrawText(e.Graphics, entry.Kind.ToUpperInvariant(), Font, new Rectangle(bounds.X + 62, bounds.Y + 3, 78, 18), color);
        var compact = bounds.Width < 520;
        var messageBounds = compact ? new Rectangle(bounds.X + 3, bounds.Y + 21, bounds.Width - 8, 30) : new Rectangle(bounds.X + 140, bounds.Y + 3, Math.Max(20, bounds.Width - 146), 46);
        TextRenderer.DrawText(e.Graphics, entry.Message, Font, messageBounds, DarkTheme.Text,
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using var line = new Pen(DarkTheme.Border); e.Graphics.DrawLine(line, bounds.Left + 4, bounds.Bottom - 1, bounds.Right - 4, bounds.Bottom - 1);
    }
    protected override void Dispose(bool disposing) { if (disposing) _tips.Dispose(); base.Dispose(disposing); }
}
