using System.Runtime.InteropServices;
using PhotoBooth.Diagnostics;
using Forms = System.Windows.Forms;

namespace ExperienceX;

// A message-only window receives shortcuts even while another application has focus.
internal sealed class ApplicationHotkeys(Action toggleConfiguration, Action close, Action toggleBackground) : Forms.NativeWindow, IDisposable
{
    internal const int ConfigurationId = 0x584B, CloseId = 0x5858, BackgroundId = 0x5842;
    internal const int AddId = 0x5841, VideoId = 0x5856;
    internal const int NewId = 0x584E, MatId = 0x584D;
    internal const int TransparencyId = 0x5854;
    internal const int SizingId = 0x5853;
    public Action? ToggleScreenSizing { get; set; }
    internal const int WhiteBackgroundId = 0x5857;
    public Action? ToggleScreenTransparency { get; set; }
    public Action? ChooseWhiteBackground { get; set; }
    public Action? AddScreenPrefix
    {
        get; set;
    }
    public Action? AddVideoScreen
    {
        get; set;
    }
    public Action? NewScreenPrefix { get; set; }
    public Action? AddMatScreen { get; set; }
    private bool _addRegistered, _videoRegistered, _newRegistered, _matRegistered, _transparencyRegistered, _whiteRegistered, _sizingRegistered, _editingText, _choosingBackground;
    internal const uint Modifiers = 0x4007; // MOD_NOREPEAT | MOD_CONTROL | MOD_ALT | MOD_SHIFT
    internal const uint LetterModifiers = 0x4000; // MOD_NOREPEAT
    private bool _configurationRegistered, _closeRegistered, _backgroundRegistered, _disposed;
    private bool _configurationMode;

    public string[] Register(bool configurationMode = false, bool editingText = false, bool choosingBackground = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Handle == nint.Zero)
            CreateHandle(new Forms.CreateParams { Caption = "ExperienceX.ApplicationHotkeys", Parent = new nint(-3) });
        if (_configurationMode != configurationMode || _editingText != editingText || _choosingBackground != choosingBackground)
        {
            Unregister();
            _configurationMode = configurationMode;
            _editingText = editingText;
            _choosingBackground = choosingBackground;
        }
        List<string> unavailable = [];
        if (editingText)
            return [];
        var modifiers = configurationMode ? LetterModifiers : Modifiers;
        Register(ConfigurationId, 0x43, modifiers, configurationMode ? "C" : "Ctrl+Shift+Alt+C", "ConfigurationHotkey", ref _configurationRegistered, unavailable);
        if (configurationMode)
        {
            Register(CloseId, 0x58, modifiers, "X", "CloseHotkey", ref _closeRegistered, unavailable);
            Register(BackgroundId, 0x42, modifiers, "B", "BackgroundHotkey", ref _backgroundRegistered, unavailable);
            if (choosingBackground && ChooseWhiteBackground is not null)
                Register(WhiteBackgroundId, 0x57, modifiers, "W", "WhiteBackgroundHotkey", ref _whiteRegistered, unavailable);
            if (ToggleScreenTransparency is not null)
                Register(TransparencyId, 0x54, modifiers, "T", "ScreenTransparencyHotkey", ref _transparencyRegistered, unavailable);
            if (ToggleScreenSizing is not null)
                Register(SizingId, 0x53, modifiers, "S", "ScreenSizingHotkey", ref _sizingRegistered, unavailable);
            if (AddScreenPrefix is not null)
                Register(AddId, 0x41, modifiers, "A", "AddScreenHotkey", ref _addRegistered, unavailable);
            if (AddVideoScreen is not null)
                Register(VideoId, 0x56, modifiers, "V", "AddVideoScreenHotkey", ref _videoRegistered, unavailable);
            if (NewScreenPrefix is not null)
                Register(NewId, 0x4E, modifiers, "N", "NewScreenHotkey", ref _newRegistered, unavailable);
            if (AddMatScreen is not null)
                Register(MatId, 0x4D, modifiers, "M", "AddMatScreenHotkey", ref _matRegistered, unavailable);
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
            if (message.WParam == BackgroundId && key == 0x42 && _backgroundRegistered)
            {
                toggleBackground();
                return;
            }
            if (message.WParam == TransparencyId && key == 0x54 && _transparencyRegistered)
            {
                ToggleScreenTransparency?.Invoke();
                return;
            }
            if (message.WParam == SizingId && key == 0x53 && _sizingRegistered)
            {
                ToggleScreenSizing?.Invoke();
                return;
            }
            if (message.WParam == WhiteBackgroundId && key == 0x57 && _whiteRegistered)
            {
                ChooseWhiteBackground?.Invoke();
                return;
            }
            if (message.WParam == AddId && key == 0x41 && _addRegistered)
            {
                AddScreenPrefix?.Invoke();
                return;
            }
            if (message.WParam == VideoId && key == 0x56 && _videoRegistered)
            {
                AddVideoScreen?.Invoke();
                return;
            }
            if (message.WParam == NewId && key == 0x4E && _newRegistered) { NewScreenPrefix?.Invoke(); return; }
            if (message.WParam == MatId && key == 0x4D && _matRegistered) { AddMatScreen?.Invoke(); return; }
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
        if (_addRegistered)
            UnregisterHotKey(Handle, AddId);
        if (_videoRegistered)
            UnregisterHotKey(Handle, VideoId);
        if (_newRegistered) UnregisterHotKey(Handle, NewId);
        if (_matRegistered) UnregisterHotKey(Handle, MatId);
        if (_transparencyRegistered) UnregisterHotKey(Handle, TransparencyId);
        if (_sizingRegistered) UnregisterHotKey(Handle, SizingId);
        if (_whiteRegistered) UnregisterHotKey(Handle, WhiteBackgroundId);
        _whiteRegistered = false;
        _transparencyRegistered = false;
        _sizingRegistered = false;
        _configurationRegistered = _closeRegistered = _backgroundRegistered = _addRegistered = _videoRegistered = _newRegistered = _matRegistered = false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
