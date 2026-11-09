namespace SoundLibert.Domain.Models;

/// <summary>One row of the listening history, joined with the song metadata.</summary>
public class PlayHistoryItem
{
    public string SongId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Artist { get; set; }
    public string? ThumbnailUrl { get; set; }
    public int DurationSeconds { get; set; }
    public DateTime Timestamp { get; set; }
}
