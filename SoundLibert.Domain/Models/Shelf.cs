namespace SoundLibert.Domain.Models;

public enum ShelfItemKind { Song, Video, Album, Playlist, Artist }

/// <summary>Where a chart entry is heading.</summary>
public enum ChartTrend { None, Up, Down, Same }

/// <summary>One entry of a shelf: anything YouTube Music can show on a page.</summary>
public sealed class ShelfItem
{
    public ShelfItemKind Kind { get; init; }
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    /// <summary>What YouTube says under the title: "Single • 2025", "33.4M subscribers", "48M plays".</summary>
    public string Subtitle { get; init; } = string.Empty;

    public string ThumbnailUrl { get; init; } = string.Empty;

    /// <summary>The playable row, for songs and videos.</summary>
    public SongItem? Song { get; init; }

    /// <summary>The place on a chart.</summary>
    public int? Rank { get; init; }

    public ChartTrend Trend { get; init; }
}

public enum ShelfLayout
{
    /// <summary>Covers side by side.</summary>
    Cards,

    /// <summary>Compact rows in columns: quick picks, charts.</summary>
    Rows
}

/// <summary>A titled row of a page.</summary>
public sealed class Shelf
{
    public string Title { get; init; } = string.Empty;
    public ShelfLayout Layout { get; init; }
    public IReadOnlyList<ShelfItem> Items { get; init; } = Array.Empty<ShelfItem>();

    /// <summary>Where its "Show all" leads, when YouTube has more.</summary>
    public string? MoreBrowseId { get; init; }

    public string? MoreParams { get; init; }
}

/// <summary>A page of shelves: a mood, a genre, the whole of a shelf.</summary>
public sealed class ShelfPage
{
    public string Title { get; init; } = string.Empty;
    public IReadOnlyList<Shelf> Shelves { get; init; } = Array.Empty<Shelf>();
}

/// <summary>A mood or genre to explore.</summary>
public sealed class MoodTile
{
    public string Name { get; init; } = string.Empty;
    public string BrowseId { get; init; } = string.Empty;
    public string? Params { get; init; }

    /// <summary>YouTube's colour for it (ARGB), when it has one.</summary>
    public uint? Color { get; init; }
}

public sealed class MoodCategory
{
    public string Title { get; init; } = string.Empty;
    public IReadOnlyList<MoodTile> Tiles { get; init; } = Array.Empty<MoodTile>();
}

/// <summary>What YouTube Music relates to a song.</summary>
/// <summary>What an album or playlist page has besides its tracks.</summary>
public sealed class CollectionExtras
{
    public IReadOnlyList<Shelf> Shelves { get; init; } = Array.Empty<Shelf>();

    /// <summary>Who the page names as its author. An album's rows do not say.</summary>
    public Artist? Artist { get; init; }
}

public sealed class SongRelated
{
    public IReadOnlyList<Shelf> Shelves { get; init; } = Array.Empty<Shelf>();

    /// <summary>The story of the song's artist, when it has one.</summary>
    public string? About { get; init; }
}

/// <summary>Everything a search finds, for the "All" results.</summary>
public sealed class SearchOverview
{
    /// <summary>The best match: an artist, an album, a song.</summary>
    public ShelfItem? TopResult { get; init; }

    /// <summary>The songs YouTube shows with the best match.</summary>
    public IReadOnlyList<ShelfItem> TopResultSongs { get; init; } = Array.Empty<ShelfItem>();

    public IReadOnlyList<Shelf> Songs { get; init; } = Array.Empty<Shelf>();
    public IReadOnlyList<Shelf> Videos { get; init; } = Array.Empty<Shelf>();
    public IReadOnlyList<Shelf> Artists { get; init; } = Array.Empty<Shelf>();
    public IReadOnlyList<Shelf> Albums { get; init; } = Array.Empty<Shelf>();
    public IReadOnlyList<Shelf> Playlists { get; init; } = Array.Empty<Shelf>();

    public bool HasAnything => TopResult != null ||
        Songs.Count + Videos.Count + Artists.Count + Albums.Count + Playlists.Count > 0;
}

/// <summary>What the search box offers while typing.</summary>
public sealed class SearchSuggestions
{
    /// <summary>Searches to run.</summary>
    public IReadOnlyList<string> Texts { get; init; } = Array.Empty<string>();

    /// <summary>Things to open right away: an artist, a song.</summary>
    public IReadOnlyList<ShelfItem> Items { get; init; } = Array.Empty<ShelfItem>();
}
