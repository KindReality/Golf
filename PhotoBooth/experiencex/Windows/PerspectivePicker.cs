using PhotoBooth.Networking;
using Forms = System.Windows.Forms;

namespace ExperienceX;

internal sealed class PerspectivePicker : ConfigurationDialog
{
    private readonly Forms.ListBox _items = new() { Dock = Forms.DockStyle.Fill };
    public PerspectiveSelection? Selection => PerspectiveChoices.Selected(_items);
    public PerspectivePicker(IReadOnlyList<PerspectiveSelection> available, PerspectiveSelection? current = null, bool led = false)
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
        var buttons = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Bottom, Height = 45, FlowDirection = Forms.FlowDirection.RightToLeft };
        var use = new Forms.Button { Text = "Use perspective", AutoSize = true, DialogResult = Forms.DialogResult.OK };
        use.Enabled = _items.Items.Count > 0;
        var cancel = new Forms.Button { Text = "Cancel", AutoSize = true, DialogResult = Forms.DialogResult.Cancel };
        buttons.Controls.Add(use);
        buttons.Controls.Add(cancel);
        AcceptButton = use;
        CancelButton = cancel;
        _items.DoubleClick += (_, _) => { DialogResult = Forms.DialogResult.OK; Close(); };
        Controls.Add(_items);
        Controls.Add(buttons);
    }
}
