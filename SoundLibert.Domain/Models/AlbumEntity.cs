namespace SoundLibert.Domain.Models;

public class AlbumEntity
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? PlaylistId { get; set; }
    public string ArtistsJson { get; set; } = "[]";
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string? Year { get; set; }
    public bool IsExplicit { get; set; }
    public DateTime? BookmarkedAt { get; set; }

    /// <summary>Saved in the YouTube Music library (imported, read-only side).</summary>
    public bool IsYouTubeLiked { get; set; }

    /// <summary>Unliked in-app while still saved on YouTube (veto, like songs).</summary>
    public bool IsLocalUnliked { get; set; }
}
