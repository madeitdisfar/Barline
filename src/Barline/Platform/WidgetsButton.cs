using Barline.Diagnostics;
using Microsoft.Win32;

namespace Barline.Platform;

/// <summary>
/// Whether Windows is showing its own Widgets button.
/// </summary>
/// <remarks>
/// It occupies the same corner of the taskbar this widget does, so the two overlap
/// until one of them goes. Asked rather than assumed, because most of the setup advice
/// an app can give is advice the reader has already followed, and being told to do
/// something already done is how a first run starts to feel like a lecture.
/// </remarks>
internal static class WidgetsButton
{
    private const string AdvancedKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private const string ValueName = "TaskbarDa";

    /// <summary>The Settings page holding the taskbar item toggles.</summary>
    public const string SettingsUri = "ms-settings:taskbar";

    public static bool IsVisible() => Interpret(Read());

    private static object? Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
            return key?.GetValue(ValueName);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"widgets button: could not read {ValueName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Turns whatever the registry held into an answer.
    /// </summary>
    /// <remarks>
    /// Anything other than the number zero counts as visible. Windows 11 ships with
    /// the button on and only writes this value once somebody changes it, so absent
    /// means on — and someone on an untouched install is exactly who needs telling.
    /// The unknown case errs the same way: an unneeded hint is read once and ignored,
    /// where a silent overlap looks like the app is broken.
    /// </remarks>
    internal static bool Interpret(object? raw) => raw is not int shown || shown != 0;
}
