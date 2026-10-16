using System.ComponentModel;

namespace Controller.Windows;

internal sealed class PowerToolbarButton : UserControl
{
    internal ToolbarIconButton RestartButton { get; } = new(ToolbarGlyph.Power, "Restart Controller and Experience");
    internal ToolbarIconButton MenuButton { get; } = new(ToolbarGlyph.Chevron, "Power menu");
    internal ContextMenuStrip Menu { get; } = new();
    internal ToolStripMenuItem RestartItem { get; }
    internal ToolStripMenuItem ShutdownItem { get; }
    private readonly ToolTip _tips = new();
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool RestartEnabled
    {
        get => RestartButton.Enabled;
        set { RestartButton.Enabled = RestartItem.Enabled = value; UpdateMenu(); }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool ShutdownEnabled
    {
        get => ShutdownItem.Enabled;
        set { ShutdownItem.Enabled = value; UpdateMenu(); }
    }
    internal PowerToolbarButton(Action restart, Action shutdown)
    {
        AutoScaleMode = AutoScaleMode.Inherit; Size = new(76, 40); Margin = new(3, 2, 3, 2);
        BackColor = DarkTheme.Background; AccessibleName = "Power controls";
        RestartButton.Dock = DockStyle.Fill; RestartButton.Margin = Padding.Empty;
        MenuButton.Dock = DockStyle.Right; MenuButton.Width = 26; MenuButton.Margin = Padding.Empty;
        Controls.Add(RestartButton); Controls.Add(MenuButton);
        RestartItem = new("Restart", null, (_, _) => restart());
        ShutdownItem = new("Shutdown", null, (_, _) => shutdown());
        Menu.Items.Add(RestartItem); Menu.Items.Add(ShutdownItem);
        Menu.BackColor = DarkTheme.Surface; Menu.ForeColor = DarkTheme.Text; Menu.AccessibleName = "Controller power menu";
        Menu.ShowImageMargin = false; Menu.Font = new("Segoe UI", 10); Menu.Padding = new(4);
        Menu.Renderer = new ToolStripProfessionalRenderer(new PowerMenuColors()) { RoundedEdges = false };
        RestartItem.Padding = ShutdownItem.Padding = new(10, 6, 10, 6);
        RestartButton.Click += (_, _) => restart();
        MenuButton.Click += (_, _) => OpenMenu();
        _tips.SetToolTip(RestartButton, "Restart Controller and Experience"); _tips.SetToolTip(MenuButton, "Restart or shut down both applications");
        RestartEnabled = ShutdownEnabled = false;
    }
    private void UpdateMenu() => MenuButton.Enabled = RestartButton.Enabled || ShutdownItem.Enabled;
    internal void OpenMenu()
    {
        if (!MenuButton.Enabled) return;
        var width = Menu.GetPreferredSize(Size.Empty).Width;
        Menu.Show(this, new Point(Width - width, Height + 4));
    }
    protected override void Dispose(bool disposing)
    { if (disposing) { Menu.Dispose(); _tips.Dispose(); } base.Dispose(disposing); }
    private sealed class PowerMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => DarkTheme.Surface;
        public override Color MenuBorder => DarkTheme.Border;
        public override Color MenuItemSelected => DarkTheme.Selection;
        public override Color MenuItemBorder => DarkTheme.Accent;
        public override Color MenuItemPressedGradientBegin => DarkTheme.Selection;
        public override Color MenuItemPressedGradientEnd => DarkTheme.Selection;
        public override Color MenuItemSelectedGradientBegin => DarkTheme.Selection;
        public override Color MenuItemSelectedGradientEnd => DarkTheme.Selection;
    }
}
