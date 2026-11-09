namespace SoundLibert.Domain.Models;

/// <summary>
/// A fully parsed artist page (port of Velune's artist browse): the artist
/// header plus the shelves YouTube Music returns — top songs, albums, singles
/// and "fans might also like" related artists. Every item keeps its browse id
/// so the UI can cross-link artist → album → song → related artist.
/// </summary>
public class ArtistPage
{
    public ArtistItem Artist { get; set; } = new();
    public string? Description { get; set; }
    public IReadOnlyList<SongItem> Songs { get; set; } = Array.Empty<SongItem>();
    public IReadOnlyList<AlbumItem> Albums { get; set; } = Array.Empty<AlbumItem>();
    public IReadOnlyList<AlbumItem> Singles { get; set; } = Array.Empty<AlbumItem>();

    /// <summary>Albums the artist appears on ("Featured on" / "Appears on").</summary>
    public IReadOnlyList<AlbumItem> AppearsOn { get; set; } = Array.Empty<AlbumItem>();

    /// <summary>Playlists curated by the artist ("Playlists by …").</summary>
    public IReadOnlyList<PlaylistItem> Playlists { get; set; } = Array.Empty<PlaylistItem>();

    public IReadOnlyList<ArtistItem> RelatedArtists { get; set; } = Array.Empty<ArtistItem>();

    /// <summary>The Videos shelf, ready to play as videos.</summary>
    public IReadOnlyList<SongItem> Videos { get; set; } = Array.Empty<SongItem>();

    /// <summary>The "Live performances" shelf.</summary>
    public IReadOnlyList<SongItem> LivePerformances { get; set; } = Array.Empty<SongItem>();

    /// <summary>"535M monthly audience".</summary>
    public string? MonthlyAudienceText { get; set; }

    /// <summary>"33.4M subscribers".</summary>
    public string? SubscribersText { get; set; }

    /// <summary>"23.7B views": everything the artist has on YouTube.</summary>
    public string? ViewsText { get; set; }

    /// <summary>The whole "Top songs" list, when YouTube has one.</summary>
    public string? TopSongsListId { get; set; }

    public bool HasAnything =>
        Songs.Count > 0 || Albums.Count > 0 || Singles.Count > 0 || RelatedArtists.Count > 0;
}
