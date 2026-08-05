using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TaskbarMusicWidget.Diagnostics;

namespace TaskbarMusicWidget.Lyrics;

/// <summary>One cached lookup, stored as JSON beside the settings file.</summary>
internal sealed class CachedLyrics
{
    public string? SyncedLyrics { get; set; }
    public string? PlainLyrics { get; set; }

    /// <summary>
    /// LRCLIB's YAML form, kept because it states each line's end and the LRC does
    /// not. Absent from entries written before it was used, which read back as null
    /// and fall through to the LRC.
    /// </summary>
    public string? LyricsFile { get; set; }

    /// <summary>
    /// False when the lookup succeeded but the track has no lyrics filed. Recorded
    /// rather than left absent, so a miss is not re-fetched on every play.
    /// </summary>
    public bool Found { get; set; }

    public DateTimeOffset FetchedUtc { get; set; }

    /// <summary>
    /// What the entry was written by. Bumped when a new field starts being stored, so
    /// entries missing it are fetched once more and then left alone — rather than
    /// either staying permanently degraded or being re-fetched on every play.
    /// </summary>
    public int Schema { get; set; }

    /// <summary>Kept for reading the cache by hand; nothing depends on it.</summary>
    public string? TrackName { get; set; }
    public string? ArtistName { get; set; }
}

/// <summary>
/// Remembers lookups on disk so a track is fetched once, not once per play.
/// </summary>
/// <remarks>
/// <para>
/// Misses are cached as deliberately as hits. Without that, every play of a track
/// with no lyrics filed — which is a great many of them — would be another request
/// to a service run for free. Misses do expire, because the database grows and
/// today's gap may be filled next month.
/// </para>
/// <para>
/// Reads never throw. A corrupt or half-written entry is treated as absent and
/// overwritten by the next fetch, exactly as the settings file is.
/// </para>
/// </remarks>
internal sealed class LyricsCache
{
    /// <summary>
    /// How long to trust a recorded miss. Long enough to stop re-asking for a track
    /// on every play, short enough that newly contributed lyrics appear without the
    /// user having to clear anything.
    /// </summary>
    private static readonly TimeSpan MissLifetime = TimeSpan.FromDays(14);

    /// <summary>
    /// Current entry schema. Version 2 added the lyricsfile form, which carries each
    /// line's end and so improves the word-timing estimate.
    /// </summary>
    public const int CurrentSchema = 2;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _directory;

    public LyricsCache()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaskbarMusicWidget",
            "lyrics");
    }

    public string DirectoryPath => _directory;

    /// <summary>
    /// Identifies a track independently of how its name was punctuated, so the same
    /// song reported slightly differently by two apps is one cache entry.
    /// </summary>
    /// <remarks>
    /// Duration is rounded to the nearest second and included, because it is what
    /// distinguishes a radio edit from an album cut that share every other field.
    /// </remarks>
    internal static string KeyFor(string title, string artist, TimeSpan duration)
    {
        string identity = string.Join(
            "|",
            TrackQuery.Clean(title).ToLowerInvariant(),
            TrackQuery.PrimaryArtist(artist).ToLowerInvariant(),
            (int)Math.Round(duration.TotalSeconds));

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));

        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>Reads an entry, or null when absent, unreadable or an expired miss.</summary>
    public CachedLyrics? Read(string key, DateTimeOffset now)
    {
        string path = PathFor(key);

        try
        {
            if (!File.Exists(path)) return null;

            var entry = JsonSerializer.Deserialize<CachedLyrics>(File.ReadAllText(path));
            if (entry is null) return null;

            // Written before a field we now store existed. Treated as absent so it is
            // fetched once more; the rewrite stamps the current schema, so this cannot
            // turn into a re-fetch on every play.
            if (entry.Schema < CurrentSchema) return null;

            // A hit is kept forever: lyrics for a released track do not change.
            if (entry.Found) return entry;

            return now - entry.FetchedUtc < MissLifetime ? entry : null;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"lyrics cache: unreadable entry {key}: {ex.Message}");
            return null;
        }
    }

    public void Write(string key, CachedLyrics entry)
    {
        try
        {
            Directory.CreateDirectory(_directory);

            // Written beside the target and swapped, so an interrupted write cannot
            // leave an entry that reads as corrupt on the next launch.
            string path = PathFor(key);
            string temporary = path + ".tmp";

            File.WriteAllText(temporary, JsonSerializer.Serialize(entry, SerializerOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // A cache that cannot be written is a slow cache, not a broken widget.
            DebugLog.Write($"lyrics cache: write failed: {ex.Message}");
        }
    }

    /// <summary>How many fetched entries are stored, and how much they take up.</summary>
    public (int Count, long Bytes) Measure()
    {
        try
        {
            if (!Directory.Exists(_directory)) return (0, 0L);

            var files = Directory.EnumerateFiles(_directory, "*.json").ToList();

            return (files.Count, files.Sum(file => new FileInfo(file).Length));
        }
        catch (Exception ex)
        {
            DebugLog.Write($"lyrics cache: could not measure: {ex.Message}");
            return (0, 0L);
        }
    }

    /// <summary>
    /// Deletes every fetched entry.
    /// </summary>
    /// <remarks>
    /// Only the <c>.json</c> entries. Hand-supplied <c>.lrc</c> files live in the same
    /// folder and are not cache — they cannot be re-fetched, so clearing the cache
    /// must never take them with it.
    /// </remarks>
    public int Clear()
    {
        int removed = 0;

        try
        {
            if (!Directory.Exists(_directory)) return 0;

            foreach (string file in Directory.EnumerateFiles(_directory, "*.json").ToList())
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"lyrics cache: could not delete {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"lyrics cache: clear failed: {ex.Message}");
        }

        return removed;
    }

    private string PathFor(string key) => Path.Combine(_directory, $"{key}.json");
}
