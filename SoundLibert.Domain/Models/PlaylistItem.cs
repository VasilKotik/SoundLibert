namespace SoundLibert.Domain.Models;

public class PlaylistItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string? AuthorId { get; set; }
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string? SongCountText { get; set; }
    public bool IsEditable { get; set; }

    /// <summary>What YouTube says under the title: "Playlist • Author • 2.2M views".</summary>
    public string? Subtitle { get; set; }
}
