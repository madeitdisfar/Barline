using System.Windows.Media;
using System.Windows.Media.Animation;
using Barline.Diagnostics;
using Barline.Settings;

namespace Barline.Ui;

/// <summary>
/// Produces the visualiser's bar brush from the chosen colour mode, the system theme
/// and the current album art — and guarantees the result is actually visible on the
/// taskbar.
/// </summary>
/// <remarks>
/// <para>
/// The legibility correction is the point of this class. An album's dominant hue is
/// whatever the artwork happens to contain: a dark navy cover on the dark taskbar or
/// a pale yellow one on the light taskbar would render bars that are technically
/// coloured and practically invisible. So the hue the artwork chose is kept, and the
/// lightness is overruled until the bars clear a contrast floor against the taskbar.
/// </para>
/// <para>
/// It owns a single, unfrozen brush for the lifetime of the widget and animates its
/// colour, rather than handing out a new frozen brush per change. That gives a track
/// change a crossfade instead of a snap, and means the visualiser never has to know
/// its colour can change at all.
/// </para>
/// </remarks>
internal sealed class BarColorResolver
{
    private readonly Theme _theme;
    private readonly SettingsStore _settings;

    /// <summary>
    /// Deliberately not frozen: its colour is animated in place. The visualiser holds
    /// this same instance for the app's lifetime.
    /// </summary>
    private readonly SolidColorBrush _brush = new(Colors.White);

    // Extraction is cached against the art it came from. SetTrack runs on every
    // metadata tick — some apps raise those on every position update — while the art
    // only changes with the track, so a one-entry memo removes essentially all of
    // the repeat work.
    private ImageSource? _memoArt;
    private Color? _memoSeed;

    /// <summary>
    /// The colour last animated toward. Tracked separately rather than read back off
    /// the brush, because mid-animation the brush reports an interpolated value — so
    /// comparing against it would restart the animation on every metadata tick.
    /// </summary>
    private Color? _target;

    public BarColorResolver(Theme theme, SettingsStore settings)
    {
        _theme = theme;
        _settings = settings;
    }

    /// <summary>The brush to hand the visualiser. Stable for the app's lifetime.</summary>
    public Brush Brush => _brush;

    /// <summary>
    /// Recomputes the bar colour. Cheap to call often — the expensive part (art
    /// extraction) is memoised, and an unchanged result animates nothing.
    /// </summary>
    /// <summary>
    /// The colour a given mode would produce right now, without selecting it.
    /// </summary>
    /// <remarks>
    /// Lets the settings window show every option's real, corrected colour side by
    /// side — which is the honest way to present this, since what the user gets is
    /// never exactly what the artwork or the picker said.
    /// </remarks>
    public Color Preview(VisualizerColorMode mode, ImageSource? albumArt) =>
        Resolve(mode, albumArt);

    public void Update(ImageSource? albumArt)
    {
        Color target = Resolve(_settings.Current.VisualizerColor, albumArt);
        if (_target == target) return;

        bool first = _target is null;
        _target = target;

        if (first)
        {
            // Nothing to crossfade from on the first resolve; animating here would
            // show white bars easing into the real colour at startup.
            _brush.Color = target;
        }
        else
        {
            var animation = new ColorAnimationUsingKeyFrames();
            animation.KeyFrames.Add(new SplineColorKeyFrame(
                target,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Motion.NormalMs)),
                Motion.Standard));

            _brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
        }

        DebugLog.Write(
            $"bar colour: mode={_settings.Current.VisualizerColor} " +
            $"-> #{target.A:X2}{target.R:X2}{target.G:X2}{target.B:X2}");
    }

    private Color Resolve(VisualizerColorMode mode, ImageSource? albumArt)
    {
        switch (mode)
        {
            case VisualizerColorMode.SystemAccent:
                return MakeLegible(_theme.Accent);

            case VisualizerColorMode.Custom:
                // An unparseable value is a hand-edit mistake, not a reason to show
                // nothing; fall back rather than rendering an invisible bar.
                var custom = TryParseColor(_settings.Current.CustomBarColor);
                return custom is null ? _theme.BarDefault : MakeLegible(custom.Value);

            case VisualizerColorMode.AlbumArt:
                var seed = SeedFor(albumArt);
                return seed is null ? _theme.BarDefault : MakeLegible(seed.Value);

            default:
                // The built-in colours are already chosen against real taskbar
                // material by hand, including their alpha, so they bypass correction
                // entirely — running them through it would only discard that.
                return _theme.BarDefault;
        }
    }

    private Color? SeedFor(ImageSource? albumArt)
    {
        if (albumArt is null) return null;
        if (ReferenceEquals(albumArt, _memoArt)) return _memoSeed;

        _memoArt = albumArt;
        _memoSeed = AlbumArtPalette.TryExtractSeed(albumArt);
        return _memoSeed;
    }

    /// <summary>
    /// Corrects a colour for visibility, falling back to the theme's own bar colour
    /// when the input has no hue worth keeping (a greyscale cover, a grey accent).
    /// </summary>
    private Color MakeLegible(Color color)
    {
        // Bars go lighter than a dark taskbar and darker than a light one.
        var coerced = Legibility.TryCoerce(color, _theme.BackdropEstimate, !_theme.IsLight);

        if (coerced is null)
            DebugLog.Write("bar colour: no legible variant of the source; using theme default");

        return coerced ?? _theme.BarDefault;
    }

    private static Color? TryParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            // Accepts "#RRGGBB", "#AARRGGBB" and the named colours, so a hand-edited
            // file can say "OrangeRed" and be understood.
            return ColorConverter.ConvertFromString(text) as Color?;
        }
        catch
        {
            return null;
        }
    }
}
