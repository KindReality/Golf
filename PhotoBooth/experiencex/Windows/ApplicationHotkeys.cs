using System.Runtime.InteropServices;
using PhotoBooth.Diagnostics;
using Forms = System.Windows.Forms;

namespace ExperienceX;

// A message-only window receives shortcuts even while another application has focus.
internal sealed class ApplicationHotkeys(Action toggleConfiguration, Action restart, Action toggleBackground, Func<bool>? isFocused = null) : Forms.NativeWindow, IDisposable
{
    internal const int ConfigurationId = 0x584B, RestartId = 0x5858, BackgroundId = 0x5842;
    internal const int PerspectiveId = 0x5850;
    public Action? ChoosePerspective
    {
        get; set;
    }
    private bool _perspectiveRegistered;
    internal const int CancelSubmenuId = 0x5890;
    public Action? CancelSubmenu { get; set; }
    private bool _cancelRegistered;
    private ConfigurationSubmenu _submenu;
    internal const int AddId = 0x5841, VideoId = 0x5856;
    internal const int VisualizationId = 0x5847;
    public Action? AddVisualizationScreen { get; set; }
    private bool _visualizationRegistered;
    internal const int LedId = 0x584C;
    public Action? AddLedScreen { get; set; }
    private bool _ledRegistered;
    internal const int NameId = 0x584E, MatId = 0x584D;
    internal const int TransparencyId = 0x5854;
    internal const int BrightnessPlusId = 0x5860, BrightnessShiftPlusId = 0x5861,
        BrightnessMinusId = 0x5862, BrightnessNumpadPlusId = 0x5863, BrightnessNumpadMinusId = 0x5864;
    private static readonly (int Id, uint Key, uint Modifiers, string Label, int Direction)[] BrightnessKeys =
    [
        (BrightnessPlusId, 0xBB, 0x4000, "+/=", 1),
        (BrightnessShiftPlusId, 0xBB, 0x4004, "+", 1),
        (BrightnessMinusId, 0xBD, 0x4000, "-", -1),
        (BrightnessNumpadPlusId, 0x6B, 0x4000, "Numpad +", 1),
        (BrightnessNumpadMinusId, 0x6D, 0x4000, "Numpad -", -1)
    ];
    private readonly bool[] _brightnessRegistered = new bool[BrightnessKeys.Length];
    internal const int ZoomPlusId = 0x5870, ZoomShiftPlusId = 0x5871, ZoomMinusId = 0x5872,
        ZoomNumpadPlusId = 0x5873, ZoomNumpadMinusId = 0x5874;
    internal const int ResetVideoId = 0x5875;
    public Action? ResetVideo { get; set; }
    private bool _resetVideoRegistered;
    private static readonly (int Id, uint Key, uint Modifiers, string Label, int Direction)[] ZoomKeys =
    [
        (ZoomPlusId, 0xBB, 0x4002, "Ctrl+=", 1),
        (ZoomShiftPlusId, 0xBB, 0x4006, "Ctrl++", 1),
        (ZoomMinusId, 0xBD, 0x4002, "Ctrl+-", -1),
        (ZoomNumpadPlusId, 0x6B, 0x4002, "Ctrl+Numpad +", 1),
        (ZoomNumpadMinusId, 0x6D, 0x4002, "Ctrl+Numpad -", -1)
    ];
    private readonly bool[] _zoomRegistered = new bool[ZoomKeys.Length];
    public Action<int>? ZoomScreen { get; set; }
    internal const int CopyScreenId = 0x5880, PasteScreenId = 0x5881;
    public Action? CopyScreen { get; set; }
    public Action? PasteScreen { get; set; }
    public Action? BeforeShortcut { get; set; }
    private bool _copyRegistered, _pasteRegistered;
    public Action<int>? AdjustScreenBrightness
    {
        get; set;
    }
    internal const int WhiteBackgroundId = 0x5857;
    public Action? ToggleScreenTransparency
    {
        get; set;
    }
    public Action? ChooseWhiteBackground
    {
        get; set;
    }
    public Action? AddScreenPrefix
    {
        get; set;
    }
    public Action? AddVideoScreen
    {
        get; set;
    }
    public Action? EditScreenName
    {
        get; set;
    }
    public Action? AddMatScreen
    {
        get; set;
    }
    private bool _addRegistered, _videoRegistered, _nameRegistered, _matRegistered, _transparencyRegistered, _whiteRegistered, _editingText;
    internal const uint Modifiers = 0x4006; // MOD_NOREPEAT | MOD_CONTROL | MOD_SHIFT
    internal const uint LetterModifiers = 0x4000; // MOD_NOREPEAT
    private bool _configurationRegistered, _restartRegistered, _backgroundRegistered, _disposed;
    private bool _configurationMode;
    private bool _focused;
    private readonly Forms.Timer _focusPoll = new() { Interval = 100 };

