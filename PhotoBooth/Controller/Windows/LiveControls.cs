namespace Controller.Windows;

// Compact styling is local to Live; other editors keep their normal spacing.
internal static class LiveControls
{
    internal static void StyleButton(Button button, bool primary = false)
    {
        DarkTheme.Button(button, primary);
        button.MinimumSize = new(0, 26);
        button.Padding = new(6, 2, 6, 2);
        button.Margin = new(2);
        if (button.AutoSize) button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    internal static FlowLayoutPanel Toolbar() => new()
    {
        Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = true, Margin = Padding.Empty, Padding = Padding.Empty
    };

    internal static Label Caption(string text) => new()
    { Text = text, AutoSize = true, ForeColor = DarkTheme.Accent, Margin = new(2, 7, 4, 0) };
}
