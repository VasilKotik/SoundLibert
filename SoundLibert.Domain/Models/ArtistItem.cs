namespace SoundLibert.Domain.Models;

public class ArtistItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string? ChannelId { get; set; }

    /// <summary>What YouTube says under the name: "14M monthly audience".</summary>
    public string? Subtitle { get; set; }
}
