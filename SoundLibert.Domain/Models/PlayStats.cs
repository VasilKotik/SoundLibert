namespace SoundLibert.Domain.Models;

public class PlayStats
{
    public int TotalPlays { get; set; }
    public TimeSpan TotalListeningTime { get; set; }
    public int UniqueTracks { get; set; }
    public TimeSpan AverageTrackLength { get; set; }
    public IReadOnlyList<TopArtist> TopArtists { get; set; } = Array.Empty<TopArtist>();
    public IReadOnlyList<TopTrack> TopTracks { get; set; } = Array.Empty<TopTrack>();
    public IReadOnlyList<TopAlbum> TopAlbums { get; set; } = Array.Empty<TopAlbum>();
    public IReadOnlyList<TopGenre> TopGenres { get; set; } = Array.Empty<TopGenre>();
    public double AverageEnergy { get; set; }
    public double DiscoveryRate { get; set; }
}

public class TopTrack
{
    public string SongId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Artist { get; set; }
    public string? ThumbnailUrl { get; set; }
    public int PlayCount { get; set; }
}

public class TopArtist
{
    public string Name { get; set; } = string.Empty;
    public int PlayCount { get; set; }
}

public class TopAlbum
{
    public string Name { get; set; } = string.Empty;
    public string? Artist { get; set; }
    public string? ThumbnailUrl { get; set; }
    public int PlayCount { get; set; }
}

public class TopGenre
{
    public string Name { get; set; } = string.Empty;
    public int PlayCount { get; set; }
}
