namespace SoundLibert.Domain.Models;

public class SearchResult
{
    public IReadOnlyList<SongItem> Songs { get; set; } = Array.Empty<SongItem>();
    public IReadOnlyList<AlbumItem> Albums { get; set; } = Array.Empty<AlbumItem>();
    public IReadOnlyList<ArtistItem> Artists { get; set; } = Array.Empty<ArtistItem>();
    public IReadOnlyList<PlaylistItem> Playlists { get; set; } = Array.Empty<PlaylistItem>();
    public string? ContinuationToken { get; set; }
}
