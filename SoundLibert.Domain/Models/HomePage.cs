namespace SoundLibert.Domain.Models;

/// <summary>A portion of the YouTube Music home feed.</summary>
public class HomePage
{
    public IReadOnlyList<Shelf> Shelves { get; set; } = Array.Empty<Shelf>();

    /// <summary>The moods the feed can be narrowed to (first portion only).</summary>
    public IReadOnlyList<HomeChip> Chips { get; set; } = Array.Empty<HomeChip>();

    /// <summary>The next portion, when there is one.</summary>
    public string? ContinuationToken { get; set; }
}

public class HomeChip
{
    public string Text { get; set; } = string.Empty;
    public string? Params { get; set; }
}

public abstract class YTItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
}
