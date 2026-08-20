using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Barline.Ui;

namespace Barline.Tray;

/// <summary>
/// Paints the tray menu the way Windows 11 paints its own.
/// </summary>
/// <remarks>
/// <para>
/// A <c>ToolStripDropDown</c> left to itself is a 2005 menu: a light gray surface
/// whatever the system theme says, a raised gutter down the left with a 3D checkbox in
/// it, square corners, and a selection band running edge to edge. Every one of those is
/// a tell, and the menu is the widget's only chrome besides the settings window.
/// </para>
/// <para>
/// Derived from <see cref="ToolStripRenderer"/> rather than from the professional
/// renderer, whose color table can restate the palette but not the geometry. The gutter
/// and the band are shape rather than color, and they are most of the problem.
/// </para>
/// <para>
/// The rounded corners are not here. They belong to the window rather than to anything
/// drawn inside it, so <c>TrayIcon</c> asks DWM for them; this only matches the radius
/// so the outline it draws follows the same curve the compositor clips to.
/// </para>
/// </remarks>
internal sealed class FluentMenuRenderer : ToolStripRenderer
{
    /// <summary>Window corner radius, in logical pixels, matching DWMWCP_ROUND.</summary>
    internal const float CornerRadius = 8f;

    /// <summary>Corner radius of the selection behind a hovered item.</summary>
    private const float ItemRadius = 4f;

    /// <summary>
    /// How far the selection is inset from the item's own edges.
    /// </summary>
    /// <remarks>
    /// Small, because the menu's padding already holds the items clear of the border.
    /// This is only what keeps two adjacent pills from touching.
    /// </remarks>
    private const float ItemInsetX = 0f;
    private const float ItemInsetY = 1f;

    /// <summary>How far a separator stops short of the edges.</summary>
    private const float SeparatorInset = 10f;

    /// <summary>Least distance the checkmark may sit from the item's left edge.</summary>
    private const float CheckInset = 4f;

    /// <summary>
    /// Drawn size of the checkmark, independent of the column reserved for it.
    /// </summary>
    /// <remarks>
    /// The column is deliberately wider than the glyph, because it is also what holds
    /// the text clear of the check. Sizing the glyph to fill it would make the check
    /// grow every time that gap was widened.
    /// </remarks>
    private const float GlyphSize = 15f;

    /// <summary>Extra distance the text is held clear of the check column.</summary>
    /// <remarks>
    /// Applied here because nothing in the layout will do it. The column WinForms
    /// reserves is about half the width Windows 11 gives one, and neither the item's
    /// padding, nor the menu's, nor the declared image size moves the text a pixel:
    /// all three were measured leaving it at the same offset. Without this the
    /// checkmark's last stroke and the first letter are two pixels apart.
    /// </remarks>
    private const float TextInset = 8f;

    private readonly Theme _theme;

    /// <summary>
    /// Device pixels per logical pixel, so every figure above can be written as one.
    /// </summary>
    /// <remarks>
    /// GDI+ draws in device pixels, so a radius of 8 is 8 physical pixels and comes out
    /// half size on a 200% display. Set by <c>TrayIcon.ApplyMetrics</c>, which measures
    /// it rather than assuming.
    /// </remarks>
    public double Scale { get; set; } = 1d;

    public FluentMenuRenderer(Theme theme) => _theme = theme;

