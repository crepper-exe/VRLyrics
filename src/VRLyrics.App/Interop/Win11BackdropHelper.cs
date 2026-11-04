using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VRLyrics.App.Interop;

/// <summary>
/// Applies Windows 11 Fluent dark titlebar, rounded window corners, and custom caption color via <c>dwmapi.dll</c>.
/// Safely no-ops on older Windows builds.
/// </summary>
internal static class Win11BackdropHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

    public static void ApplyFluentDarkWindowFrame(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            if (hwnd == 0) return;

            var darkMode = 1;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            var roundCorners = DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref roundCorners, sizeof(int));

            // COLORREF is 0x00BBGGRR: #0B0F19 -> R=0x0B, G=0x0F, B=0x19 -> 0x00190F0B
            var captionColor = 0x00190F0B;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

            // Subtle border #23304A -> R=0x23, G=0x30, B=0x4A -> 0x004A3023
            var borderColor = 0x004A3023;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));

            // Title text #F1F5F9 -> R=0xF1, G=0xF5, B=0xF9 -> 0x00F9F5F1
            var textColor = 0x00F9F5F1;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));
        }
        catch
        {
            // Ignore on systems that don't support Windows 11 DWM attributes
        }
    }
}
