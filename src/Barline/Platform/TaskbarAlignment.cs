using Barline.Diagnostics;
using Microsoft.Win32;

namespace Barline.Platform;

/// <summary>
/// Which end of the taskbar Windows puts its own buttons at.
/// </summary>
/// <remarks>
/// The widget lives at the left end, which a centered taskbar leaves empty. Setting
/// the taskbar to the left puts Start there instead, and the two overlap, so the
/// widget crosses to the other end. There is no window to measure for this and no
/// shell API that reports it, so the setting itself is read. It is the same key the
/// Widgets button is read from, one value along.
/// </remarks>
internal static class TaskbarAlignment
{
    private const string AdvancedKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private const string ValueName = "TaskbarAl";

    public static bool IsLeft() => Interpret(Read());

    private static object? Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
            return key?.GetValue(ValueName);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"taskbar alignment: could not read {ValueName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Turns whatever the registry held into an answer.
    /// </summary>
    /// <remarks>
    /// Only an explicit zero means left. Windows 11 centers by default and writes this
    /// value only once somebody changes it, so absent means centered, and so does a
    /// value of any other shape: the widget stays where every existing user already has
    /// it unless Windows says plainly that it should not.
    /// </remarks>
    internal static bool Interpret(object? raw) => raw is int value && value == 0;
}
