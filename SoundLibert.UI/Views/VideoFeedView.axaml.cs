using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SoundLibert.Domain.Models;
using SoundLibert.UI.Controls;
using SoundLibert.UI.ViewModels;

namespace SoundLibert.UI.Views;

public partial class VideoFeedView : UserControl
{
    /// <summary>Fetches ahead by about two screens of cards, so the end is rarely reached.</summary>
    private const double LoadAheadPixels = 1400;

    public VideoFeedView()
    {
        InitializeComponent();
    }

    /// <summary>Dev/QA helper: scroll to the end of what is on the page.</summary>
    public void ScrollToEnd() => PageScroll.Offset = new Vector(0, double.MaxValue);

    /// <summary>Also runs when new cards change the page's height, so a tall window keeps filling.</summary>
    private void PageScroll_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is VideoFeedViewModel vm &&
            PageScroll.Offset.Y + PageScroll.Viewport.Height >= PageScroll.Extent.Height - LoadAheadPixels)
            _ = vm.LoadMoreAsync();
    }

    private void Video_Tapped(object? sender, TappedEventArgs e)
    {
        if (InputGuards.FromButtonInteracted(e)) return;
        if (sender is Control { DataContext: SongItem video } && DataContext is VideoFeedViewModel vm)
            vm.PlayVideoCommand.Execute(video);
    }
}
