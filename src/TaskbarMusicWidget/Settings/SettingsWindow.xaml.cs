using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarMusicWidget.Media;
using TaskbarMusicWidget.Shell;
using TaskbarMusicWidget.Startup;
using TaskbarMusicWidget.Ui;

namespace TaskbarMusicWidget.Settings;

/// <summary>
/// The settings window: a Windows 11 Settings-style page over
/// <see cref="WidgetSettings"/>.
/// </summary>
/// <remarks>
/// <para>
/// Changes apply and persist immediately, with no OK or Apply button, because that is
/// how Windows 11 Settings behaves — and because the widget is visible on the taskbar
/// the whole time this window is open, so every change is already previewed live where
/// it actually matters.
/// </para>
/// <para>
/// Each colour mode shows the colour it would really produce, resolved through the
/// same contrast correction the widget uses. That matters more here than anywhere
/// else: what the user gets is deliberately not what the artwork or the picker said,
/// and showing the picked colour instead of the drawn one would misrepresent the
/// feature.
/// </para>
/// </remarks>
internal partial class SettingsWindow : Window
{
    private readonly Theme _theme;
    private readonly SettingsStore _settings;
    private readonly AutoStartService _autoStart;
    private readonly IAlbumArtSource _albumArt;

    /// <summary>
    /// The window's own resolver, separate from the overlay's. They resolve to the
    /// same answer from the same store; keeping them separate avoids the preview and
    /// the widget sharing one animated brush and fighting over it.
    /// </summary>
    private readonly BarColorResolver _preview;

    private readonly Dictionary<VisualizerColorMode, RadioButton> _modeOptions;

    /// <summary>
    /// Set while the UI is being rebuilt from the settings, so the control events
    /// fired by that rebuild do not write back and re-enter.
    /// </summary>
    /// <remarks>
    /// Always set through <see cref="WithoutFeedback"/> rather than by hand. Leaving it
    /// to each caller to remember produced a real bug: selecting "Default" re-checked
    /// whichever palette swatch matched the stored custom colour, whose Checked handler
    /// then wrote the mode straight back to Custom — so the option could not be
    /// changed at all once a palette colour had been picked.
    /// </remarks>
    private bool _syncing;

    /// <summary>Hue count for the custom palette. 30-degree steps around the wheel.</summary>
    private const int SwatchCount = 12;

    public SettingsWindow(
        Theme theme,
        SettingsStore settings,
        AutoStartService autoStart,
        IAlbumArtSource albumArt)
    {
        _theme = theme;
        _settings = settings;
        _autoStart = autoStart;
        _albumArt = albumArt;
        _preview = new BarColorResolver(theme, settings);

        InitializeComponent();

        _modeOptions = new Dictionary<VisualizerColorMode, RadioButton>
        {
            [VisualizerColorMode.Default] = DefaultOption,
            [VisualizerColorMode.SystemAccent] = AccentOption,
            [VisualizerColorMode.AlbumArt] = AlbumArtOption,
            [VisualizerColorMode.Custom] = CustomOption,
        };

        foreach (var (mode, option) in _modeOptions)
            option.Checked += (_, _) => OnModeChosen(mode);

        VisualizerToggle.Checked += (_, _) => OnVisualizerToggled(true);
        VisualizerToggle.Unchecked += (_, _) => OnVisualizerToggled(false);

        AutoStartToggle.Checked += (_, _) => OnAutoStartToggled(true);
        AutoStartToggle.Unchecked += (_, _) => OnAutoStartToggled(false);

        HexInput.KeyDown += OnHexKeyDown;
        HexInput.LostKeyboardFocus += (_, _) => CommitHex();

        BuildSwatches();

        // The preview animates on its own decorative motion: no LevelSource is set,
        // so it never touches the audio capture just to show a colour.
        PreviewBars.BarBrush = _preview.Brush;
        PreviewBars.IsActive = true;

        // Just the folder: the full path wraps to three lines at this width and pushes
        // the last card off-screen, and the folder is what anyone would actually open.
        SettingsPathText.Text =
            $"Stored in {Path.GetDirectoryName(_settings.FilePath)}";

        _theme.Changed += OnThemeChanged;
        _settings.Changed += OnSettingsChanged;
        _albumArt.AlbumArtChanged += OnAlbumArtChanged;
        Closed += (_, _) =>
        {
            _theme.Changed -= OnThemeChanged;
            _settings.Changed -= OnSettingsChanged;
            _albumArt.AlbumArtChanged -= OnAlbumArtChanged;
        };

        ApplyTheme();
        SyncFromSettings();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyWindowChrome();
    }

