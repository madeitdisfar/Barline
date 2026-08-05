using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using TaskbarMusicWidget.Lyrics;
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
    private readonly MediaSessionService _media;
    private readonly LyricsService _lyrics;

    /// <summary>
    /// The window's own resolver, separate from the overlay's. They resolve to the
    /// same answer from the same store; keeping them separate avoids the preview and
    /// the widget sharing one animated brush and fighting over it.
    /// </summary>
    private readonly BarColorResolver _preview;

    private readonly Dictionary<VisualizerColorMode, RadioButton> _modeOptions;

    /// <summary>
    /// The bar counts on offer, keyed by count.
    /// </summary>
    /// <remarks>
    /// Keyed off the range constants rather than literals, so a count the range
    /// allows but the window has no segment for fails here instead of leaving a
    /// setting the user cannot reach. The range is asserted to be exactly these
    /// three by the tests.
    /// </remarks>
    private readonly Dictionary<int, RadioButton> _barCountOptions;

    private readonly Dictionary<LyricsDisplayMode, RadioButton> _lyricsDisplayOptions;

    private readonly Dictionary<LyricsHoverBehavior, RadioButton> _hoverOptions;
    private readonly Dictionary<LyricsEffect, RadioButton> _effectOptions = [];
    private readonly Dictionary<LyricsBackground, RadioButton> _backgroundOptions = [];
    private readonly Dictionary<string, RadioButton> _weightOptions = [];

    private readonly LyricsPresetStore _presets = new();

    /// <summary>
    /// The appearance the card is editing: the panel's or the inline one, whichever is
    /// in use. Editing the look you cannot currently see would be its own bug.
    /// </summary>
    private LyricsAppearance Editing => _settings.Current.LyricsDisplay == LyricsDisplayMode.Panel
        ? _settings.Current.PanelAppearance
        : _settings.Current.InlineAppearance;

    private readonly Dictionary<LyricsPanelPosition, RadioButton> _lyricsPositionOptions;

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
        IAlbumArtSource albumArt,
        MediaSessionService media,
        LyricsService lyrics)
    {
        _theme = theme;
        _settings = settings;
        _autoStart = autoStart;
        _albumArt = albumArt;
        _media = media;
        _lyrics = lyrics;
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

        _barCountOptions = new Dictionary<int, RadioButton>
        {
            [WidgetSettings.MinBarCount] = SimpleOption,
            [WidgetSettings.MinBarCount + 1] = BalancedOption,
            [WidgetSettings.MaxBarCount] = DetailedOption,
        };

        foreach (var (count, option) in _barCountOptions)
            option.Checked += (_, _) => OnBarCountChosen(count);

        VisualizerToggle.Checked += (_, _) => OnVisualizerToggled(true);
        VisualizerToggle.Unchecked += (_, _) => OnVisualizerToggled(false);

        LyricsToggle.Checked += (_, _) => OnLyricsToggled(true);
        LyricsToggle.Unchecked += (_, _) => OnLyricsToggled(false);

        _lyricsDisplayOptions = new Dictionary<LyricsDisplayMode, RadioButton>
        {
            [LyricsDisplayMode.Inline] = InlineOption,
            [LyricsDisplayMode.Panel] = PanelOption,
        };

        foreach (var (mode, option) in _lyricsDisplayOptions)
            option.Checked += (_, _) => OnLyricsDisplayChosen(mode);

        _hoverOptions = new Dictionary<LyricsHoverBehavior, RadioButton>
        {
            [LyricsHoverBehavior.None] = HoverNoneOption,
            [LyricsHoverBehavior.Fade] = HoverFadeOption,
            [LyricsHoverBehavior.Hide] = HoverHideOption,
        };

        foreach (var (behaviour, option) in _hoverOptions)
            option.Checked += (_, _) => Mutate(s => s.LyricsHover = behaviour);

        _lyricsPositionOptions = new Dictionary<LyricsPanelPosition, RadioButton>
        {
            [LyricsPanelPosition.AboveWidget] = AboveWidgetOption,
            [LyricsPanelPosition.BottomCenter] = BottomCenterOption,
            [LyricsPanelPosition.TopCenter] = TopCenterOption,
            [LyricsPanelPosition.Custom] = CustomPositionOption,
        };

        foreach (var (position, option) in _lyricsPositionOptions)
            option.Checked += (_, _) => OnLyricsPositionChosen(position);

        WordByWordOption.Checked += (_, _) => OnHighlightChosen(wordByWord: true);
        LineAtATimeOption.Checked += (_, _) => OnHighlightChosen(wordByWord: false);

        // Suppressed while wiring up: setting a slider's Minimum coerces its Value,
        // which raises ValueChanged before the controls have been filled from the
        // settings — and that would write the coerced value straight back over them.
        WithoutFeedback(() =>
        {
            ConfigureSizeSlider(
                PanelWidthSlider, PanelWidthText,
                WidgetSettings.MinPanelWidth, WidgetSettings.MaxPanelWidth,
                value => _settings.Update(s => s.LyricsPanelWidth = value));

            ConfigureSizeSlider(
                PanelHeightSlider, PanelHeightText,
                WidgetSettings.MinPanelHeight, WidgetSettings.MaxPanelHeight,
                value => _settings.Update(s => s.LyricsPanelHeight = value));

            ConfigureAppearanceSlider(
                CustomXSlider, CustomXText, 0d, 100d, 1d,
                value => Mutate(s => s.LyricsCustomX = value),
                value => $"{value:F0}%");

            ConfigureAppearanceSlider(
                CustomYSlider, CustomYText, 0d, 100d, 1d,
                value => Mutate(s => s.LyricsCustomY = value),
                value => $"{value:F0}%");

            BuildAppearanceControls();
        });

        ImportButton.Click += (_, _) => ImportLyricsFile();
        OpenFolderButton.Click += (_, _) => OpenLyricsFolder();
        ClearCacheButton.Click += (_, _) => ClearLyricsCache();

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

        // SMTC resolves asynchronously, so at construction there is usually no track
        // yet even when something is playing. Without this the import card would sit
        // on "play something" for the whole session.
        _media.TrackChanged += OnMediaTrackChanged;

        Closed += (_, _) =>
        {
            _theme.Changed -= OnThemeChanged;
            _settings.Changed -= OnSettingsChanged;
            _albumArt.AlbumArtChanged -= OnAlbumArtChanged;
            _media.TrackChanged -= OnMediaTrackChanged;
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

    /// <summary>
    /// A new track changes which file an import would be filed as, and whether one
    /// already exists for it.
    /// </summary>
    private void OnMediaTrackChanged(object? sender, TrackInfo? track)
    {
        UpdateImportDescription();
        ImportStatus.Text = string.Empty;
    }

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
            _barCountOptions[current.VisualizerBarCount].IsChecked = true;
            VisualizerToggle.IsChecked = current.VisualizerEnabled;
            LyricsToggle.IsChecked = current.LyricsEnabled;
            _lyricsDisplayOptions[current.LyricsDisplay].IsChecked = true;
            _lyricsPositionOptions[current.LyricsPosition].IsChecked = true;
            _hoverOptions[current.LyricsHover].IsChecked = true;

            if (current.LyricsWordByWord) WordByWordOption.IsChecked = true;
            else LineAtATimeOption.IsChecked = true;

            PanelWidthSlider.Value = current.LyricsPanelWidth;
            PanelHeightSlider.Value = current.LyricsPanelHeight;
            PanelWidthText.Text = $"{current.LyricsPanelWidth}";
            PanelHeightText.Text = $"{current.LyricsPanelHeight}";

            CustomXSlider.Value = current.LyricsCustomX;
            CustomYSlider.Value = current.LyricsCustomY;
            UpdateCustomPositionVisibility();

            // Where to put lyrics is only a question once there are any.
            var lyricsVisibility = current.LyricsEnabled
                ? Visibility.Visible
                : Visibility.Collapsed;

            LyricsPlacementCard.Visibility = lyricsVisibility;
            LyricsImportCard.Visibility = lyricsVisibility;
            LyricsAppearanceCard.Visibility = lyricsVisibility;

            UpdateStyleRowVisibility();
            UpdateImportDescription();
            UpdateCacheSize();
            SyncAppearance();
            AutoStartToggle.IsChecked = _autoStart.IsEnabled;

            PreviewBars.BarCount = current.VisualizerBarCount;

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

    /// <summary>
    /// Applies a new bar count. The preview follows immediately; the widget itself
    /// picks it up from the store's change notification.
    /// </summary>
    private void OnBarCountChosen(int count)
    {
        if (_syncing) return;

        WithoutFeedback(() =>
        {
            _settings.Update(s => s.VisualizerBarCount = count);
            PreviewBars.BarCount = count;
        });
    }

    private void OnVisualizerToggled(bool enabled)
    {
        if (_syncing) return;
        WithoutFeedback(() => _settings.Update(s => s.VisualizerEnabled = enabled));
    }

    private void OnLyricsToggled(bool enabled)
    {
        if (_syncing) return;

        WithoutFeedback(() =>
        {
            _settings.Update(s => s.LyricsEnabled = enabled);

            LyricsPlacementCard.Visibility = enabled
                ? Visibility.Visible
                : Visibility.Collapsed;
        });
    }

    private void OnLyricsDisplayChosen(LyricsDisplayMode mode)
    {
        if (_syncing) return;

        WithoutFeedback(() =>
        {
            _settings.Update(s => s.LyricsDisplay = mode);
            UpdateStyleRowVisibility();
        });

        // The card now edits the other appearance, so it has to be rebuilt.
        SyncAppearance();
    }

    /// <summary>Applies a settings change unless the UI is being rebuilt from them.</summary>
    private void Mutate(Action<WidgetSettings> change)
    {
        if (_syncing) return;
        WithoutFeedback(() => _settings.Update(change));
    }

    /// <summary>Applies a change to whichever appearance is being edited.</summary>
    private void MutateAppearance(Action<LyricsAppearance> change)
    {
        if (_syncing) return;

        Mutate(s =>
        {
            var appearance = s.LyricsDisplay == LyricsDisplayMode.Panel
                ? s.PanelAppearance
                : s.InlineAppearance;

            change(appearance);

            // Any edit makes this no longer the preset it came from.
            appearance.Name = CustomPresetName;
            appearance.Normalize();
        });

        // The picker has to say so. Only the selection is touched, not the whole card
        // — rebuilding it here would re-enter every control's change handler.
        RefreshPresetSelection();
        UpdateAppearanceRowVisibility();
    }

    private const string CustomPresetName = "Custom";

    /// <summary>
    /// Points the preset picker at whatever the appearance is now called.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SyncAppearance"/> on purpose. That rebuilds every
    /// control, and doing so from inside an edit would fire their handlers again;
    /// this touches one property.
    /// </remarks>
    private void RefreshPresetSelection() => WithoutFeedback(() =>
    {
        string name = Editing.Name;

        if (!PresetPicker.Items.Contains(name)) PresetPicker.Items.Add(name);
        PresetPicker.SelectedItem = name;
    });

    /// <summary>
    /// Hides the controls that a given choice makes meaningless — an effect radius
    /// with no effect, an opacity for an opaque fill, a corner radius with nothing to
    /// round.
    /// </summary>
    private void UpdateAppearanceRowVisibility()
    {
        var appearance = Editing;

        var effect = appearance.Effect == LyricsEffect.None
            ? Visibility.Collapsed
            : Visibility.Visible;

        EffectRadiusSlider.Visibility = effect;
        EffectRadiusText.Visibility = effect;

        // Only a tint has an opacity worth setting: solid is opaque by definition and
        // none has nothing to be opaque about.
        var opacity = appearance.Background == LyricsBackground.Tinted
            ? Visibility.Visible
            : Visibility.Collapsed;

        BackgroundOpacitySlider.Visibility = opacity;
        BackgroundOpacityText.Visibility = opacity;

        var surface = appearance.Background == LyricsBackground.None
            ? Visibility.Collapsed
            : Visibility.Visible;

        BackgroundColorRow.Visibility = surface;
        CornerRadiusRow.Visibility = surface;
    }

    // ---- Appearance controls ------------------------------------------------

    private void BuildAppearanceControls()
    {
        _weightOptions[nameof(FontWeights.Normal)] = WeightNormalOption;
        _weightOptions["SemiBold"] = WeightSemiBoldOption;
        _weightOptions[nameof(FontWeights.Bold)] = WeightBoldOption;

        foreach (var (weight, option) in _weightOptions)
            option.Checked += (_, _) => MutateAppearance(a => a.FontWeight = weight);

        _effectOptions[LyricsEffect.None] = EffectNoneOption;
        _effectOptions[LyricsEffect.Glow] = EffectGlowOption;

        foreach (var (effect, option) in _effectOptions)
            option.Checked += (_, _) => MutateAppearance(a => a.Effect = effect);

        _backgroundOptions[LyricsBackground.Tinted] = BackgroundTintedOption;
        _backgroundOptions[LyricsBackground.Solid] = BackgroundSolidOption;
        _backgroundOptions[LyricsBackground.None] = BackgroundNoneOption;

        foreach (var (background, option) in _backgroundOptions)
            option.Checked += (_, _) => MutateAppearance(a => a.Background = background);

        LowercaseToggle.Checked += (_, _) => MutateAppearance(a => a.Lowercase = true);
        LowercaseToggle.Unchecked += (_, _) => MutateAppearance(a => a.Lowercase = false);

        ItalicToggle.Checked += (_, _) => MutateAppearance(a => a.Italic = true);
        ItalicToggle.Unchecked += (_, _) => MutateAppearance(a => a.Italic = false);

        // Only families that are actually installed, so a picked font always renders.
        foreach (string family in InstalledFonts())
            FontPicker.Items.Add(family);

        FontPicker.SelectionChanged += (_, _) =>
        {
            if (FontPicker.SelectedItem is string family)
                MutateAppearance(a => a.FontFamily = family);
        };

        PresetPicker.SelectionChanged += (_, _) => LoadSelectedPreset();
        SavePresetButton.Click += (_, _) => SaveCurrentAsPreset();

        ConfigureAppearanceSlider(
            FontSizeSlider, FontSizeText, LyricsAppearance.MinFontSize, LyricsAppearance.MaxFontSize, 1d,
            value => MutateAppearance(a => a.FontSize = value),
            value => $"{value:F0}px");

        ConfigureAppearanceSlider(
            UnsungSlider, UnsungText, 0d, 1d, 0.02d,
            value => MutateAppearance(a => a.UnsungOpacity = value),
            value => $"{value * 100d:F0}%");

        ConfigureAppearanceSlider(
            EffectRadiusSlider, EffectRadiusText, 0d, 40d, 1d,
            value => MutateAppearance(a => a.EffectRadius = value),
            value => $"{value:F0}px");

        ConfigureAppearanceSlider(
            BackgroundOpacitySlider, BackgroundOpacityText, 0d, 1d, 0.02d,
            value => MutateAppearance(a => a.BackgroundOpacity = value),
            value => $"{value * 100d:F0}%");

        ConfigureAppearanceSlider(
            CornerRadiusSlider, CornerRadiusText, 0d, LyricsAppearance.MaxCornerRadius, 1d,
            value => MutateAppearance(a => a.CornerRadius = value),
            value => $"{value:F0}px");

        BuildColorPalette();

        TextColorSwatch.Click += (_, _) => OpenColorPalette(TextColorSwatch, isText: true);
        BackgroundSwatch.Click += (_, _) => OpenColorPalette(BackgroundSwatch, isText: false);

        TextColorInput.LostKeyboardFocus += (_, _) => CommitColor(TextColorInput, isText: true);
        TextColorInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) CommitColor(TextColorInput, isText: true);
        };

        BackgroundColorInput.LostKeyboardFocus += (_, _) => CommitColor(BackgroundColorInput, isText: false);
        BackgroundColorInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) CommitColor(BackgroundColorInput, isText: false);
        };
    }

    /// <summary>
    /// Families installed on this machine, deduplicated and sorted.
    /// </summary>
    /// <remarks>
    /// Offering a free-text font name would let a preset name something that is not
    /// there, and the fallback would silently render a different face.
    /// </remarks>
    private static IEnumerable<string> InstalledFonts() =>
        Fonts.SystemFontFamilies
            .Select(family => family.Source)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase);

    private void ConfigureAppearanceSlider(
        Slider slider,
        TextBlock readout,
        double minimum,
        double maximum,
        double step,
        Action<double> apply,
        Func<double, string> format)
    {
        slider.Minimum = minimum;
        slider.Maximum = maximum;
        slider.SmallChange = step;
        slider.LargeChange = step * 5d;
        slider.TickFrequency = step;
        slider.IsSnapToTickEnabled = true;

        slider.ValueChanged += (_, e) =>
        {
            readout.Text = format(e.NewValue);
            apply(e.NewValue);
        };
    }

    private void CommitColor(TextBox input, bool isText)
    {
        if (_syncing) return;

        var fallback = isText ? Colors.White : Color.FromRgb(0x2C, 0x2C, 0x2C);
        var parsed = LyricsTypography.Parse(input.Text, fallback);
        string hex = LyricsTypography.ToHex(parsed);

        MutateAppearance(a =>
        {
            if (isText) a.TextColor = hex;
            else a.BackgroundColor = hex;
        });

        WithoutFeedback(() => input.Text = hex);
        UpdateColorSwatches();
    }

    private void UpdateColorSwatches()
    {
        var appearance = Editing;

        TextColorSwatch.Background =
            new SolidColorBrush(LyricsTypography.TextColor(appearance));

        // Shown at the opacity it will actually be drawn with, over the chequerboard
        // in the swatch template — otherwise a 20% tint looks like a solid dark colour.
        var background = LyricsTypography.Parse(appearance.BackgroundColor, Color.FromRgb(0x2C, 0x2C, 0x2C));
        byte alpha = appearance.Background == LyricsBackground.Solid
            ? (byte)0xFF
            : (byte)Math.Round(255d * Math.Clamp(appearance.BackgroundOpacity, 0d, 1d));

        BackgroundSwatch.Background =
            new SolidColorBrush(Color.FromArgb(alpha, background.R, background.G, background.B));
    }

    // ---- Colour palette ----------------------------------------------------

    /// <summary>Which well the palette is currently editing.</summary>
    private bool _pickingTextColor;

    /// <summary>
    /// A fixed palette: twelve hues at two lightnesses, then a greyscale ramp.
    /// </summary>
    /// <remarks>
    /// Typing hex is exact but nobody wants to do it to try three shades. This is not
    /// a full colour picker — a hue/saturation surface would be a control to build and
    /// maintain, and the hex box already covers anything the palette misses.
    /// </remarks>
    private void BuildColorPalette()
    {
        const int hues = 12;

        foreach (double lightness in new[] { 0.62d, 0.42d })
        {
            for (int i = 0; i < hues; i++)
                ColorPalette.Children.Add(
                    SwatchFor(ColorMath.FromHsl(i * (360d / hues), 0.72d, lightness)));
        }

        foreach (double level in new[] { 1d, 0.85d, 0.7d, 0.55d, 0.4d, 0.28d, 0.16d, 0.06d })
        {
            byte v = (byte)Math.Round(255d * level);
            ColorPalette.Children.Add(SwatchFor(Color.FromRgb(v, v, v)));
        }
    }

    private Button SwatchFor(Color color)
    {
        var fill = new SolidColorBrush(color);
        fill.Freeze();

        var button = new Button
        {
            Style = (Style)FindResource("SwatchButton"),
            Width = 26,
            Height = 26,
            Margin = new Thickness(0, 0, 4, 4),
            Background = fill,
            ToolTip = LyricsTypography.ToHex(color),
        };

        button.Click += (_, _) =>
        {
            string hex = LyricsTypography.ToHex(color);

            MutateAppearance(a =>
            {
                if (_pickingTextColor) a.TextColor = hex;
                else a.BackgroundColor = hex;
            });

            WithoutFeedback(() =>
            {
                if (_pickingTextColor) TextColorInput.Text = hex;
                else BackgroundColorInput.Text = hex;
            });

            UpdateColorSwatches();
            ColorPopup.IsOpen = false;
        };

        return button;
    }

    private void OpenColorPalette(UIElement target, bool isText)
    {
        _pickingTextColor = isText;
        ColorPopup.PlacementTarget = target;
        ColorPopup.IsOpen = true;
    }

    /// <summary>Rebuilds the appearance card from the appearance being edited.</summary>
    /// <remarks>
    /// Suppressed throughout. Every assignment below raises a change event, and those
    /// handlers stamp the appearance as "Custom" — so filling the card from a preset
    /// immediately un-named it, and the picker snapped back to Custom the instant a
    /// preset was chosen.
    /// </remarks>
    private void SyncAppearance() => WithoutFeedback(SyncAppearanceCore);

    private void SyncAppearanceCore()
    {
        var appearance = Editing;
        bool isPanel = _settings.Current.LyricsDisplay == LyricsDisplayMode.Panel;

        AppearanceScopeText.Text = isPanel
            ? "Applies to the floating panel. Presets are saved copies of these values, kept as files you can share."
            : "Applies to the lyric shown in the widget. It has no background of its own — the taskbar's material shows through, which is the point of the widget.";

        SurfaceSettings.Visibility = isPanel ? Visibility.Visible : Visibility.Collapsed;

        _weightOptions.GetValueOrDefault(appearance.FontWeight, WeightSemiBoldOption).IsChecked = true;
        _effectOptions[appearance.Effect].IsChecked = true;
        _backgroundOptions[appearance.Background].IsChecked = true;
        LowercaseToggle.IsChecked = appearance.Lowercase;
        ItalicToggle.IsChecked = appearance.Italic;

        // A preset can name a family Windows does not report as its own — "Arial
        // Narrow" is a face of Arial, not a family — and it can also name one that is
        // simply not installed here. Either way the box would otherwise sit blank,
        // hiding the setting rather than showing it is unusual.
        if (!FontPicker.Items.Contains(appearance.FontFamily))
            FontPicker.Items.Add(appearance.FontFamily);

        FontPicker.SelectedItem = appearance.FontFamily;
        FontSizeSlider.Value = appearance.FontSize;
        UnsungSlider.Value = appearance.UnsungOpacity;
        EffectRadiusSlider.Value = appearance.EffectRadius;
        BackgroundOpacitySlider.Value = appearance.BackgroundOpacity;
        CornerRadiusSlider.Value = appearance.CornerRadius;

        TextColorInput.Text = appearance.TextColor;
        BackgroundColorInput.Text = appearance.BackgroundColor;

        UpdateColorSwatches();
        UpdateAppearanceRowVisibility();
        SyncPresetList(appearance.Name);
    }

    // ---- Presets -----------------------------------------------------------

    private void SyncPresetList(string current)
    {
        PresetPicker.Items.Clear();

        foreach (string name in _presets.Names())
            PresetPicker.Items.Add(name);

        // "Custom" is not a file — it is what the appearance becomes once edited, and
        // it has to be selectable or the picker would show a preset that no longer
        // describes what is on screen.
        if (!PresetPicker.Items.Contains(current)) PresetPicker.Items.Add(current);

        PresetPicker.SelectedItem = current;
    }

    private void LoadSelectedPreset()
    {
        if (_syncing) return;
        if (PresetPicker.SelectedItem is not string name) return;
        if (name == Editing.Name) return;

        var preset = _presets.Load(name);
        if (preset is null)
        {
            PresetStatus.Text = $"Could not read the preset “{name}”.";
            return;
        }

        bool isPanel = _settings.Current.LyricsDisplay == LyricsDisplayMode.Panel;

        Mutate(s =>
        {
            var loaded = preset.Clone();

            // The inline display never takes a surface, whatever the preset says.
            if (!isPanel) loaded.Background = LyricsBackground.None;

            if (isPanel) s.PanelAppearance = loaded;
            else s.InlineAppearance = loaded;
        });

        PresetStatus.Text = $"Loaded “{name}”.";
        SyncAppearance();
    }

    private void SaveCurrentAsPreset()
    {
        var picker = new SaveFileDialog
        {
            Title = "Save lyrics preset",
            Filter = "Lyrics preset (*.json)|*.json",
            InitialDirectory = _presets.DirectoryPath,
            FileName = $"{Editing.Name}.json",
        };

        if (picker.ShowDialog(this) != true) return;

        var preset = Editing.Clone();
        preset.Name = LyricsPresetStore.SanitizeName(
            Path.GetFileNameWithoutExtension(picker.FileName));

        // Saved through the store rather than to the chosen path, so a preset always
        // lands where the picker can find it again.
        if (!_presets.Write(preset))
        {
            PresetStatus.Text = "Could not save the preset.";
            return;
        }

        Mutate(s =>
        {
            if (s.LyricsDisplay == LyricsDisplayMode.Panel) s.PanelAppearance.Name = preset.Name;
            else s.InlineAppearance.Name = preset.Name;
        });

        PresetStatus.Text = $"Saved “{preset.Name}”.";
        SyncAppearance();
    }

    private void OnLyricsPositionChosen(LyricsPanelPosition position)
    {
        if (_syncing) return;

        WithoutFeedback(() =>
        {
            _settings.Update(s => s.LyricsPosition = position);
            UpdateCustomPositionVisibility();
        });
    }

    /// <summary>
    /// The exact-position sliders only mean anything for the free anchor.
    /// </summary>
    private void UpdateCustomPositionVisibility() =>
        CustomPositionRow.Visibility =
            _settings.Current.LyricsPosition == LyricsPanelPosition.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;

    private void OnHighlightChosen(bool wordByWord)
    {
        if (_syncing) return;
        WithoutFeedback(() => _settings.Update(s => s.LyricsWordByWord = wordByWord));
    }

    /// <summary>
    /// Wires a size slider to its setting and its readout.
    /// </summary>
    /// <remarks>
    /// Stepped in eights so dragging lands on round numbers, and written back on every
    /// change — the store already writes atomically, and a drag is a handful of saves
    /// rather than a stream of them because the step quantises it.
    /// </remarks>
    private void ConfigureSizeSlider(
        Slider slider,
        TextBlock readout,
        int minimum,
        int maximum,
        Action<int> apply)
    {
        slider.Minimum = minimum;
        slider.Maximum = maximum;
        slider.SmallChange = 8d;
        slider.LargeChange = 40d;
        slider.TickFrequency = 8d;
        slider.IsSnapToTickEnabled = true;

        slider.ValueChanged += (_, e) =>
        {
            int value = (int)Math.Round(e.NewValue);
            readout.Text = $"{value}";

            if (_syncing) return;
            WithoutFeedback(() => apply(value));
        };
    }

    /// <summary>
    /// Style, position, size and highlight only apply to the panel, so offering them
    /// alongside the inline mode would be controls that visibly do nothing.
    /// </summary>
    private void UpdateStyleRowVisibility() =>
        PanelOnlySettings.Visibility = _settings.Current.LyricsDisplay == LyricsDisplayMode.Panel
            ? Visibility.Visible
            : Visibility.Collapsed;

    // ---- Importing your own lyrics -----------------------------------------

    /// <summary>
    /// Names the file the current track would need, so the folder can also be used
    /// by hand without guessing the convention.
    /// </summary>
    private void UpdateImportDescription()
    {
        var track = _media.Current;

        if (track is null || !track.HasContent)
        {
            ImportDescription.Text =
                "An imported .lrc always wins over the network — the way to fix bad " +
                "timings, or to add a track the database has never heard of. Play " +
                "something to import for it.";
            ImportButton.IsEnabled = false;
            return;
        }

        ImportButton.IsEnabled = true;

        string name = LyricsService.FileNameFor(track);
        bool existing = _lyrics.HasImport(track);

        ImportDescription.Text =
            "An imported .lrc always wins over the network — the way to fix bad " +
            $"timings, or to add a track the database has never heard of. This track " +
            $"is filed as “{name}”{(existing ? ", which you already have." : ".")}";
    }

    private void ImportLyricsFile()
    {
        var track = _media.Current;
        if (track is null || !track.HasContent) return;

        var picker = new OpenFileDialog
        {
            Title = $"Choose lyrics for {track.Title}",
            Filter = "Lyrics files (*.lrc;*.txt)|*.lrc;*.txt|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (picker.ShowDialog(this) != true) return;

        bool imported = _lyrics.TryImport(track, picker.FileName, out string message);

        ImportStatus.Text = message;
        ImportStatus.Foreground = imported ? _theme.TextTertiary : _theme.TextSecondary;

        UpdateImportDescription();
    }

    /// <summary>
    /// Shows how much has been fetched, so clearing it is an informed choice rather
    /// than a leap.
    /// </summary>
    private void UpdateCacheSize()
    {
        var (count, bytes) = _lyrics.MeasureCache();

        if (count == 0)
        {
            CacheSizeText.Text = "Nothing cached yet. Lyrics are kept here so a track is only fetched once.";
            ClearCacheButton.IsEnabled = false;
            return;
        }

        ClearCacheButton.IsEnabled = true;

        string size = bytes < 1024L
            ? $"{bytes} bytes"
            : bytes < 1024L * 1024L
                ? $"{bytes / 1024d:F0} KB"
                : $"{bytes / (1024d * 1024d):F1} MB";

        CacheSizeText.Text =
            $"{count} track{(count == 1 ? string.Empty : "s")}, {size}. " +
            "Clearing does not touch files you imported.";
    }

    private void ClearLyricsCache()
    {
        int removed = _lyrics.ClearCache();

        ImportStatus.Text = removed == 0
            ? "There was nothing to clear."
            : $"Cleared {removed} cached track{(removed == 1 ? string.Empty : "s")}.";

        UpdateCacheSize();
    }

    private void OpenLyricsFolder()
    {
        try
        {
            Directory.CreateDirectory(_lyrics.CacheDirectory);

            Process.Start(new ProcessStartInfo
            {
                FileName = _lyrics.CacheDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ImportStatus.Text = $"Could not open the folder: {ex.Message}";
        }
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
