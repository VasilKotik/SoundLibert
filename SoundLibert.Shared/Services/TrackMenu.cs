using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using SoundLibert.Domain.Models;
using SoundLibert.UI.Controls;
using SoundLibert.UI.ViewModels;
using SoundLibert.UI.Views;

namespace SoundLibert.UI.Services;

/// <summary>
/// The shared right-click / "more" menu for a track, built in code so every list
/// uses the same actions without per-view plumbing. The rows and the player bar
/// keep few buttons because of this menu: everything secondary lives here.
/// </summary>
public static class TrackMenu
{
    /// <summary>Extra actions of a row in a playlist the user owns.</summary>
    public sealed record PlaylistActions(Func<SongItem, Task> Remove, Action<SongItem>? MoveUp = null, Action<SongItem>? MoveDown = null);

    public static MenuFlyout Show(Control anchor, SongItem song, MainWindowViewModel host,
        Func<SongItem, Task>? removeFromPlaylist = null)
        => Show(anchor, song, host, removeFromPlaylist == null ? null : new PlaylistActions(removeFromPlaylist));

    public static MenuFlyout Show(Control anchor, SongItem song, MainWindowViewModel host, PlaylistActions? playlist)
    {
        var flyout = new MenuFlyout();

        flyout.Items.Add(Item("Play next", IconKind.PlayNext, () => host.PlayNext(song)));
        flyout.Items.Add(Item("Add to queue", IconKind.AddToQueue, () => host.Enqueue(song)));
        flyout.Items.Add(AddToPlaylist(anchor, song, host));
        // The library is asked, not the row: a Home card or a radio row never learnt its like.
        flyout.Items.Add(Item(host.IsLiked(song) ? "Remove from liked" : "Add to liked", IconKind.Heart,
            () => _ = host.ToggleLikeAsync(song)));
        AddDownload(flyout, song, host);

        // Offered only inside an editable playlist (passed by the caller).
        // No separators anywhere in this menu (user preference).
        if (playlist != null)
        {
            if (playlist.MoveUp is { } moveUp)
                flyout.Items.Add(Item("Move up", IconKind.ArrowUp, () => moveUp(song)));
            if (playlist.MoveDown is { } moveDown)
                flyout.Items.Add(Item("Move down", IconKind.ArrowDown, () => moveDown(song)));
            flyout.Items.Add(Item("Remove from this playlist", IconKind.Trash, () => _ = playlist.Remove(song)));
        }

        AddDestinations(flyout, anchor, song, host);

        flyout.ShowAt(anchor);
        return flyout;
    }

    /// <summary>
    /// The player's "more": the current track's actions, plus the things that belong
    /// to the player rather than to a song.
    /// </summary>
    public static MenuFlyout ShowNowPlaying(Control anchor, MainWindowViewModel host,
        Action? openMiniPlayer, Action customSleep, Action? reportProblem = null)
    {
        var flyout = new MenuFlyout();

        if (host.CurrentTrack is { } song)
        {
            flyout.Items.Add(AddToPlaylist(anchor, song, host));
            AddDownload(flyout, song, host);
            AddDestinations(flyout, anchor, song, host);
        }

        // A phone has no second window to shrink the player into.
        if (openMiniPlayer != null)
            flyout.Items.Add(Item("Mini player", IconKind.MiniPlayer, openMiniPlayer));

        var sleep = new MenuItem { Header = host.SleepTimerText, Icon = Glyph(IconKind.Moon) };
        foreach (var minutes in new[] { 5, 15, 30, 60 })
        {
            var value = minutes.ToString();
            sleep.Items.Add(Item($"{minutes} minutes", IconKind.None, () => host.StartSleepTimerCommand.Execute(value)));
        }
        sleep.Items.Add(Item("Custom…", IconKind.None, customSleep));
        if (host.IsSleepTimerActive)
            sleep.Items.Add(Item("Cancel timer", IconKind.None, () => host.StartSleepTimerCommand.Execute("0")));
        flyout.Items.Add(sleep);

        flyout.Items.Add(Item(host.IsCanvasEnabled ? "Turn off animated cover" : "Turn on animated cover",
            IconKind.Sparkles, () => host.ToggleCanvasCommand.Execute(null)));

        if (host.IsVideoMode)
        {
            var quality = new MenuItem { Header = $"Quality · {host.VideoQualityText}", Icon = Glyph(IconKind.Sliders) };
            foreach (var (label, value) in new[] { ("Auto (best)", "0"), ("1080p", "1080"), ("720p", "720"), ("480p", "480"), ("360p", "360") })
                quality.Items.Add(Item(label, IconKind.None, () => host.SetVideoQualityCommand.Execute(value)));
            flyout.Items.Add(quality);
        }

        // Where the window's own report button steps aside (the fullscreen player).
        if (reportProblem != null)
            flyout.Items.Add(Item("Report a bug", IconKind.Bug, reportProblem));

        flyout.ShowAt(anchor);
        return flyout;
    }

