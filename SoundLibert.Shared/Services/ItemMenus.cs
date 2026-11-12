using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SoundLibert.Domain.Models;
using SoundLibert.UI.Controls;
using SoundLibert.UI.ViewModels;

namespace SoundLibert.UI.Services;

/// <summary>
/// One answer for every list and grid. A right-click (a long press on a phone) on a song
/// row or a collection card opens the same menu as its "…" button (tracks) or the shared
/// card menu (albums, playlists, artists); a card's ▶ plays it at once. Resolved from the
/// DataContext under the pointer, so no view wires its own.
/// </summary>
public static class ItemMenus
{
    /// <summary>Listens on <paramref name="root"/> (the window, or the phone's frame).</summary>
    public static void Attach(Control root, Func<MainWindowViewModel?> host)
    {
        root.AddHandler(Control.ContextRequestedEvent, (_, e) => ShowMenu(root, host(), e), RoutingStrategies.Bubble);
        root.AddHandler(Button.ClickEvent, (_, e) => PlayCard(host(), e), RoutingStrategies.Bubble);
    }

    private static void ShowMenu(Control root, MainWindowViewModel? host, ContextRequestedEventArgs e)
    {
        if (host == null || e.Handled || e.Source is TextBox) return;

        for (var visual = e.Source as Visual; visual != null && visual != root; visual = visual.GetVisualParent())
        {
            if (visual is not Control control || control.DataContext is null) continue;
            if (ReferenceEquals(control.DataContext, host) || control.DataContext is ViewModelBase) return;

            switch (control.DataContext)
            {
                case SongItem song:
                    Func<SongItem, Task>? remove = null;
                    if (song.IsLocalPlaylist && host.CurrentView is CollectionViewModel { IsLocalPlaylist: true } local)
                        remove = s => local.RemoveFromPlaylistCommand.ExecuteAsync(s);
                    TrackMenu.Show(control, song, host, remove);
                    break;
                case ShelfCard { Item.Song: { } cardSong }:
                    TrackMenu.Show(control, cardSong, host);
                    break;
                case ShelfCard card:
                    CardMenu.Show(control, host, card.Item.Id, card.Title, card.ThumbnailUrl, card.Subtitle);
                    break;
                case AlbumItem album:
                    CardMenu.Show(control, host, album.Id, album.Title, album.ThumbnailUrl, album.ArtistDisplay);
                    break;
                case PlaylistItem playlist:
                    CardMenu.Show(control, host, playlist.Id, playlist.Title, playlist.ThumbnailUrl, null);
                    break;
                case ArtistItem artist when !string.IsNullOrWhiteSpace(artist.Id):
                    CardMenu.Show(control, host, artist.Id, artist.Name, artist.ThumbnailUrl, "Artist");
                    break;
                case HomeArtistItem topArtist when !string.IsNullOrWhiteSpace(topArtist.Id):
                    CardMenu.Show(control, host, topArtist.Id, topArtist.Name, topArtist.ThumbnailUrl, "Artist");
                    break;
                case DiscographyCard disc:
                    CardMenu.Show(control, host, disc.Id, disc.Title, disc.CoverUrl, disc.Subtitle);
                    break;
                case LibraryCard { Playlist: { } entity }:
                    CardMenu.Show(control, host, entity.Id, entity.Title, entity.ThumbnailUrl, null);
                    break;
                case PlaylistEntity entity:
                    CardMenu.Show(control, host, entity.Id, entity.Title, entity.ThumbnailUrl, null);
                    break;
                default:
                    continue;
            }
            e.Handled = true;
            return;
        }
    }

    /// <summary>
    /// The ▶ on any album / playlist / artist card plays it right away (views only add the
    /// button; Home handles its own song cards first).
    /// </summary>
    private static void PlayCard(MainWindowViewModel? host, RoutedEventArgs e)
    {
        if (e.Handled || host == null) return;
        if (e.Source is not Button { Classes: var classes } button || !classes.Contains("cardPlay")) return;

        (string Id, string Title, string? Thumb)? target = button.DataContext switch
        {
            AlbumItem album => (album.Id, album.Title, album.ThumbnailUrl),
            PlaylistItem playlist => (playlist.Id, playlist.Title, playlist.ThumbnailUrl),
            DiscographyCard disc => (disc.Id, disc.Title, disc.CoverUrl),
            ArtistItem artist => (artist.Id, artist.Name, artist.ThumbnailUrl),
            LibraryCard { Playlist: { } entity } => (entity.Id, entity.Title, entity.ThumbnailUrl),
            _ => null
        };
        if (target is not { } t || string.IsNullOrWhiteSpace(t.Id)) return;
        e.Handled = true;
        _ = host.PlayCollectionAsync(t.Id, t.Title, t.Thumb);
    }
}
