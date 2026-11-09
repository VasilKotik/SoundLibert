namespace SoundLibert.Domain.Enums;

public enum SearchFilter
{
    Songs,
    Videos,
    Albums,
    Artists,
    FeaturedPlaylists,
    CommunityPlaylists,

    /// <summary>Everything at once: the best match and a shelf of every kind.</summary>
    All
}
