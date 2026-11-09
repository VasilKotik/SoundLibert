using CommunityToolkit.Mvvm.ComponentModel;
using SoundLibert.Domain.Models;
using SoundLibert.UI.Controls;

namespace SoundLibert.UI.ViewModels;

/// <summary>A selectable genre filter chip inside a playlist (genre mix).</summary>
public partial class GenreChip : ObservableObject
{
    public required string Label { get; init; }

    /// <summary>Genre-string keywords this chip matches (case-insensitive).</summary>
    public required string[] Keywords { get; init; }

    [ObservableProperty] private bool _isActive;

    /// <summary>
    /// Builds a chip from an ACTUAL resolved genre string (iTunes
    /// <c>primaryGenreName</c>), so a playlist only shows the styles it really
    /// contains instead of a fixed list of popular genres.
    /// </summary>
    public static GenreChip FromGenre(string genre) => new()
    {
        Label = genre,
        Keywords = new[] { genre.ToLowerInvariant() }
    };
}

/// <summary>Which built-in collection a library card represents.</summary>
public enum LibrarySection
{
    Liked,
    Recent,
    Local,
    Downloads,
    History,
    Artists,
    Albums,
    Playlist
}

/// <summary>
/// One cover tile in the Library grid: either a built-in collection (Liked /
/// Recent / Local / History) or a user/YouTube playlist. Spotify-style.
/// </summary>
public sealed class LibraryCard
{
    public required LibrarySection Section { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }

    /// <summary>Set for playlist cards.</summary>
    public PlaylistEntity? Playlist { get; init; }

    /// <summary>Icon of a built-in collection.</summary>
    public IconKind Icon { get; init; } = IconKind.Music;

    /// <summary>Cover image URL for playlist cards.</summary>
    public string? CoverUrl { get; init; }

    /// <summary>First distinct track covers of a local playlist (2×2 mosaic at 4+).</summary>
    public IReadOnlyList<string>? CoverUrls { get; init; }

    public bool IsSpecial => Section != LibrarySection.Playlist;

    /// <summary>Only user-owned playlists expose the ⋯ menu.</summary>
    public bool CanManage => Section == LibrarySection.Playlist && Playlist is { IsEditable: true };
}
