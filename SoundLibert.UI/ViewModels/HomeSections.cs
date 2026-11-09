using CommunityToolkit.Mvvm.ComponentModel;

namespace SoundLibert.UI.ViewModels;

/// <summary>A top-artist tile on Home (name + plays + cover, resolved lazily).</summary>
public sealed partial class HomeArtistItem : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int PlayCount { get; set; }

    [ObservableProperty] private string? _thumbnailUrl;

    public string PlayCountText => PlayCount == 1 ? "1 play" : $"{PlayCount} plays";
}

/// <summary>A video to start right away, by its id alone.</summary>
public sealed class VideoCard
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string ThumbnailUrl { get; init; } = string.Empty;

    /// <summary>Length in seconds (0 when unknown).</summary>
    public int DurationSeconds { get; init; }
}
