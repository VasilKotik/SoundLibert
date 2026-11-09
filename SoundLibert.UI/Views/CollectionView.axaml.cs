using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SoundLibert.Domain.Models;
using SoundLibert.UI.Controls;
using SoundLibert.UI.ViewModels;

namespace SoundLibert.UI.Views;

public partial class CollectionView : UserControl
{
    public CollectionView()
    {
        InitializeComponent();
        ScrollHeader.Attach(Header, Compact, SongsList).WithFooter(Footer);
    }

    private MainWindowViewModel? Host =>
        (TopLevel.GetTopLevel(this) as Window)?.DataContext as MainWindowViewModel;

    private void GoToArtist_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CollectionViewModel vm && Host is { } host)
            host.OpenArtist(vm.ArtistId, vm.ArtistName);
    }

    private void GenreChip_Click(object? sender, RoutedEventArgs e)
    {
        // Genre mix on the open playlist: filter its own songs by genre.
        if (sender is Button { Tag: GenreChip chip } && DataContext is CollectionViewModel vm)
            vm.FilterGenreCommand.Execute(chip);
    }

    private void Song_Tapped(object? sender, TappedEventArgs e)
    {
        if (InputGuards.FromButtonInteracted(e)) return;
        if (sender is Control { DataContext: SongItem song } && DataContext is CollectionViewModel vm)
            vm.PlaySongCommand.Execute(song);
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
        if (sender is not Button { Tag: SongItem song } button || Host == null) return;

        // In a playlist the user owns, the row menu also moves and removes.
        SoundLibert.UI.Services.TrackMenu.PlaylistActions? own = null;
        if (song.IsLocalPlaylist && DataContext is CollectionViewModel vm)
        {
            own = new SoundLibert.UI.Services.TrackMenu.PlaylistActions(
                s => vm.RemoveFromPlaylistCommand.ExecuteAsync(s),
                s => vm.MoveSongCommand.Execute((s, -1)),
                s => vm.MoveSongCommand.Execute((s, 1)));
        }
        SoundLibert.UI.Services.TrackMenu.Show(button, song, Host, own);
    }
}
