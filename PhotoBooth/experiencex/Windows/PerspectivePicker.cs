using PhotoBooth.Networking;
using Forms = System.Windows.Forms;

namespace ExperienceX;

internal sealed class PerspectivePicker : Forms.Form
{
    private readonly Forms.ListBox _items = new() { Dock = Forms.DockStyle.Fill };
    private sealed record Choice(PerspectiveSelection? Value)
    {
        public override string ToString() => Value?.ToString() ?? "Full perspective — entire media";
    }
    public PerspectiveSelection? Selection => (_items.SelectedItem as Choice)?.Value;
    public PerspectivePicker(IReadOnlyList<PerspectiveSelection> available, PerspectiveSelection? current = null)
    {
        Text = "Choose perspective";
        StartPosition = Forms.FormStartPosition.CenterScreen;
        ClientSize = new(510, 320);
        Font = new("Segoe UI", 11);
        BackColor = System.Drawing.Color.FromArgb(14, 21, 31);
        ForeColor = System.Drawing.Color.Azure;
        _items.BackColor = BackColor;
        _items.ForeColor = ForeColor;
        _items.BorderStyle = Forms.BorderStyle.None;
        _items.Items.Add(new Choice(null));
        foreach (var p in available)
            _items.Items.Add(new Choice(p));
        if (current is not null && !available.Any(p => p.ControllerId == current.ControllerId && p.PerspectiveId == current.PerspectiveId))
            _items.Items.Add(new Choice(current));
        _items.SelectedIndex = 0;
        for (int i = 1; i < _items.Items.Count; i++)
            if (((Choice)_items.Items[i]).Value is { } p && p.ControllerId == current?.ControllerId && p.PerspectiveId == current.PerspectiveId)
                _items.SelectedIndex = i;
        var buttons = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Bottom, Height = 45, FlowDirection = Forms.FlowDirection.RightToLeft };
        var use = new Forms.Button { Text = "Use perspective", AutoSize = true, DialogResult = Forms.DialogResult.OK };
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
