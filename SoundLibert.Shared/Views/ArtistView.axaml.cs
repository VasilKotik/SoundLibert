using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SoundLibert.Domain.Models;
using SoundLibert.UI.Controls;
using SoundLibert.UI.ViewModels;

namespace SoundLibert.UI.Views;

public partial class ArtistView : UserControl
{
    public ArtistView()
    {
        InitializeComponent();
    }

    private bool? _narrow;

    /// <summary>On a phone the newest release goes under the top songs, which keep the width.</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < 640;
        if (narrow == _narrow) return;
        _narrow = narrow;

        TopGrid.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,Auto");
        TopGrid.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto" : "Auto");
        TopGrid.RowSpacing = narrow ? 26 : 0;
        Grid.SetColumn(LatestPanel, narrow ? 0 : 1);
        Grid.SetRow(LatestPanel, narrow ? 1 : 0);
        LatestPanel.Width = narrow ? 220 : 236;
        LatestPanel.HorizontalAlignment = narrow ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Stretch;
    }

    private MainWindowViewModel? Host =>
        SoundLibert.UI.Services.AppServices.HostOf(this);

    private void Subscribe_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ArtistViewModel vm)
            vm.ToggleSubscribeCommand.Execute(null);
    }

    private void Song_Tapped(object? sender, TappedEventArgs e)
    {
        if (InputGuards.FromButtonInteracted(e)) return;
        if (sender is Control { DataContext: SongItem song } && DataContext is ArtistViewModel vm)
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
        if (sender is Button { Tag: SongItem song } button && Host != null)
            SoundLibert.UI.Services.TrackMenu.Show(button, song, Host);
    }

    private void ShowAllTopSongs_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ArtistViewModel { TopSongsListId: { Length: > 0 } list } vm && Host is { } host)
            _ = host.OpenBrowseAsync(list, $"{vm.Title}: top songs", vm.ThumbnailUrl, null);
    }

    private void Latest_Tapped(object? sender, TappedEventArgs e)
    {
        if (InputGuards.FromButtonInteracted(e)) return;
        if (DataContext is ArtistViewModel { LatestRelease: { } card }) Host?.OpenShelfCard(card);
    }

    private void LatestPlay_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is ArtistViewModel { LatestRelease: { } card }) Host?.PlayShelfCard(card);
    }
}
