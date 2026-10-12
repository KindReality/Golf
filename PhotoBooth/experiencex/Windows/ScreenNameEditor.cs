using Forms = System.Windows.Forms;

namespace ExperienceX;

// Handle native keys because the application's WPF dispatcher has no WinForms pre-translation loop.
internal sealed class ScreenNameEditor(Action<bool> finish, Action<int>? browse = null) : Forms.TextBox
{
    protected override bool IsInputKey(Forms.Keys keyData) => (keyData & Forms.Keys.KeyCode) == Forms.Keys.Tab || base.IsInputKey(keyData);
    protected override void WndProc(ref Forms.Message message)
    {
        if (message.Msg == 0x100 && (int)message.WParam is 37 or 39 && browse is not null && Forms.Control.ModifierKeys == Forms.Keys.None)
        {
            browse((int)message.WParam == 37 ? -1 : 1);
            message.Result = 0;
            return;
        }
        if (message.Msg == 0x100 && (int)message.WParam is 9 or 13 or 27)
        {
            finish((int)message.WParam != 27);
            message.Result = 0;
            return;
        }
        if (message.Msg == 0x102 && (int)message.WParam is 9 or 13 or 27)
        {
            message.Result = 0;
            return;
        }
        base.WndProc(ref message);
    }
}
