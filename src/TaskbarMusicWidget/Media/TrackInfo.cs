using System.Windows.Media;

namespace TaskbarMusicWidget.Media;

/// <summary>
/// An immutable snapshot of what is currently playing, flattened from the several
/// WinRT objects that describe a media session.
/// </summary>
/// <remarks>
/// <see cref="AlbumArt"/> is always frozen, because it is decoded off the UI thread.
/// </remarks>
internal sealed record TrackInfo
{
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string AlbumTitle { get; init; } = string.Empty;

    public bool IsPlaying { get; init; }

    public ImageSource? AlbumArt { get; init; }

    /// <summary>
    /// Transport capabilities reported by the source app. Spotify, browsers and
    /// podcast apps expose different subsets, so buttons are driven from these
    /// rather than assumed.
    /// </summary>
    public bool CanGoNext { get; init; }
    public bool CanGoPrevious { get; init; }
    public bool CanPlayPause { get; init; }

    /// <summary>AUMID of the owning app, used to focus it when the widget is clicked.</summary>
    public string? SourceAppId { get; init; }

    public bool HasContent => !string.IsNullOrWhiteSpace(Title);
}
