using System.Runtime.InteropServices;
using System.Windows.Media;
using TaskbarMusicWidget.Diagnostics;

namespace TaskbarMusicWidget.Shell;

/// <summary>
/// Puts Windows' own acrylic material behind a window.
/// </summary>
/// <remarks>
/// <para>
/// Lyrics float over whatever happens to be on screen, so their legibility cannot be
/// assumed the way it can against the taskbar. The obvious fix — sampling the desktop
/// behind the window and adapting — is both expensive per frame and self-referential,
/// since the window would capture itself.
/// </para>
/// <para>
/// Acrylic sidesteps the problem instead of solving it. The compositor blurs and tints
/// whatever is behind, on the GPU, for free, which bounds the luminance the text has
/// to survive to roughly the same range the taskbar occupies — so the existing
/// contrast correction applies unchanged, and the panel looks like a system surface
/// rather than a painted rectangle.
/// </para>
/// <para>
/// This is why the panel cannot use WPF's <c>AllowsTransparency</c>, which the overlay
/// does use: that forces a layered window, and DWM will not compose a backdrop
/// material behind one. The two windows are transparent by different mechanisms on
/// purpose.
/// </para>
/// </remarks>
internal static class SystemBackdrop
{
    private const int UseImmersiveDarkMode = 20;
    private const int WindowCornerPreference = 33;
    private const int SystemBackdropType = 38;

    /// <summary>DWMWCP_ROUND.</summary>
    private const int CornerRound = 2;

    /// <summary>DWMSBT_TRANSIENTWINDOW — acrylic, the material used for flyouts.</summary>
    private const int BackdropAcrylic = 3;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    /// <summary>
    /// Applies acrylic, rounded corners and the correct tint for the theme.
    /// </summary>
    /// <returns>
    /// False when the material is unavailable, which is the caller's cue to paint a
    /// solid background instead. The attribute is only honoured from Windows 11
    /// 22H2, and nothing about this feature is worth failing over.
    /// </returns>
    public static bool TryApply(IntPtr hwnd, bool isLight)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            // Negative margins extend the frame across the whole client area, which
            // is what lets the material show through it rather than only behind a
            // border. Without this the window paints opaque.
            var sheet = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref sheet);

            int dark = isLight ? 0 : 1;
            DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref dark, sizeof(int));

            int corner = CornerRound;
            DwmSetWindowAttribute(hwnd, WindowCornerPreference, ref corner, sizeof(int));

            int backdrop = BackdropAcrylic;
            int result = DwmSetWindowAttribute(hwnd, SystemBackdropType, ref backdrop, sizeof(int));

            if (result != 0)
            {
                DebugLog.Write($"acrylic unavailable (hr=0x{result:X8}); falling back to a solid panel");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"acrylic unavailable: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Colour to paint the window when <see cref="TryApply"/> failed.
    /// </summary>
    /// <remarks>
    /// Deliberately close to the backdrop estimate the contrast correction measures
    /// against, so text stays exactly as legible as it would have been on acrylic.
    /// </remarks>
    public static Color Fallback(Color backdropEstimate) =>
        Color.FromArgb(0xF2, backdropEstimate.R, backdropEstimate.G, backdropEstimate.B);
}
