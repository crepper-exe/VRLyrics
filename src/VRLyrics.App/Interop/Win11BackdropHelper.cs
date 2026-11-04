using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VRLyrics.App.Interop;

/// <summary>
/// Applies Windows 11 native DWM attributes (dark title bar, rounded window corners,
/// and low-saturation charcoal frame colors) to any WPF <see cref="Window"/>.
/// Gracefully no-ops on older Windows builds.
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
    private static extern int DwmSetWindowAttribute(
        nint hwnd,
        int attr,
        ref int attrValue,
        int attrSize);

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

            // COLORREF is 0x00BBGGRR: #141619 -> R=0x14, G=0x16, B=0x19 -> 0x00191614
            var captionColor = 0x00191614;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

            // Subtle border #292D34 -> R=0x29, G=0x2D, B=0x34 -> 0x00342D29
            var borderColor = 0x00342D29;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));

            // Title text #E4E6EA -> R=0xE4, G=0xE6, B=0xEA -> 0x00EAE6E4
            var textColor = 0x00EAE6E4;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));
        }
        catch
        {
            // Ignore on systems that don't support Windows 11 DWM attributes
        }
    }
}
