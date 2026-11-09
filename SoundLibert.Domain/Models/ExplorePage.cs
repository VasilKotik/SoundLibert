namespace SoundLibert.Domain.Models;

/// <summary>YouTube Music's Explore: what is new and what is moving right now.</summary>
public class ExplorePage
{
    /// <summary>"New albums &amp; singles".</summary>
    public Shelf? NewReleases { get; set; }

    /// <summary>The trending songs, ranked.</summary>
    public Shelf? Trending { get; set; }

    /// <summary>"New music videos".</summary>
    public Shelf? NewVideos { get; set; }
}
