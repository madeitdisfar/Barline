using System.IO;
using System.Windows;
using Barline.Diagnostics;

namespace Barline.Ui;

/// <summary>
/// Shows one of the text files the app ships with, in a window rather than by handing
/// it to the shell. See the note in the XAML for why.
/// </summary>
internal partial class DocumentWindow : Window
{
    private readonly Theme _theme;

    private DocumentWindow(Theme theme, string title, string caption, string body)
    {
        _theme = theme;

        InitializeComponent();

        Title = title;
        Caption.Text = caption;
        Body.Text = body;

        ApplyTheme();
        _theme.Changed += OnThemeChanged;
        Closed += (_, _) => _theme.Changed -= OnThemeChanged;
    }

    /// <summary>
    /// Opens a shipped file, or says so plainly if it is not there.
    /// </summary>
    /// <remarks>
    /// A missing file is not silently swallowed. These two travel with the binary to
    /// satisfy the terms it is distributed under, so their absence is a packaging
    /// fault worth seeing rather than a button that appears to do nothing.
    /// </remarks>
    public static void Show(Window owner, Theme theme, string path, string caption)
    {
        string title = Path.GetFileName(path);
        string body;

        try
        {
            body = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"document: could not read {path}: {ex.Message}");

            body =
                $"This copy of Barline is missing {title}.\r\n\r\n" +
                $"It should sit beside the application, at:\r\n{path}\r\n\r\n" +
                $"The current text is always available at\r\n{Platform.AppInfo.RepositoryUrl}";
        }

        var window = new DocumentWindow(theme, title, caption, body) { Owner = owner };
        window.ShowDialog();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    private void ApplyTheme()
    {
        Resources["WindowBackgroundBrush"] = _theme.WindowBackground;
        Resources["TextPrimaryBrush"] = _theme.TextPrimary;
        Resources["TextSecondaryBrush"] = _theme.TextSecondary;
        Resources["CardBackgroundBrush"] = _theme.CardBackground;
        Resources["CardStrokeBrush"] = _theme.CardStroke;
    }
}
