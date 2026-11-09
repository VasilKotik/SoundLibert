using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundLibert.Domain.Models;

namespace SoundLibert.UI.ViewModels;

/// <summary>
/// Full-page "Discography" for an artist: the albums, singles &amp; EPs,
/// "appears on" albums and artist playlists that used to be four separate rails
/// on the artist page, combined here behind category chips at the top.
/// </summary>
public partial class ArtistDiscographyViewModel : ViewModelBase
{
    public string ArtistName { get; private set; } = string.Empty;
    public string? ThumbnailUrl { get; private set; }

    public ObservableCollection<DiscographyCard> Items { get; } = new();
    public ObservableCollection<DiscographyChip> Chips { get; } = new();

    [ObservableProperty] private bool _hasItems;
    [ObservableProperty] private string _emptyMessage = "Nothing here yet.";

    private readonly Dictionary<DiscographyCategory, List<DiscographyCard>> _byCategory = new();

    /// <summary>Set by the shell: opens the tapped album/playlist page.</summary>
    public Action<DiscographyCard>? OpenRequest { get; set; }

    public void Load(
        string artistName, string? thumbnail,
        IReadOnlyList<AlbumItem> albums,
        IReadOnlyList<AlbumItem> singles,
        IReadOnlyList<AlbumItem> appearsOn,
        IReadOnlyList<PlaylistItem> playlists)
    {
        ArtistName = artistName;
        ThumbnailUrl = thumbnail;

        _byCategory[DiscographyCategory.Albums] =
            albums.Select(a => AlbumCard(a, DiscographyCategory.Albums, a.Year)).ToList();
        _byCategory[DiscographyCategory.Singles] =
            singles.Select(a => AlbumCard(a, DiscographyCategory.Singles, a.Year)).ToList();
        _byCategory[DiscographyCategory.AppearsOn] =
            appearsOn.Select(a => AlbumCard(a, DiscographyCategory.AppearsOn, a.ArtistDisplay)).ToList();
        _byCategory[DiscographyCategory.Playlists] =
            playlists.Select(p => new DiscographyCard
            {
                Id = p.Id,
                Title = p.Title,
                Subtitle = p.Author,
                CoverUrl = p.ThumbnailUrl,
                Category = DiscographyCategory.Playlists
            }).ToList();

        Chips.Clear();
        foreach (var (category, label) in new[]
                 {
                     (DiscographyCategory.Albums, "Albums"),
                     (DiscographyCategory.Singles, "Singles & EPs"),
                     (DiscographyCategory.AppearsOn, "Appears on"),
                     (DiscographyCategory.Playlists, "Playlists")
                 })
        {
            var count = _byCategory[category].Count;
            if (count == 0) continue;
            Chips.Add(new DiscographyChip { Label = label, Category = category, Count = count });
        }

        if (Chips.Count == 0)
        {
            Items.Clear();
            HasItems = false;
            EmptyMessage = "No releases found for this artist.";
            return;
        }

        Select(Chips[0]);
    }

    [RelayCommand]
    private void SelectChip(DiscographyChip? chip)
    {
        if (chip == null) return;
        Select(chip);
    }

    private void Select(DiscographyChip chip)
    {
        foreach (var c in Chips) c.IsActive = ReferenceEquals(c, chip);

        Items.Clear();
        _shownSource = _byCategory.GetValueOrDefault(chip.Category) ?? new List<DiscographyCard>();
        ShowMore();

        HasItems = Items.Count > 0;
        EmptyMessage = chip.Category switch
        {
            DiscographyCategory.Singles => "No singles or EPs.",
            DiscographyCategory.AppearsOn => "No appearances.",
            DiscographyCategory.Playlists => "No playlists.",
            _ => "No albums."
        };
    }

    private const int PageSize = 60;
    private IReadOnlyList<DiscographyCard> _shownSource = Array.Empty<DiscographyCard>();

    /// <summary>
    /// Adds the next cards to the page. An artist can have thousands of releases;
    /// building them as the page scrolls keeps opening it instant.
    /// </summary>
    public void ShowMore()
    {
        var end = Math.Min(Items.Count + PageSize, _shownSource.Count);
        for (var i = Items.Count; i < end; i++) Items.Add(_shownSource[i]);
    }

    private static DiscographyCard AlbumCard(AlbumItem album, DiscographyCategory category, string? subtitle) => new()
    {
        Id = album.Id,
        Title = album.Title,
        Subtitle = subtitle,
        CoverUrl = album.ThumbnailUrl,
        Category = category
    };
}
