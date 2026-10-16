using Controller.Windows;

internal static class ToolbarChecks
{
    internal static void Run()
    {
        void Check(bool condition, string text) { if (!condition) throw new Exception(text); Console.WriteLine("PASS " + text); }
        var restarts = 0; var shutdowns = 0;
        using var form = new Form { ClientSize = new(720, 100), FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            BackColor = DarkTheme.Background, StartPosition = FormStartPosition.Manual, Location = new(-32000, -32000) };
        using var power = new PowerToolbarButton(() => restarts++, () => shutdowns++) { Location = new(628, 28) };
        form.Controls.Add(power); form.Show();
        Check(!power.RestartButton.Enabled && !power.MenuButton.Enabled, "Power controls stay disabled until actions are available");
        power.ShutdownEnabled = true;
        Check(!power.RestartButton.Enabled && power.MenuButton.Enabled && !power.RestartItem.Enabled && power.ShutdownItem.Enabled,
            "Shutdown remains available from the chevron when restart is unavailable");
        power.RestartEnabled = true; power.RestartButton.PerformClick();
        Check(power.RestartButton.AccessibleName == "Restart Controller and Experience", "Power icon describes restarting both applications");
        Check(restarts == 1 && shutdowns == 0, "Power icon performs one restart and never shuts down");
        power.MenuButton.PerformClick();
        Check(power.Menu.Visible && power.Menu.Items.Cast<ToolStripItem>().Select(i => i.Text).SequenceEqual(["Restart", "Shutdown"]), "Chevron opens the themed Restart / Shutdown menu without invoking an action");
        power.Menu.Close(); power.RestartItem.PerformClick(); power.ShutdownItem.PerformClick();
        Check(restarts == 2 && shutdowns == 1, "Restart and Shutdown menu items invoke their respective actions once");
        Check(power.MenuButton.Left >= power.RestartButton.Right && power.MenuButton.Height == power.RestartButton.Height,
            "Power control places the chevron to the right at the same height");
        foreach (var (glyph, description, x) in new[] { (ToolbarGlyph.Return, "Return to dashboard", 18), (ToolbarGlyph.Play, "Exit configuration mode", 78), (ToolbarGlyph.Configure, "Enter configuration mode", 138), (ToolbarGlyph.Minimize, "Minimize Controller", 198) })
        {
            var button = new ToolbarIconButton(glyph, description) { Location = new(x, 28) }; form.Controls.Add(button);
            using var icon = new Bitmap(button.Width, button.Height); button.DrawToBitmap(icon, new(0, 0, icon.Width, icon.Height));
            var colored = 0;
            for (var y = 8; y < button.Height - 8; y++) for (var px = 8; px < button.Width - 8; px++)
            { var color = icon.GetPixel(px, y); if (color.G > 140 && color.B > 140 && color.R < color.G) colored++; }
            Check(colored >= 10 && button.Text.Length == 0 && button.AccessibleName == description, glyph + " glyph renders in Azure with an accessible icon-only label");
        }
        using var screenshot = new Bitmap(form.Width, form.Height); form.DrawToBitmap(screenshot, new(0, 0, screenshot.Width, screenshot.Height));
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.tools/controller-toolbar-icons.png"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!); screenshot.Save(output);
        power.RestartEnabled = power.ShutdownEnabled = false; Check(!power.MenuButton.Enabled, "Restart/shutdown transitions disable the power menu");
    }
}
