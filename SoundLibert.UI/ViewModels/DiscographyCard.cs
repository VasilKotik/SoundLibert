using CommunityToolkit.Mvvm.ComponentModel;

namespace SoundLibert.UI.ViewModels;

/// <summary>Which shelf of an artist's discography an item belongs to.</summary>
public enum DiscographyCategory
{
    Albums,
    Singles,
    AppearsOn,
    Playlists
}

/// <summary>
/// One discography tile on the artist page and the full-page discography view:
/// a uniform card whatever the underlying item (album / single / appears-on
/// album / playlist), so one grid template renders them all.
/// </summary>
public sealed class DiscographyCard
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string? CoverUrl { get; init; }
    public DiscographyCategory Category { get; init; }
}

/// <summary>A category filter chip on the discography page (Albums / Singles &amp; EPs / …).</summary>
public partial class DiscographyChip : ObservableObject
{
    public required string Label { get; init; }
    public DiscographyCategory Category { get; init; }
    public int Count { get; init; }

    public string LabelWithCount => Count > 0 ? $"{Label} · {Count}" : Label;

    [ObservableProperty] private bool _isActive;
}
