using System.Windows.Media;

namespace Barline.Media;

/// <summary>
/// Supplies the album art currently on display, and says when it changes.
/// </summary>
/// <remarks>
/// Exists so the settings window can preview the album-art colour mode without
/// depending on the overlay window. A plain getter is not enough: tracks change while
/// the settings window is open, and a preview that never hears about it silently shows
/// the colour of a song that stopped playing several tracks ago.
/// </remarks>
internal interface IAlbumArtSource
{
    ImageSource? CurrentAlbumArt { get; }

    /// <summary>Raised when <see cref="CurrentAlbumArt"/> becomes a different image.</summary>
    event EventHandler? AlbumArtChanged;
}
