using System.Runtime.InteropServices;
using System.Text;

namespace ExperienceX;

// Controls only a window of the process that Experience launched. No global keystrokes.
internal static class GForceWindow
{
    internal const string MainClass = "SoundSpectrum G-Force Standalone";
    internal const string FullscreenClass = "SSFullscreenWindowClass";
    internal static nint Find(int pid, string windowClass, bool includeHidden = false)
    {
        nint result = 0;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var process);
            if (process != (uint)pid || (!includeHidden && !IsWindowVisible(h)))
                return true;
            var name = new StringBuilder(128);
            GetClassName(h, name, name.Capacity);
            if (name.ToString() != windowClass)
                return true;
            result = h;
            return false;
        }, 0);
        return result;
    }
    internal static System.Drawing.Rectangle Bounds(nint window)
    {
        GetWindowRect(window, out var rect);
        return System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }
    internal static void ShowWithoutActivation(nint window) => ShowWindowAsync(window, 4);
    internal static void Hide(int pid) => EnumWindows((h, _) => { GetWindowThreadProcessId(h, out var p); if (p == (uint)pid) ShowWindowAsync(h, 0); return true; }, 0);
    internal static bool EnterFullscreen(nint window)
    {
        var original = new byte[256];
        if (!GetKeyboardState(original))
            return false;
        var caller = GetCurrentThreadId();
        var target = GetWindowThreadProcessId(window, out _);
        var attached = target != caller && AttachThreadInput(caller, target, true);
        if (target != caller && !attached)
            return false;
        try
        {
            var keys = (byte[])original.Clone();
            foreach (var key in new[] { 16, 17, 18, 160, 161, 162, 163, 164, 165 })
                keys[key] = 0;
            keys[16] = keys[160] = 0x80;
            if (!SetKeyboardState(keys))
                return false;
            return SendMessageTimeout(window, 0x102, 13, 0x001c0001, 3, 1000, out _) != 0;
        }
        finally
        {
            SetKeyboardState(original);
            if (attached)
                AttachThreadInput(caller, target, false);
            SetKeyboardState(original);
        }
    }
    internal static bool Place(nint window, int x, int y, int width, int height) => SetWindowPos(window, 0, x, y, width, height, 0x4014);
    internal static void Behind(nint window, nint surface)
    {
        if (GetWindow(window, 3) != surface)
            SetWindowPos(window, surface, 0, 0, 0, 0, 0x4013);
    }
    internal static bool IsHung(nint window) => IsHungAppWindow(window);
    internal static void Close(int pid)
    {
        EnumWindows((h, _) => { GetWindowThreadProcessId(h, out var p); if (p == (uint)pid) PostMessage(h, 0x10, 0, 0); return true; }, 0);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint h, int command);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint h, uint command);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(nint h);
    private delegate bool Callback(nint h, nint l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, nint l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint h, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("user32.dll")] private static extern bool GetKeyboardState([Out] byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeout(nint h, uint message, nuint w, nint l, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint h, uint message, nint w, nint l);
}