    private float S(float value) => (float)(value * Scale);

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Gdi(_theme.MenuBackground));
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    /// <summary>
    /// Outlines the surface, following the curve DWM rounds the window to.
    /// </summary>
    /// <remarks>
    /// A hairline rather than a border proper. On a dark desktop the flyout would
    /// otherwise have no edge at all where it overlaps something of a similar shade,
    /// and it is the outline rather than the fill that reads as a raised surface.
    /// </remarks>
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var bounds = e.AffectedBounds;
        bounds.Width -= 1;
        bounds.Height -= 1;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = Rounded(bounds, S(CornerRadius));
        using var pen = new Pen(Gdi(_theme.MenuBorder));

        e.Graphics.DrawPath(pen, path);
        e.Graphics.SmoothingMode = SmoothingMode.Default;
    }

    /// <summary>
    /// The gutter, deliberately left as bare surface.
    /// </summary>
    /// <remarks>
    /// The column stays, because a checkable item needs somewhere to put its check and
    /// Windows 11 reserves the same space. What goes is the raised gray fill and the
    /// rule down its right edge, which is the single most dated thing about the menu.
    /// </remarks>
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is not ToolStripMenuItem item || !item.Enabled) return;
        if (!item.Selected && !item.Pressed) return;

        var fill = item.Pressed ? _theme.SubtlePressed : _theme.SubtleHover;

        // Inset on both axes, so the selection is a rounded pill sitting inside the
        // flyout rather than a band spanning it. That shape is what a Fluent menu reads
        // as, more than the color does.
        var bounds = new RectangleF(
            S(ItemInsetX),
            S(ItemInsetY),
            e.Item.Width - (S(ItemInsetX) * 2f),
            e.Item.Height - (S(ItemInsetY) * 2f));

        if (bounds.Width <= 0f || bounds.Height <= 0f) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = Rounded(bounds, S(ItemRadius));
        // Alpha is kept rather than flattened: GDI+ composites a fill correctly, and
        // these two tokens are defined as translucent.
        using var brush = new SolidBrush(Gdi(fill));

        e.Graphics.FillPath(brush, path);
        e.Graphics.SmoothingMode = SmoothingMode.Default;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // Flattened against the surface, because the text goes through GDI, which
        // discards alpha rather than compositing it.
        e.TextColor = Flatten(
            e.Item.Enabled ? _theme.TextPrimary : _theme.TextTertiary,
            _theme.MenuBackground);

        // Given back the whole item to sit in, and centered inside it. WinForms
        // measures the text box against the content area but never offsets it by the
        // top padding, so a rectangle 34 high is handed back at y=1 inside an item 72
        // high and every line in the menu sits about a third of the way up.
        var bounds = e.TextRectangle;
        bounds.Y = 0;
        bounds.Height = e.Item.Height;

        // Taken out of the width as well as added to the left, so a line long enough to
        // fill the item still stops inside it rather than running under the border.
        int inset = (int)Math.Round(S(TextInset));
        bounds.X += inset;
        bounds.Width -= inset;

        e.TextRectangle = bounds;
        e.TextFormat |= TextFormatFlags.VerticalCenter;

        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        float y = (float)Math.Round(e.Item.Height / 2d);
        float inset = S(SeparatorInset);

        using var pen = new Pen(Gdi(_theme.MenuDivider));

        e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset, y);
    }

    /// <summary>
    /// Draws the checkmark as two strokes rather than letting WinForms draw its boxed
    /// one, which arrives as a sunken 3D checkbox and belongs to a different decade.
    /// </summary>
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var rect = e.ImageRectangle;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        float w = S(GlyphSize);
        float h = w;

        // Centered in the column rather than drawn to fill it, and clamped. WinForms
        // centers the rectangle on a margin narrower than the image it was told to
        // reserve, so its left edge can be negative, and the check was painted on the
        // desktop beside the menu rather than inside it.
        float x = Math.Max(rect.X + ((rect.Width - w) / 2f), S(CheckInset));
        float y = rect.Y + ((rect.Height - h) / 2f);

        // Proportions of the Segoe Fluent CheckMark glyph in a unit box, so the stroke
        // lands where the system's own does at any size.
        var start = new PointF(x + (w * 0.22f), y + (h * 0.52f));
        var knee = new PointF(x + (w * 0.42f), y + (h * 0.72f));
        var end = new PointF(x + (w * 0.78f), y + (h * 0.28f));

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var pen = new Pen(Flatten(_theme.TextPrimary, _theme.MenuBackground), S(1.3f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        e.Graphics.DrawLines(pen, [start, knee, end]);
        e.Graphics.SmoothingMode = SmoothingMode.Default;
    }

    private static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();

        float d = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));

        if (d <= 0f)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, d, d, 180f, 90f);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270f, 90f);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0f, 90f);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90f, 90f);
        path.CloseFigure();

        return path;
    }

    private static Color Gdi(System.Windows.Media.Color color) =>
        Color.FromArgb(color.A, color.R, color.G, color.B);

    private static Color Gdi(System.Windows.Media.Brush brush) =>
        brush is System.Windows.Media.SolidColorBrush solid
            ? Gdi(solid.Color)
            : Color.Transparent;

    /// <summary>Composites a translucent token onto a known opaque surface.</summary>
    private static Color Flatten(System.Windows.Media.Brush brush, System.Windows.Media.Color over)
    {
        var color = Gdi(brush);
        double a = color.A / 255d;

        return Color.FromArgb(
            255,
            (byte)Math.Round((color.R * a) + (over.R * (1d - a))),
            (byte)Math.Round((color.G * a) + (over.G * (1d - a))),
            (byte)Math.Round((color.B * a) + (over.B * (1d - a))));
    }
}
