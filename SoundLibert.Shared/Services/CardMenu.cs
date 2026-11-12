using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using SoundLibert.Domain.Models;
using SoundLibert.UI.Controls;
using SoundLibert.UI.ViewModels;

namespace SoundLibert.UI.Services;

/// <summary>
/// The right-click / "…" menu for a collection card (album, playlist, local
/// playlist, artist): Play, Shuffle, Play next, Add to queue, Open, Copy link.
/// The card counterpart of <see cref="TrackMenu"/> — every grid and rail uses
/// it, so a card behaves the same on Home, Search, Library and artist pages.
/// </summary>
public static class CardMenu
{
    public static void Show(Control anchor, MainWindowViewModel host,
        string id, string title, string? thumbnail, string? subtitle)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var isArtist = id.StartsWith("UC", StringComparison.Ordinal);
        var flyout = new MenuFlyout();

        flyout.Items.Add(TrackMenu.Item(isArtist ? "Play artist" : "Play", IconKind.Play,
            () => _ = host.PlayCollectionAsync(id, title, thumbnail)));
        flyout.Items.Add(TrackMenu.Item("Shuffle", IconKind.Shuffle,
            () => _ = host.PlayCollectionAsync(id, title, thumbnail, MainWindowViewModel.CollectionAction.Shuffle)));
        flyout.Items.Add(TrackMenu.Item("Play next", IconKind.PlayNext,
            () => _ = host.PlayCollectionAsync(id, title, thumbnail, MainWindowViewModel.CollectionAction.PlayNext)));
        flyout.Items.Add(TrackMenu.Item("Add to queue", IconKind.AddToQueue,
            () => _ = host.PlayCollectionAsync(id, title, thumbnail, MainWindowViewModel.CollectionAction.AddToQueue)));

        if (isArtist && !string.IsNullOrWhiteSpace(title))
            flyout.Items.Add(TrackMenu.AiItem(title, host));

        var link = LinkFor(id);
        if (link != null)
            flyout.Items.Add(TrackMenu.Item("Copy YouTube link", IconKind.Link, () => _ = CopyAsync(anchor, host, link)));

        flyout.ShowAt(anchor);
    }

    /// <summary>Public music.youtube.com link for a card id (null for local playlists).</summary>
    private static string? LinkFor(string id)
    {
        if (id.StartsWith(PlaylistEntity.LocalIdPrefix, StringComparison.Ordinal)) return null;
        if (id.StartsWith("UC", StringComparison.Ordinal)) return $"https://music.youtube.com/channel/{id}";
        if (id.StartsWith("MPRE", StringComparison.Ordinal)) return $"https://music.youtube.com/browse/{id}";
        var list = id.StartsWith("VL", StringComparison.Ordinal) ? id[2..] : id;
        return $"https://music.youtube.com/playlist?list={list}";
    }

    private static async Task CopyAsync(Control anchor, MainWindowViewModel host, string text)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(anchor)?.Clipboard;
            if (clipboard == null) return;
            await clipboard.SetTextAsync(text);
            host.ShowToast("Link copied");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[CardMenu] copy failed: {ex.Message}");
        }
    }
}
