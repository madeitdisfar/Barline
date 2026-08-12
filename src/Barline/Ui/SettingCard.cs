using System.Windows;

namespace Barline.Ui;

/// <summary>
/// Extra values a settings card carries beyond its own content.
/// </summary>
/// <remarks>
/// Attached rather than a control of its own, because the card is a template over the
/// stock <see cref="System.Windows.Controls.Expander"/> — everything else it needs is
/// already there, and a summary line is the one thing WPF has nowhere to put.
/// </remarks>
internal static class SettingCard
{
    /// <summary>
    /// What the setting currently says, shown on the header while it is collapsed.
    /// </summary>
    /// <remarks>
    /// The point of folding a card away is that you can still tell what is inside it
    /// without opening it. A row of chevrons with nothing but nouns beside them would
    /// hide the settings rather than tidy them.
    /// </remarks>
    public static readonly DependencyProperty SummaryProperty =
        DependencyProperty.RegisterAttached(
            "Summary",
            typeof(string),
            typeof(SettingCard),
            new PropertyMetadata(string.Empty));

    public static string GetSummary(DependencyObject element) =>
        (string)element.GetValue(SummaryProperty);

    public static void SetSummary(DependencyObject element, string value) =>
        element.SetValue(SummaryProperty, value);

    /// <summary>
    /// Whether this control is one the free build cannot use, which puts a lock beside
    /// its label.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="UIElement.IsEnabled"/>, which a locked control is also
    /// set to but which is not the same claim. Controls go disabled for ordinary
    /// reasons — importing a lyric file needs something playing, the effect radius
    /// needs an effect — and none of those should grow a padlock. This says why.
    /// </remarks>
    public static readonly DependencyProperty LockedProperty =
        DependencyProperty.RegisterAttached(
            "Locked",
            typeof(bool),
            typeof(SettingCard),
            new PropertyMetadata(false));

    public static bool GetLocked(DependencyObject element) =>
        (bool)element.GetValue(LockedProperty);

    public static void SetLocked(DependencyObject element, bool value) =>
        element.SetValue(LockedProperty, value);

    /// <summary>
    /// A Segoe Fluent Icons glyph to draw before a button's label.
    /// </summary>
    /// <remarks>
    /// Attached rather than folded into the button's content, because the card button
    /// template puts its content straight into a <c>TextBlock.Text</c>. Anything richer
    /// assigned to <c>Content</c> renders as its type name instead.
    /// </remarks>
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.RegisterAttached(
            "Glyph",
            typeof(string),
            typeof(SettingCard),
            new PropertyMetadata(string.Empty));

    public static string GetGlyph(DependencyObject element) =>
        (string)element.GetValue(GlyphProperty);

    public static void SetGlyph(DependencyObject element, string value) =>
        element.SetValue(GlyphProperty, value);
}
