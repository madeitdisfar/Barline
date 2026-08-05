using System.IO;
using System.Text.Json;
using TaskbarMusicWidget.Diagnostics;
using TaskbarMusicWidget.Settings;

namespace TaskbarMusicWidget.Lyrics;

/// <summary>
/// Named looks, stored as JSON files under
/// <c>%LocalAppData%\TaskbarMusicWidget\presets</c>.
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
/// The three built-in looks are written here on first run rather than being compiled
/// in and hidden. That makes them readable, copyable and editable, and means "make one
/// of your own" starts from a working example instead of an empty file.
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
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaskbarMusicWidget",
            "presets");
    }

    public string DirectoryPath => _directory;

    /// <summary>
    /// Writes the built-in looks if they are not already there.
    /// </summary>
    /// <remarks>
    /// Never overwrites. Someone who has edited "Glow" to their taste should keep it
    /// across an update; a built-in they have deleted on purpose does come back, which
    /// is the lesser of the two surprises.
    /// </remarks>
    public void EnsureBuiltIns()
    {
        try
        {
            Directory.CreateDirectory(_directory);

            foreach (var preset in LyricsAppearance.BuiltIn)
            {
                string path = PathFor(preset.Name);
                if (!File.Exists(path)) Write(preset);
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"presets: could not write built-ins: {ex.Message}");
        }
    }

    /// <summary>Every preset on disk, by name.</summary>
    public IReadOnlyList<string> Names()
    {
        try
        {
            if (!Directory.Exists(_directory)) return [];

            return Directory
                .EnumerateFiles(_directory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
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

            string path = PathFor(preset.Name);
            string temporary = path + ".tmp";

            File.WriteAllText(temporary, JsonSerializer.Serialize(preset, SerializerOptions));
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
