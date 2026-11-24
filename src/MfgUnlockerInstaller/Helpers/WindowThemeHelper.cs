using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MfgUnlockerInstaller.Helpers;

/// <summary>
/// Applies native Windows 10/11 dark title bar styling to WPF windows.
/// </summary>
public static class WindowThemeHelper
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint hwnd,
        int attr,
        ref int attrValue,
        int attrSize);

    public static void EnableDarkTitleBar(Window window)
    {
        try
        {
            nint hwnd = new WindowInteropHelper(window).EnsureHandle();
            int enabled = 1;
            int hr = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
            if (hr != 0)
            {
                _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
            }
        }
        catch
        {
            // Ignore on unsupported OS versions.
        }
    }
}
