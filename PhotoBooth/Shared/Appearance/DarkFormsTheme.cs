using System.Drawing;
using System.Windows.Forms;

namespace PhotoBooth.Appearance;

// Shared palette and button treatment for Controller pages and Experience configuration dialogs.
internal static class DarkFormsTheme
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

    public static void Dialog(Form dialog)
    {
        void Apply(Control control)
        {
            control.BackColor = control is ListBox or TextBoxBase or ComboBox ? Surface : Background;
            if (control is not Label) control.ForeColor = Text;
            if (control is Button button) Button(button, ReferenceEquals(dialog.AcceptButton, button));
            foreach (Control child in control.Controls) Apply(child);
        }
        Apply(dialog);
    }
}
