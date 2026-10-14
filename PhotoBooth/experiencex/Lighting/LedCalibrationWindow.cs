using System.ComponentModel;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace ExperienceX;

// Only tools occupy this HWND. Excluding it never hides the actual media/mats/G-Force.
internal sealed class LedCalibrationWindow : Forms.Form
{
    internal VideoRenderer Renderer { get; }
    protected override bool ShowWithoutActivation => true;
    protected override Forms.CreateParams CreateParams
    {
        get { var p=base.CreateParams; p.ExStyle|=0x00200000|0x08000000|0x00000020; return p; }
    }
    internal LedCalibrationWindow(MainWindow owner, ExperienceOptions options)
    {
        Text="ExperienceX • LED editing guides"; FormBorderStyle=Forms.FormBorderStyle.None;
        ShowInTaskbar=false; StartPosition=Forms.FormStartPosition.Manual; Bounds=new(owner.PointToScreen(System.Drawing.Point.Empty),owner.ClientSize);
        TopMost=true;
        if(!SetWindowDisplayAffinity(Handle,0x11)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Windows could not exclude LED editing guides from capture.");
        Renderer=new(Handle,ClientSize.Width,ClientSize.Height,options,owner.DisplayName,()=>{} ,toolsOnly:true);
        Show(owner);
    }
    internal void Update(MainWindow owner,ExperienceOptions options)
    {
        var next=new System.Drawing.Rectangle(owner.PointToScreen(System.Drawing.Point.Empty),owner.ClientSize);
        if(Bounds!=next) { Bounds=next; Renderer.Resize(Math.Max(1,ClientSize.Width),Math.Max(1,ClientSize.Height)); }
        Renderer.Configure(options);
        if(owner.Visible && options.ConfigurationMode) { if(!Visible) Show(owner); }
        else Hide();
    }
    protected override void WndProc(ref Forms.Message message)
    {
        if(message.Msg==0x84) { message.Result=new nint(-1); return; } // HTTRANSPARENT: editing belongs to the content window.
        base.WndProc(ref message);
    }
    internal async Task ShutdownAsync() { Hide(); await Task.Run(Renderer.Dispose); Close(); Dispose(); }
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowDisplayAffinity(nint hwnd,uint affinity);
}
