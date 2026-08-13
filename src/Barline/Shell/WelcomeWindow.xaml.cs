using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Barline.Diagnostics;
using Barline.Platform;
using Barline.Settings;
using Barline.Ui;

namespace Barline.Shell;

/// <summary>
/// The one-time greeting. See the note in the XAML for what it is for.
/// </summary>
internal partial class WelcomeWindow : Window
{
    private readonly Theme _theme;
    private readonly BarColorResolver _bars;

    public WelcomeWindow(Theme theme, SettingsStore settings)
    {
        _theme = theme;

        // Its own resolver, for the same reason the settings preview has one: two
        // controls sharing an animated brush fight over it.
        _bars = new BarColorResolver(theme, settings);

        InitializeComponent();

        SampleArt.Source = DemoContent.CreateArt();
        SampleTitle.Text = "Everything In Its Right Place";
        SampleArtist.Text = "Radiohead";

        SampleBars.BarBrush = _bars.Brush;
        SampleBars.BarCount = settings.Current.VisualizerBarCount;

        // No LevelSource, so the bars run on their own decorative motion rather than
        // opening an audio capture for the sake of a picture.
        SampleBars.IsActive = true;

        // The override exists because the notice is invisible on any machine set up
        // the way this one asks you to set it up, so the branch could otherwise only
        // be looked at by changing a Windows setting to inspect a window.
        bool widgetsShowing =
            WidgetsButton.IsVisible() ||
            DevOverride.Read("BARLINE_WELCOME") == "widgets";

        if (widgetsShowing)
        {
            WidgetsNotice.Visibility = Visibility.Visible;
            TaskbarSettingsButton.Click += (_, _) => OpenTaskbarSettings();
        }

        CloseButton.Click += (_, _) => Close();

        ApplyTheme();
        _theme.Changed += OnThemeChanged;

        Closed += (_, _) =>
        {
            _theme.Changed -= OnThemeChanged;

            // Stops the render-loop subscription behind the animation. Left running,
            // it would keep a closed window's control ticking for the life of the app.
            SampleBars.IsActive = false;
        };
    }

    private void OpenTaskbarSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = WidgetsButton.SettingsUri,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            DebugLog.Write($"welcome: could not open taskbar settings: {ex.Message}");

            // The button is a shortcut, not the only route, so a failure demotes it to
            // the instructions rather than leaving a control that does nothing.
            TaskbarSettingsButton.IsEnabled = false;
            TaskbarSettingsButton.Content = "Settings, Personalization, Taskbar";
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
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

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyTheme();
        ApplyWindowChrome();
    }

    private void ApplyTheme()
    {
        Resources["WindowBackgroundBrush"] = _theme.WindowBackground;
        Resources["TextPrimaryBrush"] = _theme.TextPrimary;
        Resources["TextSecondaryBrush"] = _theme.TextSecondary;
        Resources["TextTertiaryBrush"] = _theme.TextTertiary;
        Resources["CardBackgroundBrush"] = _theme.CardBackground;
        Resources["CardStrokeBrush"] = _theme.CardStroke;
        Resources["ControlAltFillBrush"] = _theme.ControlAltFill;
        Resources["AccentFillBrush"] = _theme.AccentFill;
        Resources["TextOnAccentBrush"] = _theme.TextOnAccent;
        Resources["SubtleHoverBrush"] = _theme.SubtleHover;
        Resources["SubtlePressedBrush"] = _theme.SubtlePressed;

        // The sample sits on the shade the taskbar actually is, which is the same
        // value the bar contrast correction measures against. Anything else would
        // show colors the widget will not produce.
        var strip = new SolidColorBrush(_theme.BackdropEstimate);
        strip.Freeze();
        SampleStrip.Background = strip;

        // The widget draws its text over the taskbar, not over this window, so the
        // sample's own labels follow the taskbar's shade rather than the window's.
        var onStrip = _theme.IsLight ? Brushes.Black : Brushes.White;
        SampleTitle.Foreground = onStrip;
        SampleArtist.Foreground = onStrip.Clone();
        SampleArtist.Opacity = 0.72d;
    }
}
