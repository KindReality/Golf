using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace PhotoBooth.Windows;

// One shortcut owner, with a hidden top-level receiver in each application. No network/service.
internal sealed class ConfigurationShortcut : Forms.NativeWindow, IDisposable
{
    private const int HotkeyId = 0x5343;
    private readonly uint _enable = RegisterWindowMessage("HomeTechnologies.EnableConfiguration.v1");
    private readonly Forms.Timer _retry = new() { Interval = 1000 };
    private readonly Action _received;
    private readonly string _application;
    private bool _registered, _disposed;
    internal bool OwnsShortcut => _registered && !_disposed;
    public ConfigurationShortcut(string application, Action received)
    {
        _received = received;
        _application = application;
        CreateHandle(new Forms.CreateParams { Caption = "HomeTechnologies.Configuration." + application });
        _retry.Tick += (_, _) => TryRegister();
        TryRegister(); _retry.Start();
    }
    private void TryRegister() { if (!_disposed && !_registered) _registered = RegisterHotKey(Handle, HotkeyId, 0x4007, 0x43); }
    public void EnableBoth()
    {
        var controller = FindWindow(null, "HomeTechnologies.Configuration.Controller");
        var granted=false;
        if (controller != 0) { GetWindowThreadProcessId(controller, out var pid); granted=AllowSetForegroundWindow(pid); }
        PhotoBooth.Diagnostics.Telemetry.Info("SharedConfigurationRequested",new{Application=_application,ControllerFound=controller!=0,ForegroundGrant=granted,ForegroundProcessId});
        PostMessage(new nint(0xffff), _enable, 0, 0);
    }
    public static bool ControllerRunning => FindWindow(null, "HomeTechnologies.Configuration.Controller") != 0;
    public static bool ControllerFocused
    {
        get
        {
            var controller = FindWindow(null, "HomeTechnologies.Configuration.Controller");
            if (controller == 0) return false;
            GetWindowThreadProcessId(controller, out var c);
            GetWindowThreadProcessId(GetForegroundWindow(), out var f);
            return c != 0 && c == f;
        }
    }
    public static bool ApplicationFocused
    {
        get { GetWindowThreadProcessId(GetForegroundWindow(), out var pid); return pid == Environment.ProcessId; }
    }
    public static uint ForegroundProcessId { get { GetWindowThreadProcessId(GetForegroundWindow(),out var pid);return pid; } }
    protected override void WndProc(ref Forms.Message m)
    {
        if (!_disposed && m.Msg == 0x312 && m.WParam == HotkeyId) { EnableBoth(); return; }
        if (!_disposed && (uint)m.Msg == _enable) { _received(); return; }
        base.WndProc(ref m);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _retry.Stop(); _retry.Dispose();
        if (_registered) UnregisterHotKey(Handle, HotkeyId);
        DestroyHandle();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint h, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint h, int id);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint h, uint msg, nint w, nint l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? c, string name);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint process);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint process);
}
