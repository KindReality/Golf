using System.Runtime.InteropServices;

namespace PhotoBooth.Appearance;

internal static class DarkWindowChrome
{
    public static void Apply(nint handle)
    {
        int enabled = 1, caption = 0x19100B;
        // Older Windows versions may decline these attributes; the client area still uses our theme.
        DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