    private static MenuItem AddToPlaylist(Control anchor, SongItem song, MainWindowViewModel host)
    {
        var addMenu = new MenuItem { Header = "Add to playlist", Icon = Glyph(IconKind.AddToPlaylist) };
        addMenu.Items.Add(Item("New playlist…", IconKind.Plus, () => _ = PromptCreatePlaylistAsync(anchor, song, host)));
        foreach (var playlist in host.Playlists)
        {
            if (playlist.IsEditable)
                addMenu.Items.Add(Item(playlist.Title, IconKind.None, () => _ = host.AddToPlaylistAsync(playlist, song)));
        }
        return addMenu;
    }

    /// <summary>Offline copy (catalogue songs only; local files are already offline).</summary>
    private static void AddDownload(MenuFlyout flyout, SongItem song, MainWindowViewModel host)
    {
        if (song.IsLocal) return;

        if (host.Downloads.HasCopy(song))
            flyout.Items.Add(Item("Remove download", IconKind.Downloaded, () => _ = host.RemoveDownloadAsync(song)));
        else if (host.Downloads.IsBusy(song.Id))
            flyout.Items.Add(new MenuItem { Header = "Downloading…", IsEnabled = false, Icon = Glyph(IconKind.Download) });
        else
            flyout.Items.Add(Item("Download", IconKind.Download, () => _ = host.DownloadTrackAsync(song)));
    }

    private static void AddDestinations(MenuFlyout flyout, Control anchor, SongItem song, MainWindowViewModel host)
    {
        if (song.IsLocal) return;

        var artist = song.Artists.Count > 0 ? song.Artists[0].Name : null;
        var artistId = song.Artists.Count > 0 ? song.Artists[0].Id : null;

        flyout.Items.Add(Item("Start radio", IconKind.Radio, () => _ = host.PlayRadioAsync(song)));
        if (!string.IsNullOrWhiteSpace(artist))
        {
            flyout.Items.Add(Item($"Go to {artist}", IconKind.User, () => LeaveFullscreen(host, () => host.OpenArtist(artistId, artist))));
            flyout.Items.Add(AiItem(artist, host));
        }
        if (!string.IsNullOrWhiteSpace(song.AlbumId))
            flyout.Items.Add(Item("Go to album", IconKind.Album,
                () => LeaveFullscreen(host, () => _ = host.OpenBrowseAsync(song.AlbumId!, song.AlbumTitle ?? "Album", null, artist))));

        var copy = new MenuItem { Header = "Copy", Icon = Glyph(IconKind.Copy) };
        copy.Items.Add(Item("YouTube link", IconKind.Link,
            () => _ = CopyAsync(anchor, host, $"https://music.youtube.com/watch?v={song.Id}", "Link copied")));
        copy.Items.Add(Item("Song info", IconKind.Music,
            () => _ = CopyAsync(anchor, host, $"{song.Title} — {song.ArtistDisplay}", "Song info copied")));
        flyout.Items.Add(copy);
    }

    /// <summary>
    /// The listener's word about an artist: "AI artist: don't recommend", or for one taken for
    /// AI (by the community list or an earlier word), "Not AI: recommend again".
    /// </summary>
    internal static MenuItem AiItem(string artist, MainWindowViewModel host)
    {
        var isAi = host.AiFilter.IsAiArtist(artist);
        return Item(isAi ? "Not AI: recommend again" : "AI artist: don't recommend", IconKind.Sparkles,
            () => host.MarkAiArtist(artist, !isAi));
    }

    private static void LeaveFullscreen(MainWindowViewModel host, Action then)
    {
        if (host.IsFullscreenPlayer) host.CloseFullscreenPlayerCommand.Execute(null);
        then();
    }

    private static async Task CopyAsync(Control anchor, MainWindowViewModel host, string text, string toast)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(anchor)?.Clipboard;
            if (clipboard == null) return;
            await clipboard.SetTextAsync(text);
            host.ShowToast(toast);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[TrackMenu] copy failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Shows just the "add to playlist" chooser (New playlist… + every editable
    /// playlist). Used after liking a track so the user can file it right away.
    /// </summary>
    public static void ShowAddToPlaylist(Control anchor, SongItem song, MainWindowViewModel host)
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(Item("New playlist…", IconKind.Plus, () => _ = PromptCreatePlaylistAsync(anchor, song, host)));

        foreach (var playlist in host.Playlists)
        {
            if (playlist.IsEditable)
                flyout.Items.Add(Item(playlist.Title, IconKind.None, () => _ = host.AddToPlaylistAsync(playlist, song)));
        }

        if (flyout.Items.Count == 1)
            flyout.Items.Add(new MenuItem { Header = "No playlists yet", IsEnabled = false });

        flyout.ShowAt(anchor);
    }

    internal static MenuItem Item(string header, IconKind icon, Action action)
    {
        var item = new MenuItem { Header = header };
        if (icon != IconKind.None) item.Icon = Glyph(icon);
        item.Click += (_, _) => action();
        return item;
    }

    internal static Icon Glyph(IconKind kind) => new() { Kind = kind, Size = 16 };

    private static async Task PromptCreatePlaylistAsync(Control anchor, SongItem song, MainWindowViewModel host)
    {
        var name = await AppServices.Shell.AskAsync(anchor, "New playlist", placeholder: "Playlist name");
        if (!string.IsNullOrWhiteSpace(name)) await host.CreatePlaylistWithSongAsync(name, song);
    }
}
