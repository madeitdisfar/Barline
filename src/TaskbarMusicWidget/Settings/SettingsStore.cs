using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskbarMusicWidget.Diagnostics;

namespace TaskbarMusicWidget.Settings;

/// <summary>
/// Loads and persists <see cref="WidgetSettings"/> as JSON under
/// <c>%LocalAppData%\TaskbarMusicWidget\settings.json</c>.
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
        // By name, so the file reads as configuration rather than as magic numbers.
        Converters = { new JsonStringEnumConverter() },
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
            "TaskbarMusicWidget");
        _path = Path.Combine(_directory, "settings.json");

        Current = Load();
    }

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
        Save();

        DebugLog.Write(
            $"settings updated: color={Current.VisualizerColor} " +
            $"custom={Current.CustomBarColor ?? "(none)"} " +
            $"visualizer={Current.VisualizerEnabled}");

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

            DebugLog.Write($"settings loaded: visualizerColor={loaded.VisualizerColor}");
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
