namespace SoundLibert.Domain.Models;

public class ArtistEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string? ChannelId { get; set; }
    public DateTime? LastUpdated { get; set; }
    public DateTime? BookmarkedAt { get; set; }

    /// <summary>Subscribed in the YouTube Music account (imported, read-only side).</summary>
    public bool IsYouTubeSubscribed { get; set; }

    /// <summary>Unsubscribed in-app while still subscribed on YouTube (veto).</summary>
    public bool IsLocalUnsubscribed { get; set; }
}
