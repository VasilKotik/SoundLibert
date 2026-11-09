namespace SoundLibert.Domain.Models;

public class PlayEvent
{
    public long Id { get; set; }
    public string SongId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public int PlayDurationMs { get; set; }
    public string? PlaylistId { get; set; }
}
