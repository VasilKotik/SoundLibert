namespace SoundLibert.Domain.Models;

/// <summary>An artist the listener chose on the welcome screen. Home's personal shelves start from them.</summary>
public sealed class FavouriteArtist
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
}
