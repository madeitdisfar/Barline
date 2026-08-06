using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Barline.Diagnostics;

namespace Barline.Settings;

/// <summary>
/// Loads and persists <see cref="WidgetSettings"/> as JSON under
/// <c>%LocalAppData%\Barline\settings.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// LocalAppData rather than the registry or the install directory: the file is
/// per-user, survives moving the executable, and stays hand-editable — which is the
/// only way to reach settings the UI does not expose yet.
/// </para>
/// <para>
/// Reads never throw. A missing file is first run, and a malformed one is a bad hand
/// edit or a truncated write; both fall back to defaults so the widget always
/// starts. The bad file is left on disk rather than deleted, so it can be inspected,
/// and is overwritten by the next save.
/// </para>
/// </remarks>
internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        // By name, so the file reads as configuration rather than as magic numbers —
        // and tolerantly, so a value this build no longer recognises costs one setting
        // rather than the whole file.
        Converters = { new TolerantEnumConverterFactory() },
    };

    private readonly string _directory;
    private readonly string _path;

    /// <summary>
    /// The live settings object. Held by reference, so consumers read the current
    /// value without re-fetching; they are told it changed via <see cref="Changed"/>.
    /// </summary>
    public WidgetSettings Current { get; private set; }

    /// <summary>Where the file lives, so the settings window can point users at it.</summary>
    public string FilePath => _path;

    public event EventHandler? Changed;

    public SettingsStore()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Barline");
        _path = Path.Combine(_directory, "settings.json");

        Current = Load();

        // Rewritten at once rather than at the next change, so a file folded forward on
        // load stops describing itself as something it no longer is — and so the fold
        // happens once instead of on every launch until a setting happens to change.
        if (_migrated) Save();
    }

    /// <summary>Whether the file that was loaded had to be folded forward.</summary>
    private bool _migrated;

    /// <summary>
    /// Applies a change, persists it, and notifies listeners.
    /// </summary>
    /// <remarks>
    /// The single write path. Callers mutating <see cref="Current"/> directly would
    /// get a change that works until the next restart and then vanishes, which is
    /// exactly the bug this shape prevents.
    /// </remarks>
    public void Update(Action<WidgetSettings> mutate)
    {
        mutate(Current);

        // Before saving, so an out-of-range value can never reach the file even if a
        // caller writes one.
        Current.Normalize();

        Save();

        DebugLog.Write(
            $"settings updated: color={Current.VisualizerColor} " +
            $"custom={Current.CustomBarColor ?? "(none)"} " +
            $"visualizer={Current.VisualizerEnabled} " +
            $"bars={Current.VisualizerBarCount}");

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private WidgetSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                DebugLog.Write("settings: no file yet; using defaults");
                return new WidgetSettings();
            }

            string json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<WidgetSettings>(json, SerializerOptions);

            if (loaded is null)
            {
                DebugLog.Write("settings: file deserialised to null; using defaults");
                return new WidgetSettings();
            }

            // Read before normalising, which is what clears them.
            _migrated = loaded.Version != WidgetSettings.CurrentVersion;

            // A hand edit can put values out of range without making the file
            // unreadable, so this runs on every successful load rather than only on
            // a parse failure.
            loaded.Normalize();

            DebugLog.Write(
                $"settings loaded: visualizerColor={loaded.VisualizerColor} " +
                $"bars={loaded.VisualizerBarCount}");
            return loaded;
        }
        catch (Exception ex)
        {
            // Includes an unknown enum name, which throws rather than falling back —
            // so a file written by a newer build resets rather than half-applying.
            DebugLog.Write($"settings load failed ({ex.Message}); using defaults");
            return new WidgetSettings();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_directory);

            // Write beside the target and swap, so a crash or power loss mid-write
            // cannot leave a truncated file that reads as corrupt on next launch.
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Current, SerializerOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Settings are a convenience; failing to persist must not take the
            // widget down. The change still applies for this session.
            DebugLog.Write($"settings save failed: {ex.Message}");
        }
    }
}
