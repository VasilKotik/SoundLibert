namespace SoundLibert.Domain.Models;

public class AlbumItem : YTItem
{
    public new string Id { get; set; } = string.Empty;
    public new string Title { get; set; } = string.Empty;
    public string? PlaylistId { get; set; }
    public IReadOnlyList<Artist> Artists { get; set; } = Array.Empty<Artist>();
    public new string ThumbnailUrl { get; set; } = string.Empty;
    public string? Year { get; set; }
    public bool IsExplicit { get; set; }

    /// <summary>What YouTube says under the title: "Single • 2025".</summary>
    public string? Subtitle { get; set; }

    public string ArtistDisplay => string.Join(", ", Artists.Select(a => a.Name));
}
