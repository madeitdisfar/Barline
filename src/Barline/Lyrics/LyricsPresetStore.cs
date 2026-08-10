using System.IO;
using System.Text.Json;
using Barline.Diagnostics;
using Barline.Platform;
using Barline.Settings;

namespace Barline.Lyrics;

/// <summary>
/// Named looks, stored as JSON files under
/// <c>%LocalAppData%\Barline\presets</c>.
/// </summary>
/// <remarks>
/// <para>
/// Presets are snapshots, not the live configuration. The settings window edits the
/// appearance directly and you see it immediately; a preset is a copy of that saved
/// under a name, and loading one copies it back. The alternative — treating the file
/// as the only home for these values — would mean every tweak was a file edit and the
/// UI could only pick between whole files.
/// </para>
/// <para>
/// The built-in looks are written here on first run rather than being compiled in and
/// hidden. That makes them readable, copyable and editable, and means "make one of your
/// own" starts from a working example instead of an empty file. Which of them get
/// written depends on the license, since four of the seven glow.
/// </para>
/// </remarks>
internal sealed class LyricsPresetStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        // Tolerant, so a preset written by another build — or naming an option since
        // removed, as the acrylic background was — still loads.
        Converters = { new TolerantEnumConverterFactory() },
    };

    private readonly string _directory;

    public LyricsPresetStore()
    {
        _directory = AppPaths.Presets;
    }

    public string DirectoryPath => _directory;

    /// <summary>
    /// Writes the built-in looks if they are not already there, or if the copy on disk
    /// predates something a preset now has to say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Otherwise never overwrites. Someone who has edited "Glow" to their taste should
    /// keep it across an update; a built-in they have deleted on purpose does come
    /// back, which is the lesser of the two surprises.
    /// </para>
    /// <para>
    /// The exception is a schema change, because a stale built-in does not merely look
    /// dated — it means something different from what its name promises. "Glow" written
    /// before placement was part of a preset says nothing about the panel, so loading it
    /// would leave a panel-sized look in the widget's 150px slot. Ours to maintain, so
    /// they are replaced; a preset the user saved is theirs, and is left alone.
    /// </para>
    /// </remarks>
    public void EnsureBuiltIns(bool premium)
    {
        try
        {
            Directory.CreateDirectory(_directory);

            RemoveRetired();

            foreach (var preset in LyricsAppearance.BuiltIn)
            {
                // A paid look is never written by a free build, so it cannot appear in
                // the picker only to refuse to load. Buying writes the missing files on
                // the next start, which is this same pass.
                if (preset.UsesPremium && !premium) continue;

                var stored = Load(preset.Name);

                if (stored is null || stored.Schema < LyricsAppearance.CurrentSchema)
                    Write(preset);
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"presets: could not write built-ins: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes built-ins we no longer ship, as long as they are still what we wrote.
    /// </summary>
    /// <remarks>
    /// Withdrawing one has to reach into the user's folder, so it is deliberately the
    /// narrowest deletion that works: same name, and every visual field still matching
    /// the copy in <see cref="LyricsAppearance.Retired"/>. Anything edited is theirs and
    /// stays, under its old name, doing what it always did.
    /// </remarks>
    private void RemoveRetired()
    {
        foreach (var retired in LyricsAppearance.Retired)
        {
            var stored = Load(retired.Name);

            if (stored is null || !stored.LooksLike(retired)) continue;

            if (Delete(retired.Name))
                DebugLog.Write($"presets: removed the retired {retired.Name}");
        }
    }

    /// <summary>Deletes a preset by name. False when it was not there, or would not go.</summary>
    public bool Delete(string name)
    {
        try
        {
            string path = PathFor(name);

            if (!File.Exists(path)) return false;

            File.Delete(path);

            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"presets: could not delete {name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Every preset on disk, by name. Without <paramref name="premium"/>, only the free
    /// built-ins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The free list is by allowlist rather than by filtering out paid looks, because
    /// keeping your own presets is itself what is being sold. Filtering only on content
    /// would still hand a free build any preset dropped into the folder by hand, which
    /// is the whole feature by another route.
    /// </para>
    /// <para>
    /// It also cannot be left to <see cref="EnsureBuiltIns"/> alone. That stops paid
    /// looks being written; this stops ones already on disk being offered — from a run
    /// made while licensed, or from a file copied in. Both have to hold, or the picker
    /// ends up listing a preset that will not load.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Names(bool premium = true)
    {
        try
        {
            if (!Directory.Exists(_directory)) return [];

            var free = LyricsAppearance.BuiltIn
                .Where(preset => !preset.UsesPremium)
                .Select(preset => preset.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return Directory
                .EnumerateFiles(_directory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Where(name => premium || free.Contains(name))
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            DebugLog.Write($"presets: could not list: {ex.Message}");
            return [];
        }
    }

    /// <summary>Reads a preset by name, or null when it is missing or unreadable.</summary>
    public LyricsAppearance? Load(string name)
    {
        try
        {
            string path = PathFor(name);
            if (!File.Exists(path)) return null;

            var preset = JsonSerializer.Deserialize<LyricsAppearance>(
                File.ReadAllText(path), SerializerOptions);

            if (preset is null) return null;

            // The file name is the name, so renaming the file renames the preset and
            // the two can never disagree.
            preset.Name = name;

            return preset.Normalize();
        }
        catch (Exception ex)
        {
            DebugLog.Write($"presets: '{name}' unreadable: {ex.Message}");
            return null;
        }
    }

    /// <summary>Reads a preset from an arbitrary path, for importing.</summary>
    public LyricsAppearance? Read(string path)
    {
        try
        {
            var preset = JsonSerializer.Deserialize<LyricsAppearance>(
                File.ReadAllText(path), SerializerOptions);

            if (preset is null) return null;

            preset.Name = Path.GetFileNameWithoutExtension(path);
            return preset.Normalize();
        }
        catch (Exception ex)
        {
            DebugLog.Write($"presets: could not read '{path}': {ex.Message}");
            return null;
        }
    }

    public bool Write(LyricsAppearance preset)
    {
        try
        {
            Directory.CreateDirectory(_directory);

            // Stamped on a copy rather than on the caller's object: this is a fact about
            // the file, and the live style has no business being edited by saving it.
            var stamped = preset.Clone();
            stamped.Schema = LyricsAppearance.CurrentSchema;

            string path = PathFor(preset.Name);
            string temporary = path + ".tmp";

            File.WriteAllText(temporary, JsonSerializer.Serialize(stamped, SerializerOptions));
            File.Move(temporary, path, overwrite: true);

            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"presets: could not save '{preset.Name}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Strips anything a file name cannot carry, so any name can be saved.</summary>
    public static string SanitizeName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        name = name.Trim();

        return name.Length == 0 ? "Custom" : name;
    }

    private string PathFor(string name) =>
        Path.Combine(_directory, $"{SanitizeName(name)}.json");
}
