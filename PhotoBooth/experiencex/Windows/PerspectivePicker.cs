using PhotoBooth.Networking;
using Forms = System.Windows.Forms;

namespace ExperienceX;

internal sealed class PerspectivePicker : ConfigurationDialog
{
    private readonly Forms.ListBox _items = new() { Dock = Forms.DockStyle.Fill };
    public PerspectiveSelection? Selection => PerspectiveChoices.Selected(_items);
    public bool CreateNew => _items.SelectedItem is NewPerspectiveChoice;
    private sealed record NewPerspectiveChoice(double Width, double Height)
    {
        public override string ToString() => $"Create from current screen — {Width:0.##}% × {Height:0.##}%";
    }
    public PerspectivePicker(IReadOnlyList<PerspectiveSelection> available, PerspectiveSelection? current = null, bool led = false,
        (double WidthPercent, double HeightPercent)? createFrom = null)
    {
        Text = "Choose perspective";

        ClientSize = new(510, 320);
        Font = new("Segoe UI", 11);
        BackColor = System.Drawing.Color.FromArgb(14, 21, 31);
        ForeColor = System.Drawing.Color.Azure;
        _items.BackColor = BackColor;
        _items.ForeColor = ForeColor;
        _items.BorderStyle = Forms.BorderStyle.None;
        PerspectiveChoices.Populate(_items, available, current, led);
        if (!led && createFrom is { } dimensions)
        {
            _items.Items.Insert(0, new NewPerspectiveChoice(dimensions.WidthPercent, dimensions.HeightPercent));
            _items.SelectedIndex = 0;
        }
        var buttons = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Bottom, Height = 45, FlowDirection = Forms.FlowDirection.RightToLeft };
        var use = new Forms.Button { Text = CreateNew ? "Create perspective" : "Use perspective", AutoSize = true, DialogResult = Forms.DialogResult.OK };
        _items.SelectedIndexChanged += (_, _) => use.Text = CreateNew ? "Create perspective" : "Use perspective";
        use.Enabled = _items.Items.Count > 0;
        var cancel = new Forms.Button { Text = "Cancel", AutoSize = true, DialogResult = Forms.DialogResult.Cancel };
        buttons.Controls.Add(use);
        buttons.Controls.Add(cancel);
        AcceptButton = use;
        CancelButton = cancel;
        _items.DoubleClick += (_, _) => { if (_items.SelectedItem is not null) { DialogResult = Forms.DialogResult.OK; Close(); } };
        Controls.Add(_items);
        Controls.Add(buttons);
    }
}
