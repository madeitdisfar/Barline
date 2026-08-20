using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Barline.Platform;
using Barline.Ui;

namespace Barline.Shell;

/// <summary>
/// Shown once, immediately after the add-on is bought.
/// </summary>
/// <remarks>
/// The settings window applies its gating once at construction and only ever locks, so
/// a purchase cannot light the controls back up in place. That is a deliberate choice —
/// an unlock path would mean a second, almost never exercised branch behind every gated
/// control — and it leaves a restart as the honest answer. This window is where that is
/// asked for, and where it can actually be carried out rather than only requested.
/// </remarks>
internal partial class ThankYouWindow : Window
{
    private readonly Theme _theme;

    /// <param name="restored">
    /// True when the add-on turned out to be owned already, rather than having just
    /// been bought. Thanking somebody for a purchase they made months ago on another
    /// machine reads as a mistake, and it is the one thing that would make this window
    /// feel automated rather than meant.
    /// </param>
    public ThankYouWindow(Theme theme, bool restored = false)
    {
        _theme = theme;

        InitializeComponent();

        if (restored)
        {
            Heading.Text = "Welcome back";
            Body.Text = "Barline Plus is already unlocked on this account. Every "
                + "paid feature is yours, and the paid presets have been added to your "
                + "folder.";
        }

        LaterButton.Click += (_, _) => Close();
        RestartButton.Click += (_, _) => Restart();

        ApplyTheme();

        _theme.Changed += OnThemeChanged;
        Closed += (_, _) => _theme.Changed -= OnThemeChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyWindowChrome();
    }

    /// <summary>
    /// Restarts the app, so the settings window is rebuilt with the paid controls live.
    /// </summary>
    /// <remarks>
    /// Carried out by <see cref="AppRestart"/>, which the tray menu also uses. A refusal
    /// there means nothing was shut down, so the app is still on screen and the button
    /// has somewhere to report from.
    /// </remarks>
    private void Restart()
    {
        RestartButton.IsEnabled = false;
        RestartStatus.Visibility = Visibility.Collapsed;

        if (AppRestart.TryRestart()) return;

        Failed();
    }

    private void Failed()
    {
        RestartButton.IsEnabled = true;

        RestartStatus.Text = "Barline could not restart itself. Close it from the "
            + "notification area and open it again.";
        RestartStatus.Visibility = Visibility.Visible;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyTheme();
        ApplyWindowChrome();
    }

    /// <summary>
    /// Paints the title bar to match. Without it a dark window wears a light caption,
    /// which is the first thing that says an app is not part of the system.
    /// </summary>
    private void ApplyWindowChrome()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var caption = (_theme.WindowBackground as SolidColorBrush)?.Color ?? Colors.Black;
        TitleBarTheme.Apply(handle, _theme.IsLight, caption);
    }

    private void ApplyTheme()
    {
        Resources["WindowBackgroundBrush"] = _theme.WindowBackground;
        Resources["TextPrimaryBrush"] = _theme.TextPrimary;
        Resources["TextSecondaryBrush"] = _theme.TextSecondary;
        Resources["TextTertiaryBrush"] = _theme.TextTertiary;
        Resources["CardStrokeBrush"] = _theme.CardStroke;
        Resources["ControlAltFillBrush"] = _theme.ControlAltFill;
        Resources["AccentFillBrush"] = _theme.AccentFill;
        Resources["TextOnAccentBrush"] = _theme.TextOnAccent;
        Resources["SubtleHoverBrush"] = _theme.SubtleHover;
        Resources["SubtlePressedBrush"] = _theme.SubtlePressed;
    }
}
