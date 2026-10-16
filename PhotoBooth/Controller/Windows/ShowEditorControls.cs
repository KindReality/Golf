namespace Controller.Windows;

internal static class ShowEditorControls
{
    internal static TextBox TextField(string value = "", bool multiline = false) => new()
    { Text = value, BackColor = DarkTheme.Raised, ForeColor = DarkTheme.Text, BorderStyle = BorderStyle.FixedSingle,
        Multiline = multiline, AcceptsReturn = multiline, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
        Height = multiline ? 190 : 30, AccessibleName = "Text" };
    internal static ComboBox Choice(IEnumerable<string> choices, string? value = null, bool editable = false)
    {
        var box = new DarkComboBox { DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList, Height = 30 };
        DarkTheme.Choices(box); box.Items.AddRange(choices.Distinct(StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray());
        if (editable) box.Text = value ?? "";
        else if (value is not null && box.Items.Cast<string>().FirstOrDefault(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase)) is { } match) box.SelectedItem = match;
        else if (box.Items.Count > 0) box.SelectedIndex = 0;
        return box;
    }
    internal static NumericUpDown Number(decimal value = 0, decimal maximum = 86400000) => new()
    { Minimum = 0, Maximum = maximum, Value = Math.Clamp(value, 0, maximum), ThousandsSeparator = true,
        BackColor = DarkTheme.Raised, ForeColor = DarkTheme.Text, BorderStyle = BorderStyle.FixedSingle, Height = 30 };
    internal static CheckBox Check(string text, bool value) => new() { Text = text, AccessibleName = text, Checked = value, AutoSize = true, ForeColor = DarkTheme.Text, Padding = new(0, 4, 0, 4) };
    internal static Button Button(string text, Action action, bool primary = false)
    { var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink }; DarkTheme.Button(b, primary); b.Padding = new(8, 5, 8, 5); b.Click += (_, _) => action(); return b; }
    internal static FlowLayoutPanel Toolbar(params Control[] controls)
    { var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Height = 48, AutoScroll = false, WrapContents = true, Margin = Padding.Empty }; panel.Controls.AddRange(controls); return panel; }
    internal static ListView List(params string[] columns)
    {
        var list = new DarkListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            HideSelection = false, Font = new("Segoe UI", 10), AccessibleName = columns[0] + " list" };
        foreach (var column in columns) list.Columns.Add(column, 130);
        var resizing = false;
        list.SizeChanged += (_, _) =>
        {
            if (resizing) return;
            resizing = true;
            try
            {
                var available = Math.Max(0, list.ClientSize.Width - 8);
                for (var i = 0; i < list.Columns.Count; i++)
                {
                    var width = list.Columns.Count == 2 ? (int)(available * (i == 0 ? .65 : .35)) : i == 0 ? available / 2 : available / Math.Max(2, (list.Columns.Count - 1) * 2);
                    if (list.Columns[i].Width != width) list.Columns[i].Width = width;
                }
            }
            finally { resizing = false; }
        };
        return list;
    }
    internal static Label Note(string text) => new() { Text = text, AutoSize = false, Height = text.Length > 90 ? 62 : 34, ForeColor = DarkTheme.Muted, Dock = DockStyle.Top, Padding = new(0, 6, 0, 8), AutoEllipsis = true };
    internal static Panel ScrollContainer(Control content)
    { var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = DarkTheme.Surface, Padding = new(12) }; panel.Controls.Add(content); return panel; }
    internal static void Watch(Control control, Action changed)
    {
        switch (control)
        {
            case TextBoxBase text: text.TextChanged += (_, _) => changed(); break;
            case ComboBox combo: combo.TextChanged += (_, _) => changed(); break;
            case NumericUpDown number: number.ValueChanged += (_, _) => changed(); break;
            case CheckBox check: check.CheckedChanged += (_, _) => changed(); break;
        }
    }
}

internal sealed class ShowEditorFields : TableLayoutPanel
{
    internal ShowEditorFields()
    {
        Dock = DockStyle.Top; AutoSize = false; Height = 4; ColumnCount = 2; GrowStyle = TableLayoutPanelGrowStyle.AddRows;
        BackColor = DarkTheme.Surface; Padding = new(2);
        ColumnStyles.Add(new(SizeType.Percent, 34)); ColumnStyles.Add(new(SizeType.Percent, 66));
    }
    internal void Field(string label, Control control)
    {
        var row = RowCount++; RowStyles.Add(new(SizeType.Absolute, Math.Max(44, control.Height + 10)));
        control.AccessibleName = label; control.Dock = DockStyle.Top; control.Margin = new(4, 4, 4, 6);
        var caption = new Label { Text = label, AutoSize = true, Dock = DockStyle.Top, ForeColor = DarkTheme.Muted, Margin = new(4, 8, 6, 4) };
        Controls.Add(caption, 0, row); Controls.Add(control, 1, row);
        FitRow(control, row, 44);
    }
    internal void Wide(Control control)
    {
        var row = RowCount++; RowStyles.Add(new(SizeType.Absolute, control.Height + 6));
        control.Dock = DockStyle.Top; Controls.Add(control, 0, row); SetColumnSpan(control, 2); FitRow(control, row, 0);
    }
    private void FitRow(Control control, int row, int minimum)
    {
        void Fit()
        {
            if (row >= RowStyles.Count || control.IsDisposed) return;
            var height = Math.Max(minimum, control.Height + control.Margin.Vertical + 2);
            if (RowStyles[row].Height != height) RowStyles[row].Height = height;
            Height = (int)RowStyles.Cast<RowStyle>().Sum(r => r.Height) + Padding.Vertical;
        }
        control.SizeChanged += (_, _) => Fit(); Fit();
    }
}
