using Avalonia.Controls;
using SoundLibert.UI.ViewModels;

namespace SoundLibert.UI.Views;

public partial class ShelfPageView : UserControl
{
    private const double LoadAheadPixels = 1200;

    public ShelfPageView()
    {
        InitializeComponent();
    }

    private void PageScroll_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is ShelfPageViewModel vm &&
            PageScroll.Offset.Y + PageScroll.Viewport.Height >= PageScroll.Extent.Height - LoadAheadPixels)
            vm.ShowMore();
    }
}