    public string[] Register(bool configurationMode = false, bool editingText = false, ConfigurationSubmenu submenu = ConfigurationSubmenu.None)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Handle == nint.Zero)
            CreateHandle(new Forms.CreateParams { Caption = "ExperienceX.ApplicationHotkeys", Parent = new nint(-3) });
        var focused = (isFocused?.Invoke() ?? PhotoBooth.Windows.ConfigurationShortcut.ApplicationFocused);
        if (_configurationMode != configurationMode || _editingText != editingText || _submenu != submenu || _focused != focused)
        {
            Unregister();
            _configurationMode = configurationMode;
            _focused = focused;
            _editingText = editingText;
            _submenu = submenu;
        }
        if (!_focusPoll.Enabled)
        {
            _focusPoll.Tick += (_, _) => Register(_configurationMode, _editingText, _submenu);
            _focusPoll.Start();
        }
        List<string> unavailable = [];
        if (editingText || !configurationMode || !focused)
            return [];
        if (submenu != ConfigurationSubmenu.None)
        {
            Register(CancelSubmenuId, 0x1B, LetterModifiers, "Esc", "CancelSubmenuHotkey", ref _cancelRegistered, unavailable);
            if (submenu == ConfigurationSubmenu.AddScreen)
            {
                if (AddVideoScreen is not null) Register(VideoId, 0x56, LetterModifiers, "V", "AddVideoScreenHotkey", ref _videoRegistered, unavailable);
                if (AddMatScreen is not null) Register(MatId, 0x4D, LetterModifiers, "M", "AddMatScreenHotkey", ref _matRegistered, unavailable);
                if (AddVisualizationScreen is not null) Register(VisualizationId, 0x47, LetterModifiers, "G", "AddVisualizationScreenHotkey", ref _visualizationRegistered, unavailable);
                if (AddLedScreen is not null) Register(LedId, 0x4C, LetterModifiers, "L", "AddLedScreenHotkey", ref _ledRegistered, unavailable);
            }
            else
            {
                Register(BackgroundId, 0x42, LetterModifiers, "B", "BackgroundHotkey", ref _backgroundRegistered, unavailable);
                if (ChooseWhiteBackground is not null) Register(WhiteBackgroundId, 0x57, LetterModifiers, "W", "WhiteBackgroundHotkey", ref _whiteRegistered, unavailable);
            }
            return unavailable.ToArray();
        }
        var modifiers = configurationMode ? LetterModifiers : Modifiers;
        Register(ConfigurationId, 0x43, modifiers, "C", "ApplicationSwitchHotkey", ref _configurationRegistered, unavailable);
        if (configurationMode)
        {
            if (CopyScreen is not null)
                Register(CopyScreenId, 0x43, 0x4002, "Ctrl+C", "CopyScreenHotkey", ref _copyRegistered, unavailable);
            if (PasteScreen is not null)
                Register(PasteScreenId, 0x56, 0x4002, "Ctrl+V", "PasteScreenHotkey", ref _pasteRegistered, unavailable);
            Register(RestartId, 0x58, modifiers, "X", "RestartHotkey", ref _restartRegistered, unavailable);
            Register(BackgroundId, 0x42, modifiers, "B", "BackgroundHotkey", ref _backgroundRegistered, unavailable);
            if (ToggleScreenTransparency is not null)
                Register(TransparencyId, 0x4B, modifiers, "K", "ScreenTransparencyHotkey", ref _transparencyRegistered, unavailable);
            if (AdjustScreenBrightness is not null)
                for (int i = 0; i < BrightnessKeys.Length; i++)
                {
                    var chord = BrightnessKeys[i];
                    Register(chord.Id, chord.Key, chord.Modifiers, chord.Label, "ScreenBrightnessHotkey", ref _brightnessRegistered[i], unavailable);
                }
            if (ChoosePerspective is not null)
                Register(PerspectiveId, 0x50, modifiers, "P", "PerspectiveHotkey", ref _perspectiveRegistered, unavailable);
            if (ResetVideo is not null)
                Register(ResetVideoId, 0x52, 0x4002, "Ctrl+R", "ResetVideoHotkey", ref _resetVideoRegistered, unavailable);
            if (ZoomScreen is not null)
                for (int i = 0; i < ZoomKeys.Length; i++)
                {
                    var chord = ZoomKeys[i];
                    Register(chord.Id, chord.Key, chord.Modifiers, chord.Label, "ScreenZoomHotkey", ref _zoomRegistered[i], unavailable);
                }
            if (AddScreenPrefix is not null)
                Register(AddId, 0x41, modifiers, "A", "AddScreenHotkey", ref _addRegistered, unavailable);
            if (AddVideoScreen is not null)
                Register(VideoId, 0x56, modifiers, "V", "AddVideoScreenHotkey", ref _videoRegistered, unavailable);
            if (EditScreenName is not null)
                Register(NameId, 0x4E, modifiers, "N", "ScreenNameHotkey", ref _nameRegistered, unavailable);
            if (AddVisualizationScreen is not null)
                Register(VisualizationId, 0x47, modifiers, "G", "AddVisualizationScreenHotkey", ref _visualizationRegistered, unavailable);
            if (AddMatScreen is not null)
                Register(MatId, 0x4D, modifiers, "M", "AddMatScreenHotkey", ref _matRegistered, unavailable);
            if (AddLedScreen is not null)
                Register(LedId, 0x4C, modifiers, "L", "AddLedScreenHotkey", ref _ledRegistered, unavailable);
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
        if (message.Msg == 0x0312 && !_disposed && (isFocused?.Invoke() ?? PhotoBooth.Windows.ConfigurationShortcut.ApplicationFocused))
        {
            // Mode changes can leave old shortcut messages queued. Match the current chord.
            var data = message.LParam.ToInt64();
            var modifiers = (uint)(data & 0xFFFF);
            var key = (uint)((data >> 16) & 0xFFFF);
            if (_submenu != ConfigurationSubmenu.None &&
                (modifiers != 0 || !ConfigurationSubmenuChoices.Accepts(_submenu, (Forms.Keys)key))) return;
            if (message.WParam == CancelSubmenuId && key == 0x1B && modifiers == 0 && _cancelRegistered)
            { CancelSubmenu?.Invoke(); return; }
            BeforeShortcut?.Invoke();
            if (modifiers == 2 && message.WParam == ResetVideoId && key == 0x52 && _resetVideoRegistered)
            { ResetVideo?.Invoke(); return; }
            if (modifiers == 2 && message.WParam == CopyScreenId && key == 0x43 && _copyRegistered)
            {
                CopyScreen?.Invoke(); return;
            }
            if (modifiers == 2 && message.WParam == PasteScreenId && key == 0x56 && _pasteRegistered)
            {
                PasteScreen?.Invoke(); return;
            }
            for (int i = 0; i < ZoomKeys.Length; i++)
            {
                var chord = ZoomKeys[i];
                if (_zoomRegistered[i] && message.WParam == chord.Id && key == chord.Key && modifiers == (chord.Modifiers & 7))
                {
                    ZoomScreen?.Invoke(chord.Direction);
                    return;
                }
            }
            for (int i = 0; i < BrightnessKeys.Length; i++)
            {
                var chord = BrightnessKeys[i];
                if (_brightnessRegistered[i] && message.WParam == chord.Id && key == chord.Key && modifiers == (chord.Modifiers & 7))
                {
                    AdjustScreenBrightness?.Invoke(chord.Direction);
                    return;
                }
            }
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
            if (message.WParam == RestartId && key == 0x58 && _restartRegistered)
            {
                restart();
                return;
            }
            if (message.WParam == BackgroundId && key == 0x42 && _backgroundRegistered)
            {
                toggleBackground();
                return;
            }
            if (message.WParam == TransparencyId && key == 0x4B && _transparencyRegistered)
            {
                ToggleScreenTransparency?.Invoke();
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
            if (message.WParam == PerspectiveId && key == 0x50 && _perspectiveRegistered)
            {
                ChoosePerspective?.Invoke();
                return;
            }
            if (message.WParam == NameId && key == 0x4E && _nameRegistered)
            {
                EditScreenName?.Invoke();
                return;
            }
            if (message.WParam == VisualizationId && key == 0x47 && _visualizationRegistered)
            {
                AddVisualizationScreen?.Invoke();
                return;
            }
            if (message.WParam == MatId && key == 0x4D && _matRegistered)
            {
                AddMatScreen?.Invoke();
                return;
            }
            if (message.WParam == LedId && key == 0x4C && _ledRegistered) { AddLedScreen?.Invoke(); return; }
        }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _focusPoll.Stop();
        _focusPoll.Dispose();
        Unregister();
        if (Handle != nint.Zero)
            DestroyHandle();
    }

    private void Unregister()
    {
        if (_resetVideoRegistered) UnregisterHotKey(Handle, ResetVideoId);
        _resetVideoRegistered = false;
        if (_cancelRegistered) UnregisterHotKey(Handle, CancelSubmenuId);
        _cancelRegistered = false;
        if (_copyRegistered) UnregisterHotKey(Handle, CopyScreenId);
        if (_pasteRegistered) UnregisterHotKey(Handle, PasteScreenId);
        _copyRegistered = _pasteRegistered = false;
        for (int i = 0; i < ZoomKeys.Length; i++)
        {
            if (_zoomRegistered[i]) UnregisterHotKey(Handle, ZoomKeys[i].Id);
            _zoomRegistered[i] = false;
        }
        if (_ledRegistered) UnregisterHotKey(Handle, LedId);
        _ledRegistered = false;
        if (_visualizationRegistered) UnregisterHotKey(Handle, VisualizationId);
        _visualizationRegistered = false;
        if (_perspectiveRegistered)
        {
            UnregisterHotKey(Handle, PerspectiveId);
            _perspectiveRegistered = false;
        }
        for (int i = 0; i < BrightnessKeys.Length; i++)
        {
            if (_brightnessRegistered[i])
                UnregisterHotKey(Handle, BrightnessKeys[i].Id);
            _brightnessRegistered[i] = false;
        }
        if (_configurationRegistered)
            UnregisterHotKey(Handle, ConfigurationId);
        if (_restartRegistered)
            UnregisterHotKey(Handle, RestartId);
        if (_backgroundRegistered)
            UnregisterHotKey(Handle, BackgroundId);
        if (_addRegistered)
            UnregisterHotKey(Handle, AddId);
        if (_videoRegistered)
            UnregisterHotKey(Handle, VideoId);
        if (_nameRegistered)
            UnregisterHotKey(Handle, NameId);
        if (_matRegistered)
            UnregisterHotKey(Handle, MatId);
        if (_transparencyRegistered)
            UnregisterHotKey(Handle, TransparencyId);
        if (_whiteRegistered)
            UnregisterHotKey(Handle, WhiteBackgroundId);
        _whiteRegistered = false;
        _transparencyRegistered = false;
        _configurationRegistered = _restartRegistered = _backgroundRegistered = _addRegistered = _videoRegistered = _nameRegistered = _matRegistered = false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