    private void ApplyWindowChrome()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var caption = (_theme.WindowBackground as SolidColorBrush)?.Color ?? Colors.Black;
        TitleBarTheme.Apply(handle, _theme.IsLight, caption);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyTheme();
        ApplyWindowChrome();
        RefreshPreview();
    }

    /// <summary>
    /// A new track means a new album-art colour, so the swatch, the hex readout and
    /// the preview bars all have to be recomputed.
    /// </summary>
    private void OnAlbumArtChanged(object? sender, EventArgs e) => RefreshPreview();

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // Covers changes made from the tray menu while this window is open.
        if (_syncing) return;
        SyncFromSettings();
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
        Resources["ControlStrongStrokeBrush"] = _theme.ControlStrongStroke;
        Resources["AccentFillBrush"] = _theme.AccentFill;
        Resources["TextOnAccentBrush"] = _theme.TextOnAccent;
        Resources["SubtleHoverBrush"] = _theme.SubtleHover;
        Resources["SubtlePressedBrush"] = _theme.SubtlePressed;

        // The preview strip is painted with the very value the correction measures
        // against, so the contrast shown here is the contrast that was guaranteed.
        var strip = new SolidColorBrush(_theme.BackdropEstimate);
        strip.Freeze();
        PreviewStrip.Background = strip;
    }

    /// <summary>
    /// Runs a programmatic UI update with change events suppressed. Re-entrant: nested
    /// calls restore the previous state rather than clearing the flag outright.
    /// </summary>
    private void WithoutFeedback(Action update)
    {
        bool previous = _syncing;
        _syncing = true;
        try { update(); }
        finally { _syncing = previous; }
    }

    // ---- Reading the settings into the UI ----------------------------------

    private void SyncFromSettings()
    {
        WithoutFeedback(() =>
        {
            var current = _settings.Current;

            _modeOptions[current.VisualizerColor].IsChecked = true;
            VisualizerToggle.IsChecked = current.VisualizerEnabled;
            AutoStartToggle.IsChecked = _autoStart.IsEnabled;

            CustomCard.Visibility = current.VisualizerColor == VisualizerColorMode.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;

            HexInput.Text = current.CustomBarColor ?? string.Empty;
            HexError.Text = string.Empty;

            SyncSwatchSelection();
        });

        RefreshPreview();
    }

    /// <summary>
    /// Updates every mode's swatch and hex readout, and the live preview bars.
    /// </summary>
    private void RefreshPreview()
    {
        var art = _albumArt.CurrentAlbumArt;

        ShowResolved(VisualizerColorMode.Default, DefaultSwatch, DefaultHex, art);
        ShowResolved(VisualizerColorMode.SystemAccent, AccentSwatch, AccentHex, art);
        ShowResolved(VisualizerColorMode.AlbumArt, AlbumArtSwatch, AlbumArtHex, art);
        ShowResolved(VisualizerColorMode.Custom, CustomSwatch, CustomHex, art);

        _preview.Update(art);

        PreviewCaption.Text = _settings.Current.VisualizerColor == VisualizerColorMode.AlbumArt && art is null
            ? "Nothing is playing, so this falls back to the default color."
            : "Shown against the taskbar's approximate shade.";
    }

    private void ShowResolved(
        VisualizerColorMode mode, Border swatch, TextBlock hex, ImageSource? art)
    {
        var color = _preview.Preview(mode, art);

        // Swatches sit on the card, not on the taskbar, so a translucent bar colour
        // (the light-mode default is 53% black) is composited over the backdrop
        // estimate first — otherwise it would read against the wrong surface.
        var brush = new SolidColorBrush(Flatten(color, _theme.BackdropEstimate));
        brush.Freeze();
        swatch.Background = brush;

        hex.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    /// <summary>Composites a possibly-translucent colour over an opaque one.</summary>
    private static Color Flatten(Color color, Color over)
    {
        if (color.A == 0xFF) return color;

        double a = color.A / 255d;
        return Color.FromRgb(
            (byte)Math.Round(color.R * a + over.R * (1d - a)),
            (byte)Math.Round(color.G * a + over.G * (1d - a)),
            (byte)Math.Round(color.B * a + over.B * (1d - a)));
    }

    // ---- Writing the UI back to the settings -------------------------------

    private void OnModeChosen(VisualizerColorMode mode)
    {
        if (_syncing) return;

        // Choosing Custom with nothing set yet would draw the fallback and look
        // broken, so seed it from whatever the previous mode was actually painting.
        if (mode == VisualizerColorMode.Custom &&
            string.IsNullOrWhiteSpace(_settings.Current.CustomBarColor))
        {
            var seed = _preview.Preview(_settings.Current.VisualizerColor, _albumArt.CurrentAlbumArt);
            _settings.Update(s => s.CustomBarColor = $"#{seed.R:X2}{seed.G:X2}{seed.B:X2}");
        }

        WithoutFeedback(() =>
        {
            _settings.Update(s => s.VisualizerColor = mode);

            CustomCard.Visibility = mode == VisualizerColorMode.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;

            HexInput.Text = _settings.Current.CustomBarColor ?? string.Empty;
            SyncSwatchSelection();
        });

        RefreshPreview();
    }

    private void OnVisualizerToggled(bool enabled)
    {
        if (_syncing) return;
        WithoutFeedback(() => _settings.Update(s => s.VisualizerEnabled = enabled));
    }

    private void OnAutoStartToggled(bool enabled)
    {
        if (_syncing) return;

        // Autostart lives in the registry, not in settings.json — it is Windows' own
        // state and has to stay wherever Windows reads it from.
        _autoStart.SetEnabled(enabled);
    }

    // ---- Custom colour -----------------------------------------------------

    private void BuildSwatches()
    {
        for (int i = 0; i < SwatchCount; i++)
        {
            double hue = i * (360d / SwatchCount);

            // Mid lightness and high saturation: the swatch communicates a hue, and
            // the correction decides the lightness anyway, so showing anything else
            // would promise a colour the widget will not paint.
            var color = ColorMath.FromHsl(hue, 0.75d, 0.5d);
            var fill = new SolidColorBrush(color);
            fill.Freeze();

            string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

            var swatch = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                Background = fill,
                Tag = hex,
                ToolTip = hex,
            };

            swatch.Checked += (_, _) =>
            {
                if (_syncing) return;
                ApplyCustomColor(hex);
            };

            SwatchPanel.Children.Add(swatch);
        }
    }

    /// <summary>
    /// Points the palette selection at the stored custom colour. Guards itself, since
    /// checking a swatch fires the handler that writes the colour back.
    /// </summary>
    private void SyncSwatchSelection() => WithoutFeedback(() =>
    {
        string? current = _settings.Current.CustomBarColor;

        foreach (var child in SwatchPanel.Children)
        {
            if (child is not RadioButton swatch) continue;

            swatch.IsChecked = current is not null &&
                string.Equals((string?)swatch.Tag, current, StringComparison.OrdinalIgnoreCase);
        }
    });

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        CommitHex();
    }

    private void CommitHex()
    {
        if (_syncing) return;

        string text = HexInput.Text.Trim();
        if (text.Length == 0)
        {
            HexError.Text = string.Empty;
            return;
        }

        // Accept a bare hex triple as well as #RRGGBB, since typing the hash is easy
        // to forget and rejecting it teaches nothing.
        if (!text.StartsWith('#') && text.Length is 6 or 8 && IsHex(text))
            text = "#" + text;

        if (!TryParse(text, out var color))
        {
            HexError.Text = "Not a color.";
            return;
        }

        HexError.Text = string.Empty;
        ApplyCustomColor($"#{color.R:X2}{color.G:X2}{color.B:X2}");
    }

    private static bool IsHex(string text) =>
        text.All(Uri.IsHexDigit);

    private static bool TryParse(string text, out Color color)
    {
        try
        {
            if (ColorConverter.ConvertFromString(text) is Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch
        {
            // Malformed input is expected here; the caller reports it in the UI.
        }

        color = default;
        return false;
    }

    private void ApplyCustomColor(string hex)
    {
        WithoutFeedback(() =>
        {
            _settings.Update(s =>
            {
                s.CustomBarColor = hex;
                s.VisualizerColor = VisualizerColorMode.Custom;
            });

            HexInput.Text = hex;
            _modeOptions[VisualizerColorMode.Custom].IsChecked = true;
            CustomCard.Visibility = Visibility.Visible;
            SyncSwatchSelection();
        });

        RefreshPreview();
    }
}
