using Forms = System.Windows.Forms;
using System.Windows.Threading;
namespace ExperienceX;

// Every display yields its topmost enforcement while a configuration dialog is open.
internal class ConfigurationDialog : Forms.Form
{
    private static int _open;
    internal static bool Active => _open > 0;
    private TopmostGuard? _guard;
    private bool _counted;
    public ConfigurationDialog()
    {
        TopMost=true; ShowInTaskbar=false; StartPosition=Forms.FormStartPosition.CenterParent; MinimizeBox=false; MaximizeBox=false;
        Font=new("Segoe UI",11); BackColor=PhotoBooth.Appearance.DarkFormsTheme.Background; ForeColor=PhotoBooth.Appearance.DarkFormsTheme.Text;
    }
    protected override void OnHandleCreated(EventArgs e)
    { base.OnHandleCreated(e); PhotoBooth.Appearance.DarkWindowChrome.Apply(Handle); }
    protected override void OnShown(EventArgs e)
    {
        PhotoBooth.Appearance.DarkFormsTheme.Dialog(this);
        base.OnShown(e); _counted=true; _open++;
        _guard=new(Handle,Dispatcher.CurrentDispatcher,()=>Visible,()=>null,yieldToController:false);
        _guard.EnsureTopmost("ConfigurationDialogOpened",true); Activate();
    }
    private void Release() { _guard?.Dispose(); _guard=null; if(_counted){_counted=false;_open--;} }
    protected override void OnFormClosed(Forms.FormClosedEventArgs e) { Release(); base.OnFormClosed(e); }
    protected override void Dispose(bool disposing) { if(disposing)Release(); base.Dispose(disposing); }
}
