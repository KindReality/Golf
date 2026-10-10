namespace Controller.Windows;

internal static class DarkTheme
{
    public static readonly Color Background = Color.FromArgb(11, 16, 25);
    public static readonly Color Surface = Color.FromArgb(19, 28, 41);
    public static readonly Color Raised = Color.FromArgb(25, 38, 56);
    public static readonly Color Border = Color.FromArgb(42, 60, 80);
    public static readonly Color Text = Color.FromArgb(229, 237, 244);
    public static readonly Color Muted = Color.FromArgb(147, 166, 186);
    public static readonly Color Accent = Color.FromArgb(99, 215, 232);
    public static readonly Color Selection = Color.FromArgb(30, 61, 78);
    public static readonly Color Warning = Color.FromArgb(234, 191, 112);
    public static readonly Color Error = Color.FromArgb(244, 139, 150);

    public static void Button(Button button, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = primary ? Selection : Raised;
        button.ForeColor = primary ? Accent : Text;
        button.FlatAppearance.BorderColor = primary ? Color.FromArgb(61, 123, 143) : Border;
        button.FlatAppearance.MouseOverBackColor = Selection;
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(38, 80, 97);
        button.Padding = new Padding(12, 5, 12, 5);
        button.MinimumSize = new Size(0, 36);
        button.Cursor = Cursors.Hand;
    }

    public static void Choices(ComboBox box)
    {
        box.FlatStyle = FlatStyle.Flat;
        box.BackColor = Raised;
        box.ForeColor = Text;
        box.DrawMode = DrawMode.OwnerDrawFixed;
        box.ItemHeight = 26;
        box.DrawItem += (_, e) =>
        {
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using var fill = new SolidBrush(selected ? Selection : Raised);
            e.Graphics.FillRectangle(fill, e.Bounds);
            var text = e.Index >= 0 ? box.GetItemText(box.Items[e.Index]) : box.Text;
            TextRenderer.DrawText(e.Graphics, text, box.Font, Rectangle.Inflate(e.Bounds, -9, 0), selected ? Accent : Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        };
    }
    public static Label Caption(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 9, FontStyle.Bold),
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty
    };
}

internal sealed class DarkCard : Panel
{
    public DarkCard()
    {
        DoubleBuffered = true;
        BackColor = DarkTheme.Surface;
        ForeColor = DarkTheme.Text;
        Padding = new Padding(18);
        Dock = DockStyle.Fill;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(DarkTheme.Border);
        e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        using var accent = new Pen(Color.FromArgb(47, 94, 114));
        e.Graphics.DrawLine(accent, 18, 0, Math.Min(70, Width - 1), 0);
    }
}

internal sealed class DarkComboBox : ComboBox
{
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if ((m.Msg != 0x000F && m.Msg != 0x0318 && m.Msg != 0x0317) || DropDownStyle != ComboBoxStyle.DropDownList) return;
        // Keep native selection, keyboard navigation and accessibility; theme the arrow chrome.
        using var graphics = m.Msg == 0x000F ? Graphics.FromHwnd(Handle) : Graphics.FromHdc(m.WParam);
        var arrowWidth = Math.Max(20, SystemInformation.VerticalScrollBarWidth + 6);
        using var fill = new SolidBrush(DarkTheme.Raised);
        graphics.FillRectangle(fill, Width - arrowWidth - 1, 1, arrowWidth, Height - 2);
        using var border = new Pen(Focused ? DarkTheme.Accent : DarkTheme.Border);
        graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        using var arrow = new Pen(Enabled ? DarkTheme.Muted : DarkTheme.Border, 1.5f);
        var center = Width - arrowWidth / 2 - 1;
        graphics.DrawLines(arrow, [new Point(center - 4, Height / 2 - 2), new Point(center, Height / 2 + 2), new Point(center + 4, Height / 2 - 2)]);
    }
}

internal sealed class DarkListView : ListView
{
    public DarkListView()
    {
        DoubleBuffered = true;
        BackColor = DarkTheme.Surface;
        ForeColor = DarkTheme.Text;
        BorderStyle = BorderStyle.None;
        OwnerDraw = true;
    }
    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        using var fill = new SolidBrush(DarkTheme.Raised);
        e.Graphics.FillRectangle(fill, e.Bounds);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text, Font, Rectangle.Inflate(e.Bounds, -8, 0), DarkTheme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        if (View != View.Details) e.DrawDefault = true;
    }
    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null) return;
        var selected = e.Item.Selected;
        var background = selected ? DarkTheme.Selection : e.ItemIndex % 2 == 0 ? DarkTheme.Surface : Color.FromArgb(21, 31, 45);
        using var fill = new SolidBrush(background);
        e.Graphics.FillRectangle(fill, e.Bounds);
        var level = e.Item.SubItems.Count > 1 ? e.Item.SubItems[1].Text : "";
        var color = level == "Error" ? DarkTheme.Error : level == "Warning" ? DarkTheme.Warning : selected ? DarkTheme.Accent : DarkTheme.Text;
        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text, Font, Rectangle.Inflate(e.Bounds, -8, 0), color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (selected && Focused && e.ColumnIndex == 0)
        {
            using var accent = new SolidBrush(DarkTheme.Accent);
            e.Graphics.FillRectangle(accent, e.Bounds.Left, e.Bounds.Top, 2, e.Bounds.Height);
        }
    }
}
