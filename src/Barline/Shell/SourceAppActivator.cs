using System.Diagnostics;
using System.IO;
using Barline.Diagnostics;
using static Barline.Shell.NativeMethods;

namespace Barline.Shell;

/// <summary>
/// Best-effort activation of the app that owns the current media session, so
/// clicking the widget brings the player forward.
/// </summary>
/// <remarks>
/// <para>
/// SMTC identifies a session only by AUMID and offers no activation call. For
/// packaged apps the AUMID is an opaque family name (e.g.
/// <c>SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify</c>) that maps to no process
/// name, so this resolves the common desktop case — where the AUMID is an
/// executable name or path — and quietly does nothing otherwise.
/// </para>
/// <para>
/// Windows also enforces foreground-activation rules, so the call can legitimately
/// fail and merely flash the app's taskbar button. Both outcomes are acceptable
/// here; the widget must never appear broken because of it.
/// </para>
/// </remarks>
internal static class SourceAppActivator
{
    private const int SW_RESTORE = 9;

    public static void TryActivate(string? sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId)) return;

        try
        {
            string name = Path.GetFileNameWithoutExtension(sourceAppId);
            if (string.IsNullOrWhiteSpace(name)) return;

            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero) continue;

                    ShowWindow(handle, SW_RESTORE);
                    SetForegroundWindow(handle);
                    return;
                }
            }

            DebugLog.Write($"no window found to activate for '{sourceAppId}'");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"activate failed: {ex.Message}");
        }
    }
}
