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
}
