using PhotoBooth.Networking;
using Forms=System.Windows.Forms;
namespace ExperienceX;
internal sealed class ControllerPicker : ConfigurationDialog
{
    private sealed record Choice(ControllerSharedDefinitions Controller) { public override string ToString()=>Controller.ControllerName+" ("+Controller.Hostname+")"; }
    private readonly Forms.ListBox _items=new(){Dock=Forms.DockStyle.Fill};
    public ControllerSharedDefinitions? Selection=>(_items.SelectedItem as Choice)?.Controller;
    public ControllerPicker(IEnumerable<ControllerSharedDefinitions> controllers)
    {
        Text="Save perspective on Controller"; ClientSize=new(510,280);Font=new("Segoe UI",11);
        BackColor=System.Drawing.Color.FromArgb(14,21,31);ForeColor=System.Drawing.Color.Azure;
        _items.BackColor=BackColor;_items.ForeColor=ForeColor;_items.BorderStyle=Forms.BorderStyle.None;
        foreach(var controller in controllers)_items.Items.Add(new Choice(controller));if(_items.Items.Count>0)_items.SelectedIndex=0;
        var buttons=new Forms.FlowLayoutPanel{Dock=Forms.DockStyle.Bottom,Height=45,FlowDirection=Forms.FlowDirection.RightToLeft};
        var use=new Forms.Button{Text="Use Controller",AutoSize=true,DialogResult=Forms.DialogResult.OK};
        var cancel=new Forms.Button{Text="Cancel",AutoSize=true,DialogResult=Forms.DialogResult.Cancel};buttons.Controls.Add(use);buttons.Controls.Add(cancel);
        AcceptButton=use;CancelButton=cancel;Controls.Add(_items);Controls.Add(buttons);
    }
}
