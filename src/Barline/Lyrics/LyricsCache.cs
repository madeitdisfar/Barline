using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Barline.Diagnostics;

namespace Barline.Lyrics;

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

    /// <summary>
    /// How many times this track has been looked up and come back with nothing. Zero
    /// for a hit, and zero for an entry written before this was recorded — which only
    /// means the next retry comes sooner than it strictly had to.
    /// </summary>
    public int Misses { get; set; }

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
/// Misses are cached as deliberately as hits, but never permanently. LRCLIB is a
/// contributed database: a track with nothing filed today is a track nobody has got to
/// yet, and new songs are exactly the ones most likely to be filled in shortly after
/// they are asked for. A miss that never expired would mean a track stayed silent for
/// as long as the widget was installed, long after the lyrics existed.
/// </para>
/// <para>
/// So a miss is retried on a widening delay rather than at one flat lifetime. The first
/// retry comes the next day, which catches the common case; a track that keeps coming
/// back empty settles into being asked about once a month, which is what stops a
/// library of instrumentals from hammering a service run for free.
/// </para>
/// <para>
/// Reads never throw. A corrupt or half-written entry is treated as absent and
/// overwritten by the next fetch, exactly as the settings file is.
/// </para>
/// </remarks>
internal sealed class LyricsCache
{
    /// <summary>
    /// How long to trust a recorded miss, by how many times it has already missed.
    /// </summary>
    /// <remarks>
    /// The last entry is the standing interval once the ladder runs out. Nothing is
    /// permanent: even a track that has come back empty ten times is asked again
    /// eventually, because the only cost of being wrong about that is one request.
    /// </remarks>
    internal static readonly TimeSpan[] MissBackoff =
    [
        TimeSpan.FromDays(1),
        TimeSpan.FromDays(3),
        TimeSpan.FromDays(7),
        TimeSpan.FromDays(30),
    ];

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
            "Barline",
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

    /// <summary>
    /// Reads whatever is stored for a key, fresh or stale, or null when there is
    /// nothing readable there.
    /// </summary>
    /// <remarks>
    /// Stale entries come back rather than being hidden, because a stale miss still
    /// carries how many times this track has already come back empty — which is what
    /// decides when to ask again. Callers pass what they get to
    /// <see cref="IsUsable"/> before treating it as an answer.
    /// </remarks>
    public CachedLyrics? Read(string key)
    {
        string path = PathFor(key);

        try
        {
            if (!File.Exists(path)) return null;

            return JsonSerializer.Deserialize<CachedLyrics>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            DebugLog.Write($"lyrics cache: unreadable entry {key}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Whether a stored entry can still be used as the answer for a track.</summary>
    public static bool IsUsable(CachedLyrics entry, DateTimeOffset now)
    {
        // Written before a field we now store existed. Treated as unusable so it is
        // fetched once more; the rewrite stamps the current schema, so this cannot turn
        // into a re-fetch on every play.
        if (entry.Schema < CurrentSchema) return false;

        // A hit is kept forever: lyrics for a released track do not change.
        if (entry.Found) return true;

        return now - entry.FetchedUtc < RetryAfter(entry.Misses);
    }

    /// <summary>How long to wait before asking again about a track that has missed.</summary>
    internal static TimeSpan RetryAfter(int misses) =>
        MissBackoff[Math.Clamp(misses - 1, 0, MissBackoff.Length - 1)];

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
