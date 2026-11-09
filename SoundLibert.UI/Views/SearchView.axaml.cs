using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SoundLibert.Domain.Enums;
using SoundLibert.Domain.Models;
using SoundLibert.UI.Controls;
using SoundLibert.UI.ViewModels;

namespace SoundLibert.UI.Views;

public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
        ResultsScroll.ScrollChanged += (_, _) => ResultsEdge.Opacity = Math.Clamp(ResultsScroll.Offset.Y / 40, 0, 1);
        SetActiveChip("All");
        // The view is rebuilt on each visit while the filter lives in the view
        // model: the highlighted chip must show the filter really in use.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not SearchViewModel vm) return;
            SetActiveChip(ChipFor(vm.SelectedFilter));
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SearchViewModel.SelectedFilter)) SetActiveChip(ChipFor(vm.SelectedFilter));
            };
        };
    }

    private static string ChipFor(SearchFilter filter) => filter switch
    {
        SearchFilter.Videos => "Videos",
        SearchFilter.Albums => "Albums",
        SearchFilter.Artists => "Artists",
        SearchFilter.CommunityPlaylists or SearchFilter.FeaturedPlaylists => "Playlists",
        SearchFilter.Songs => "Songs",
        _ => "All"
    };

    private MainWindowViewModel? Host =>
        (TopLevel.GetTopLevel(this) as Window)?.DataContext as MainWindowViewModel;

    private void Song_Tapped(object? sender, TappedEventArgs e)
    {
        if (InputGuards.FromButtonInteracted(e)) return;
        if (sender is Control { DataContext: SongItem song } && DataContext is SearchViewModel vm)
            vm.PlaySongCommand.Execute(song);
    }

    private void Album_Tapped(object? sender, TappedEventArgs e)
    {
        if (InputGuards.FromButtonInteracted(e)) return;
        if (sender is Control { DataContext: AlbumItem album })
            _ = Host?.OpenBrowseAsync(album.Id, album.Title, album.ThumbnailUrl, album.ArtistDisplay);
    }

    private void Artist_Tapped(object? sender, TappedEventArgs e)
    {
        if (InputGuards.FromButtonInteracted(e)) return;
        if (sender is Control { DataContext: ArtistItem artist })
            _ = Host?.OpenBrowseAsync(artist.Id, artist.Name, artist.ThumbnailUrl, "Artist");
    }

    private void Playlist_Tapped(object? sender, TappedEventArgs e)
    {
        if (InputGuards.FromButtonInteracted(e)) return;
        if (sender is Control { DataContext: PlaylistItem playlist })
            _ = Host?.OpenBrowseAsync(playlist.Id, playlist.Title, playlist.ThumbnailUrl, null);
    }

    private async void Like_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SongItem song } button) return;

        var wasLiked = song.IsLiked;
        await Host!.ToggleLikeAsync(song);
        if (!wasLiked && song.IsLiked)
            SoundLibert.UI.Services.TrackMenu.ShowAddToPlaylist(button, song, Host!);
    }

    private void More_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SongItem song } button && Host != null)
            SoundLibert.UI.Services.TrackMenu.Show(button, song, Host);
    }

    private void Card_Tapped(object? sender, TappedEventArgs e)
    {
        if (InputGuards.FromButtonInteracted(e)) return;
        if (sender is Control { Tag: ShelfCard card }) Host?.OpenShelfCard(card);
    }

    private void TopResult_Tapped(object? sender, TappedEventArgs e)
    {
        if (InputGuards.FromButtonInteracted(e)) return;
        if (DataContext is SearchViewModel { TopResult: { } card }) Host?.OpenShelfCard(card);
    }

    private void TopResultPlay_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is SearchViewModel { TopResult: { } card }) Host?.PlayShelfCard(card);
    }

    private void Suggestion_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string text } && DataContext is SearchViewModel vm)
            vm.UseSuggestionCommand.Execute(text);
    }

    private void RecentSearch_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Content is string query
            && DataContext is SearchViewModel vm)
        {
            vm.UseRecentSearchCommand.Execute(query);
        }
    }

    private void FilterButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && DataContext is SearchViewModel vm && button.Tag is string tag)
        {
            vm.SelectedFilter = tag switch
            {
                "Songs" => SearchFilter.Songs,
                "Videos" => SearchFilter.Videos,
                "Albums" => SearchFilter.Albums,
                "Artists" => SearchFilter.Artists,
                "Playlists" => SearchFilter.CommunityPlaylists,
                _ => SearchFilter.All
            };
            SetActiveChip(tag);
            vm.SearchCommand.Execute(null);
        }
    }

    private void SetActiveChip(string tag)
    {
        foreach (var child in FilterBar.Children)
            if (child is Button b && b.Tag is string t)
                b.Classes.Set("active", t == tag);
    }
}
