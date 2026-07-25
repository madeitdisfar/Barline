using System.Windows.Media;
using Microsoft.Win32;
using TaskbarMusicWidget.Diagnostics;

namespace TaskbarMusicWidget.Ui;

/// <summary>
/// Windows 11 Fluent colour tokens, resolved against the live system theme.
/// </summary>
/// <remarks>
/// <para>
/// The literal ARGB values below are the WinUI <c>TextFillColor*</c> and
/// <c>SubtleFillColor*</c> resources. They are reproduced exactly rather than
/// approximated: text sitting on real taskbar material at the wrong opacity is
/// the most obvious tell that a widget is not part of the shell.
/// </para>
/// <para>
/// The system theme is read from the registry rather than
/// <c>UISettings.ColorValuesChanged</c>, whose notifications are unreliable in
/// desktop apps. <see cref="Refresh"/> is instead driven by the
/// <c>WM_SETTINGCHANGE</c> the overlay window already receives.
/// </para>
/// <para>
/// Note <c>SystemUsesLightTheme</c>, not <c>AppsUseLightTheme</c>: the widget sits
/// on the taskbar, which follows the system setting. They are independently
/// configurable and frequently differ.
/// </para>
/// </remarks>
internal sealed class Theme
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    public bool IsLight { get; private set; }

    public Brush TextPrimary { get; private set; } = Brushes.White;
    public Brush TextSecondary { get; private set; } = Brushes.White;
    public Brush TextTertiary { get; private set; } = Brushes.White;

    /// <summary>Hover fill for transport buttons (WinUI SubtleFillColorSecondary).</summary>
    public Brush SubtleHover { get; private set; } = Brushes.Transparent;

    /// <summary>Pressed fill for transport buttons (WinUI SubtleFillColorTertiary).</summary>
    public Brush SubtlePressed { get; private set; } = Brushes.Transparent;

    /// <summary>Fill shown behind album art before it loads, or when there is none.</summary>
    public Brush ArtPlaceholder { get; private set; } = Brushes.Transparent;

    /// <summary>
    /// Default visualiser bar colour. Its own token rather than the primary text
    /// colour: white bars read fine on the dark taskbar, but near-black bars on the
    /// light taskbar are the heaviest thing on screen, so light mode softens them to
    /// a medium grey.
    /// </summary>
    /// <remarks>
    /// A <see cref="Color"/> rather than a brush because <c>BarColorResolver</c> may
    /// need to animate to and from it, and because its alpha participates in that
    /// interpolation. Holding both a colour and a brush for one token would only
    /// invite the two to drift.
    /// </remarks>
    public Color BarDefault { get; private set; } = Colors.White;

    /// <summary>
    /// Approximate luminance of the taskbar material, for contrast checks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The real backdrop is Mica over the wallpaper, so no single value is correct —
    /// but a contrast floor needs something to measure against. These are pessimistic
    /// rather than typical: the dark value is lighter than a normal dark taskbar
    /// (~#1F1F1F) and the light value darker than a normal light one (~#F3F3F3).
    /// </para>
    /// <para>
    /// Pessimistic in the direction that matters. Bars are corrected away from the
    /// backdrop, so assuming the taskbar is closer to the bars than it usually is
    /// makes every real case clear the floor by more than asked, never less.
    /// </para>
    /// </remarks>
    public Color BackdropEstimate { get; private set; } = Color.FromRgb(0x2C, 0x2C, 0x2C);

    public Color Accent { get; private set; } = Color.FromRgb(0x00, 0x78, 0xD4);

    public event EventHandler? Changed;

    private bool _initialised;

    public Theme() => Refresh();

    /// <summary>
    /// Re-reads the system theme. Cheap to call often — WM_SETTINGCHANGE fires for
    /// many unrelated reasons, so this no-ops unless something actually changed.
    /// </summary>
    public void Refresh()
    {
        bool light = ReadIsLightTheme();
        var accent = ReadAccent();

        if (_initialised && light == IsLight && accent == Accent)
            return;

        _initialised = true;
        IsLight = light;
        Accent = accent;

        if (light)
        {
            TextPrimary = Frozen(0xE4, 0x00, 0x00, 0x00);
            // Darker than the WinUI secondary token (0x9E): on the light taskbar
            // material that standard value reads washed-out next to the title.
            TextSecondary = Frozen(0xB8, 0x00, 0x00, 0x00);
            TextTertiary = Frozen(0x72, 0x00, 0x00, 0x00);
            SubtleHover = Frozen(0x09, 0x00, 0x00, 0x00);
            SubtlePressed = Frozen(0x06, 0x00, 0x00, 0x00);
            ArtPlaceholder = Frozen(0x0F, 0x00, 0x00, 0x00);
            // Softer than the near-black text so the bars stay a quiet accent.
            BarDefault = Color.FromArgb(0x87, 0x00, 0x00, 0x00);
            BackdropEstimate = Color.FromRgb(0xE6, 0xE6, 0xE6);
        }
        else
        {
            TextPrimary = Frozen(0xFF, 0xFF, 0xFF, 0xFF);
            TextSecondary = Frozen(0xC5, 0xFF, 0xFF, 0xFF);
            TextTertiary = Frozen(0x87, 0xFF, 0xFF, 0xFF);
            SubtleHover = Frozen(0x0F, 0xFF, 0xFF, 0xFF);
            SubtlePressed = Frozen(0x0A, 0xFF, 0xFF, 0xFF);
            ArtPlaceholder = Frozen(0x14, 0xFF, 0xFF, 0xFF);
            BarDefault = Colors.White;
            BackdropEstimate = Color.FromRgb(0x2C, 0x2C, 0x2C);
        }

        DebugLog.Write($"theme: {(light ? "light" : "dark")} accent=#{Accent.R:X2}{Accent.G:X2}{Accent.B:X2}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static bool ReadIsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;   // dark is the Windows 11 default
        }
    }

    private static Color ReadAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DwmKey);
            if (key?.GetValue("AccentColor") is int raw)
            {
                // DWM stores accent as 0xAABBGGRR — byte order is reversed
                // relative to the usual ARGB layout.
                uint v = unchecked((uint)raw);
                return Color.FromArgb(
                    (byte)((v >> 24) & 0xFF),
                    (byte)(v & 0xFF),
                    (byte)((v >> 8) & 0xFF),
                    (byte)((v >> 16) & 0xFF));
            }
        }
        catch { /* fall through to the default */ }

        return Color.FromRgb(0x00, 0x78, 0xD4);
    }
}
