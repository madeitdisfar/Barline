using System.Runtime.InteropServices;
using System.Windows.Media;
using Barline.Diagnostics;

namespace Barline.Shell;

/// <summary>
/// Makes a normal WPF window's title bar follow the system theme.
/// </summary>
/// <remarks>
/// <para>
/// Without this a WPF window gets the legacy light title bar in dark mode, which is
/// the single most obvious sign that a window is not a modern Windows app — more
/// obvious than anything inside the client area.
/// </para>
/// <para>
/// Mica is deliberately not used. It needs a transparent window background, which
/// costs subpixel antialiasing on all the text in front of it; for a settings window
/// that is mostly text, a solid <c>SolidBackgroundFillColorBase</c> surface with a
/// matching caption reads as native and keeps the text crisp.
/// </para>
/// </remarks>
internal static class TitleBarTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Applies the dark-mode title bar and paints the caption to match the client
    /// area, so there is no seam across the top of the window.
    /// </summary>
    public static void Apply(IntPtr hwnd, bool isLight, Color caption)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            int useDark = isLight ? 0 : 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));

            // COLORREF is 0x00BBGGRR — the reverse of the usual RGB packing.
            int colorRef = caption.R | (caption.G << 8) | (caption.B << 16);
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref colorRef, sizeof(int));
        }
        catch (Exception ex)
        {
            // Both attributes are Windows 11-era. An older build simply keeps its
            // default title bar, which is cosmetic and not worth failing over.
            DebugLog.Write($"window chrome unavailable: {ex.Message}");
        }
    }
}
