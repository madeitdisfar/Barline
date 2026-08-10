using System.IO;
using System.Text.Json;
using Barline.Diagnostics;
using Barline.Lyrics;
using Barline.Platform;

namespace Barline.Settings;

/// <summary>
/// Takes the paid values out of the settings when they are not paid for, and puts them
/// back when they are.
/// </summary>
/// <remarks>
/// <para>
/// Stripping rather than merely ignoring, because <c>settings.json</c> is documented as
/// safe to hand-edit and the settings window has a button that opens the folder it is
/// in. Leaving the values live would make the gate a text edit away, which is a
/// different thing from being a compile away: one is a supported route the app itself
/// points at, the other is not.
/// </para>
/// <para>
/// Everything removed is written to <c>premium-backup.json</c> first, and nothing ever
/// deletes that file except a restore. That is what makes stripping safe to be wrong
/// about — the worst a mistaken strip can do is move values into the sidecar for one
/// run. Together with <see cref="LicenseService.MayStrip"/>, which refuses to act on
/// anything short of a positive no, a Store outage cannot cost a paying user their
/// configuration.
/// </para>
/// </remarks>
internal static class PremiumSettings
{
    public static string BackupPath => Path.Combine(AppPaths.Root, "premium-backup.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new TolerantEnumConverterFactory() },
    };

    /// <summary>
    /// Removes anything paid from <paramref name="settings"/>, keeping a copy.
    /// </summary>
    /// <returns>True when something was removed, and so the file needs saving.</returns>
    public static bool Strip(WidgetSettings settings) => Strip(settings, BackupPath);

    internal static bool Strip(WidgetSettings settings, string backupPath)
    {
        var kept = Read(backupPath) ?? new Backup();
        bool changed = false;

        if (settings.VisualizerBarCount > WidgetSettings.MinBarCount)
        {
            kept.BarCount = settings.VisualizerBarCount;
            settings.VisualizerBarCount = WidgetSettings.MinBarCount;
            changed = true;
        }

        if (settings.VisualizerColor == VisualizerColorMode.AlbumArt)
        {
            kept.Color = settings.VisualizerColor;
            settings.VisualizerColor = VisualizerColorMode.Default;
            changed = true;
        }

        var style = settings.LyricsStyle;

        if (style.Effect != LyricsEffect.None)
        {
            kept.Effect = style.Effect;
            kept.EffectRadius = style.EffectRadius;
            kept.EffectColor = style.EffectColor;
            style.Effect = LyricsEffect.None;
            changed = true;
        }

        if (style.Position == LyricsPanelPosition.Custom)
        {
            kept.Position = style.Position;
            kept.CustomX = style.CustomX;
            kept.CustomY = style.CustomY;
            style.Position = LyricsPanelPosition.AboveWidget;
            changed = true;
        }

        if (changed) Write(kept, backupPath);

        return changed;
    }

    /// <summary>
    /// Puts back anything a previous run took out.
    /// </summary>
    /// <remarks>
    /// A value is only restored if the setting is still sitting on the fallback that
    /// replaced it. Somebody who spent a week unlicensed and picked a custom bar color
    /// in the meantime has made a newer choice than the backup holds, and a purchase
    /// should not undo it.
    /// </remarks>
    /// <returns>True when something was put back, and so the file needs saving.</returns>
    public static bool Restore(WidgetSettings settings) => Restore(settings, BackupPath);

    internal static bool Restore(WidgetSettings settings, string backupPath)
    {
        if (Read(backupPath) is not { } kept) return false;

        bool changed = false;

        if (kept.BarCount is { } bars
            && settings.VisualizerBarCount == WidgetSettings.MinBarCount)
        {
            settings.VisualizerBarCount =
                Math.Clamp(bars, WidgetSettings.MinBarCount, WidgetSettings.MaxBarCount);
            changed = true;
        }

        if (kept.Color is { } color && settings.VisualizerColor == VisualizerColorMode.Default)
        {
            settings.VisualizerColor = color;
            changed = true;
        }

        var style = settings.LyricsStyle;

        if (kept.Effect is { } effect && style.Effect == LyricsEffect.None)
        {
            style.Effect = effect;
            style.EffectRadius = kept.EffectRadius ?? style.EffectRadius;
            style.EffectColor = kept.EffectColor ?? style.EffectColor;
            changed = true;
        }

        if (kept.Position is { } position && style.Position == LyricsPanelPosition.AboveWidget)
        {
            style.Position = position;
            style.CustomX = kept.CustomX ?? style.CustomX;
            style.CustomY = kept.CustomY ?? style.CustomY;
            changed = true;
        }

        Clear(backupPath);

        return changed;
    }

    internal static Backup? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            return JsonSerializer.Deserialize<Backup>(
                File.ReadAllText(path), SerializerOptions);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"premium: could not read the backup: {ex.Message}");
            return null;
        }
    }

    private static void Write(Backup backup, string path)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonSerializer.Serialize(backup, SerializerOptions));
        }
        catch (Exception ex)
        {
            DebugLog.Write($"premium: could not write the backup: {ex.Message}");
        }
    }

    private static void Clear(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"premium: could not clear the backup: {ex.Message}");
        }
    }

    /// <summary>
    /// The paid values a previous run removed.
    /// </summary>
    /// <remarks>
    /// Every field is nullable so that absent and default stay distinguishable: a null
    /// effect means nothing was taken, where <see cref="LyricsEffect.None"/> would be a
    /// claim that the user had chosen no effect.
    /// </remarks>
    internal sealed class Backup
    {
        public int? BarCount { get; set; }
        public VisualizerColorMode? Color { get; set; }
        public LyricsEffect? Effect { get; set; }
        public double? EffectRadius { get; set; }
        public string? EffectColor { get; set; }
        public LyricsPanelPosition? Position { get; set; }
        public double? CustomX { get; set; }
        public double? CustomY { get; set; }
    }
}
