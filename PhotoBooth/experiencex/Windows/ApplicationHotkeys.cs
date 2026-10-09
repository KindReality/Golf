using System.Runtime.InteropServices;
using PhotoBooth.Diagnostics;
using Forms = System.Windows.Forms;

namespace ExperienceX;

// A message-only window receives shortcuts even while another application has focus.
internal sealed class ApplicationHotkeys(Action toggleConfiguration, Action close, Action toggleBackground) : Forms.NativeWindow, IDisposable
{
    internal const int ConfigurationId = 0x584B, CloseId = 0x5858, BackgroundId = 0x5854;
    internal const uint Modifiers = 0x4007; // MOD_NOREPEAT | MOD_CONTROL | MOD_ALT | MOD_SHIFT
    internal const uint LetterModifiers = 0x4000; // MOD_NOREPEAT
    private bool _configurationRegistered, _closeRegistered, _backgroundRegistered, _disposed;
    private bool _configurationMode;

    public string[] Register(bool configurationMode = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Handle == nint.Zero)
            CreateHandle(new Forms.CreateParams { Caption = "ExperienceX.ApplicationHotkeys", Parent = new nint(-3) });
        if (_configurationMode != configurationMode)
        {
            Unregister();
            _configurationMode = configurationMode;
        }
        List<string> unavailable = [];
        var modifiers = configurationMode ? LetterModifiers : Modifiers;
        Register(ConfigurationId, 0x43, modifiers, configurationMode ? "C" : "Ctrl+Shift+Alt+C", "ConfigurationHotkey", ref _configurationRegistered, unavailable);
        if (configurationMode)
        {
            Register(CloseId, 0x58, modifiers, "X", "CloseHotkey", ref _closeRegistered, unavailable);
            Register(BackgroundId, 0x54, modifiers, "T", "BackgroundHotkey", ref _backgroundRegistered, unavailable);
        }
        return unavailable.ToArray();
    }

    private void Register(int id, uint key, uint modifiers, string shortcut, string eventPrefix, ref bool registered, List<string> unavailable)
    {
        if (registered)
            return;
        registered = RegisterHotKey(Handle, id, modifiers, key);
        if (registered)
            Telemetry.Info(eventPrefix + "Registered", new
            {
                Shortcut = shortcut,
                Repeat = false
            });
        else
        {
            var error = Marshal.GetLastWin32Error();
            unavailable.Add(shortcut);
            Telemetry.Warning(eventPrefix + "Unavailable", new
            {
                Shortcut = shortcut,
                Win32Error = error
            });
        }
    }

    protected override void WndProc(ref Forms.Message message)
    {
        if (message.Msg == 0x0312 && !_disposed)
        {
            // Mode changes can leave old shortcut messages queued. Match the current chord.
            var data = message.LParam.ToInt64();
            var modifiers = (uint)(data & 0xFFFF);
            var key = (uint)((data >> 16) & 0xFFFF);
            if (modifiers != (_configurationMode ? 0u : 7u))
            {
                base.WndProc(ref message);
                return;
            }
            if (message.WParam == ConfigurationId && key == 0x43 && _configurationRegistered)
            {
                toggleConfiguration();
                return;
            }
            if (message.WParam == CloseId && key == 0x58 && _closeRegistered)
            {
                close();
                return;
            }
            if (message.WParam == BackgroundId && key == 0x54 && _backgroundRegistered)
            {
                toggleBackground();
                return;
            }
        }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Unregister();
        if (Handle != nint.Zero)
            DestroyHandle();
    }

    private void Unregister()
    {
        if (_configurationRegistered)
            UnregisterHotKey(Handle, ConfigurationId);
        if (_closeRegistered)
            UnregisterHotKey(Handle, CloseId);
        if (_backgroundRegistered)
            UnregisterHotKey(Handle, BackgroundId);
        _configurationRegistered = _closeRegistered = _backgroundRegistered = false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
