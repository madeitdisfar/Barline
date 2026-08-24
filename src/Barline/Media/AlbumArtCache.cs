using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Barline.Diagnostics;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Barline.Media;

/// <summary>
/// Decodes and caches album art from SMTC thumbnails.
/// </summary>
/// <remarks>
/// <para>
/// MediaPropertiesChanged fires more often than the track actually changes, and some
/// apps raise it on every position update, so decoding is cached by track identity
/// to keep it off the hot path.
/// </para>
/// <para>
/// Decoded images are frozen so they can cross from the decode thread to the UI
/// thread, which also lets WPF render them without per-frame locking.
/// </para>
/// </remarks>
internal sealed class AlbumArtCache
{
    private const int MaxEntries = 8;

    /// <summary>
    /// The widest the art is ever decoded, in pixels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The art is drawn into a 32 by 32 logical square and nowhere else, and the
    /// largest scale the package carries assets for is 400%, so 128 pixels is the most
    /// any display can ask for. The palette wants less still: it scales whatever it is
    /// given down to 32 before sampling a hue from it.
    /// </para>
    /// <para>
    /// Sources hand out far more than that. A 640 by 640 cover is ordinary and 1400 is
    /// not rare, and at eight cached entries the difference is tens of megabytes of
    /// unmanaged decode buffers held to draw a thumbnail. Decoding to the size actually
    /// wanted puts an entry at about 64 KB.
    /// </para>
    /// </remarks>
    private const int DecodeWidth = 128;

    private readonly Dictionary<string, ImageSource?> _cache = new();
    private readonly Queue<string> _insertionOrder = new();
    private readonly object _gate = new();

    public async Task<ImageSource?> GetAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        string key = $"{props.Title}{props.Artist}{props.AlbumTitle}";

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;
        }

        var decoded = await DecodeAsync(props.Thumbnail).ConfigureAwait(false);

        lock (_gate)
        {
            // A concurrent refresh may have populated it already.
            if (_cache.TryGetValue(key, out var raced))
                return raced;

            _cache[key] = decoded;
            _insertionOrder.Enqueue(key);
            while (_insertionOrder.Count > MaxEntries)
                _cache.Remove(_insertionOrder.Dequeue());
        }

        return decoded;
    }

    private static async Task<ImageSource?> DecodeAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null) return null;

        try
        {
            using var winrtStream = await thumbnail.OpenReadAsync();

            // BitmapImage needs a seekable .NET stream, so the WinRT stream is
            // copied into memory rather than decoded in place.
            using var buffer = new MemoryStream();
            await winrtStream.AsStreamForRead().CopyToAsync(buffer).ConfigureAwait(false);
            buffer.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;   // detach from the stream
            bitmap.StreamSource = buffer;

            // Width alone, so the decoder keeps the aspect ratio. This is the size
            // decoded to rather than a ceiling, so a thumbnail smaller than this is
            // scaled up to it. That costs about 48 KB and changes nothing on screen,
            // since anything below 128 was going to be scaled up to be drawn anyway.
            bitmap.DecodePixelWidth = DecodeWidth;

            bitmap.EndInit();
            bitmap.Freeze();                                  // cross-thread handoff

            return bitmap;
        }
        catch (Exception ex)
        {
            // A missing or malformed thumbnail is normal for some sources; the
            // widget falls back to a placeholder rather than failing.
            DebugLog.Write($"album art decode failed: {ex.Message}");
            return null;
        }
    }
}
