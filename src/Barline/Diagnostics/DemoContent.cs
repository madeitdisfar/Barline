using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Barline.Media;

namespace Barline.Diagnostics;

/// <summary>
/// Synthetic now-playing content, enabled with <c>BARLINE_DEMO=1</c>.
/// </summary>
/// <remarks>
/// The widget hides itself when nothing is playing, which makes the visual design
/// impossible to inspect on a quiet machine. Demo mode supplies a plausible track
/// and generated cover art so layout, typography and theming can be iterated on
/// without commandeering the user's audio.
/// </remarks>
internal static class DemoContent
{
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("BARLINE_DEMO") == "1";

    public static TrackInfo CreateTrack() => new()
    {
        // Title/artist can be overridden to sweep text lengths while checking the
        // overflow fade, without rebuilding.
        Title = Environment.GetEnvironmentVariable("BARLINE_DEMO_TITLE") ?? "Everything In Its Right Place",
        Artist = Environment.GetEnvironmentVariable("BARLINE_DEMO_ARTIST") ?? "Radiohead",
        AlbumTitle = "Kid A",
        IsPlaying = true,
        CanGoNext = true,
        CanGoPrevious = true,
        CanPlayPause = true,
        AlbumArt = CreateArt(),
    };

    /// <summary>Stand-in cover art: a diagonal gradient with a soft highlight.</summary>
    private static ImageSource CreateArt()
    {
        const int size = 160;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
            };
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0xE8, 0x53, 0x2F), 0.0));
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0xC1, 0x27, 0x6B), 0.55));
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0x3B, 0x1C, 0x6B), 1.0));

            dc.DrawRectangle(gradient, null, new Rect(0, 0, size, size));

            var highlight = new RadialGradientBrush(
                Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF))
            {
                Center = new Point(0.3, 0.25),
                GradientOrigin = new Point(0.3, 0.25),
                RadiusX = 0.6,
                RadiusY = 0.6,
            };
            dc.DrawEllipse(highlight, null, new Point(size * 0.3, size * 0.25), size * 0.5, size * 0.5);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
