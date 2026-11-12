using SoundLibert.UI.Services;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SoundLibert.Domain.Models;
using SoundLibert.UI.Controls;
using SoundLibert.UI.ViewModels;

namespace SoundLibert.UI.Views;

public partial class LibraryView : UserControl
{
    private LibraryViewModel? _vm;
    private int _fadeGen;

    private readonly ScrollHeader _header;

    public LibraryView()
    {
        InitializeComponent();
        _header = ScrollHeader.Attach(DrillHeader, DrillCompact, TracksList, ArtistsScroll, AlbumsScroll);
        DataContextChanged += (_, _) => Attach();
    }

    private void Attach()
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as LibraryViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LibraryViewModel.IsCollectionOpen)) return;
        // Drill-in entrance fades; back to the grid also fades. Either target
        // is only realised while visible, so fade the one that just appeared.
        var target = _vm?.IsCollectionOpen == true ? (Control?)DrillIn : LibraryRoot;
        if (_vm?.IsCollectionOpen == true) _header.Reset();
        FadeIn(target);
    }

    /// <summary>Proven fade pattern (declarative transition, final state posted).</summary>
    private void FadeIn(Control? target)
    {
        if (target == null) return;
        var gen = ++_fadeGen;
        var transitions = target.Transitions;
        target.Transitions = null;
        target.Opacity = 0;
        target.Transitions = transitions;
        Dispatcher.UIThread.Post(() =>
        {
            if (gen == _fadeGen) target.Opacity = 1;
        }, DispatcherPriority.Background);
    }

    private MainWindowViewModel? Host =>
        SoundLibert.UI.Services.AppServices.HostOf(this);

    private void LibraryCard_Tapped(object? sender, TappedEventArgs e)
    {
        if (InputGuards.FromButtonInteracted(e)) return;
        if (sender is Control { DataContext: LibraryCard card } && DataContext is LibraryViewModel vm)
            vm.OpenCard(card);
    }

    private void LibraryTrack_Tapped(object? sender, TappedEventArgs e)
    {
        if (InputGuards.FromButtonInteracted(e)) return;
        if (sender is not Control { DataContext: SongItem song } ||
            DataContext is not LibraryViewModel vm) return;

        // Routing lives in the VM: clip rows (badge) open as video, the rest
        // join the song queue.
        vm.PlayCollectionTrack(song);
    }

    private void GenreChip_Click(object? sender, RoutedEventArgs e)
    {
        // Genre mix inside the open playlist: filter its own songs by genre.
        if (sender is Button { Tag: GenreChip chip } && DataContext is LibraryViewModel vm)
            vm.FilterGenreCommand.Execute(chip);
    }

    private void DrillArtist_Tapped(object? sender, TappedEventArgs e)
    {
        if (InputGuards.FromButtonInteracted(e)) return;
        if (sender is Control { DataContext: ArtistItem artist })
            Host?.OpenArtist(artist.Id, artist.Name);
    }

    private void DrillAlbum_Tapped(object? sender, TappedEventArgs e)
    {
        if (InputGuards.FromButtonInteracted(e)) return;
        if (sender is Control { DataContext: AlbumItem album } && Host != null)
            _ = Host.OpenBrowseAsync(album.Id, album.Title, album.ThumbnailUrl, album.ArtistDisplay);
    }

    private async void LikeTrack_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SongItem song } button ||
            Host is null || DataContext is not LibraryViewModel vm) return;

        var wasLiked = song.IsLiked;
        await vm.ToggleLikeCommand.ExecuteAsync(song);
        if (!wasLiked && song.IsLiked)
            SoundLibert.UI.Services.TrackMenu.ShowAddToPlaylist(button, song, Host);
    }

    private void More_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SongItem song } button && Host != null && DataContext is LibraryViewModel vm)
            SoundLibert.UI.Services.TrackMenu.Show(button, song, Host,
                vm.IsOpenPlaylistEditable ? (Func<SongItem, Task>)(s => vm.RemovePlaylistTrackCommand.ExecuteAsync(s)) : null);
    }

    private async void CreatePlaylist_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm) return;

        var name = await AppServices.Shell.AskAsync(this, "New playlist", placeholder: "Playlist name");
        if (string.IsNullOrWhiteSpace(name)) return;

        await vm.CreatePlaylistCommand.ExecuteAsync(name);

        // Keep the track menu's playlist cache in sync.
        if (Host != null) await Host.RefreshPlaylistsAsync();
    }

    private void LibraryCardMore_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: LibraryCard card } button) return;
        if (card.Playlist is not { } playlist || DataContext is not LibraryViewModel vm) return;

        var menu = new MenuFlyout();

        if (playlist.IsEditable)
        {
            menu.Items.Add(SoundLibert.UI.Services.TrackMenu.Item("Rename", IconKind.Edit,
                () => _ = AfterPlaylistChangeAsync(RenamePlaylistAsync(playlist, vm))));
            menu.Items.Add(SoundLibert.UI.Services.TrackMenu.Item("Duplicate", IconKind.Duplicate,
                () => _ = AfterPlaylistChangeAsync(vm.DuplicatePlaylistCommand.ExecuteAsync(playlist))));
            menu.Items.Add(SoundLibert.UI.Services.TrackMenu.Item("Delete playlist", IconKind.Trash,
                () => _ = AfterPlaylistChangeAsync(DeletePlaylistAsync(playlist, vm))));
        }

        menu.ShowAt(button);
    }

    /// <summary>The track menu lists the playlists too: a deleted one must not stay an "Add to" target.</summary>
    private async Task AfterPlaylistChangeAsync(Task change)
    {
        await change;
        if (Host != null) await Host.RefreshPlaylistsAsync();
    }

    private async Task RenamePlaylistAsync(PlaylistEntity playlist, LibraryViewModel vm)
    {
        var name = await AppServices.Shell.AskAsync(this, "Rename playlist", initial: playlist.Title);
        if (!string.IsNullOrWhiteSpace(name))
            await vm.RenamePlaylistCommand.ExecuteAsync((playlist, name));
    }

    /// <summary>Deleting cannot be undone, so it asks first.</summary>
    private async Task DeletePlaylistAsync(PlaylistEntity playlist, LibraryViewModel vm)
    {
        if (await AppServices.Shell.ConfirmAsync(this, "Delete this playlist?",
                $"“{playlist.Title}” will be removed from SoundLibert. The songs themselves stay available.",
                "Delete"))
        {
            await vm.DeletePlaylistCommand.ExecuteAsync(playlist);
        }
    }

    private async void ImportLocal_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm) return;

        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is not { } storage) return;

        var folders = await storage.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Choose a music folder to import",
            AllowMultiple = false
        });

        var folder = folders.Count > 0 ? folders[0].Path.LocalPath : null;
        if (!string.IsNullOrEmpty(folder))
            await vm.ImportLocalCommand.ExecuteAsync(folder);
    }
}
