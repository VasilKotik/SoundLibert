using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SoundLibert.UI.Controls;
using SoundLibert.UI.ViewModels;

namespace SoundLibert.UI.Views;

public partial class ArtistDiscographyView : UserControl
{
    public ArtistDiscographyView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? Host =>
        SoundLibert.UI.Services.AppServices.HostOf(this);

    private void Scroller_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer scroller && DataContext is ArtistDiscographyViewModel vm &&
            scroller.Offset.Y + scroller.Viewport.Height > scroller.Extent.Height - 900)
        {
            vm.ShowMore();
        }
    }

    private void Chip_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DiscographyChip chip } && DataContext is ArtistDiscographyViewModel vm)
            vm.SelectChipCommand.Execute(chip);
    }

    private void Card_Tapped(object? sender, TappedEventArgs e)
    {
        if (InputGuards.FromButtonInteracted(e)) return;
        if (sender is Control { DataContext: DiscographyCard card } && DataContext is ArtistDiscographyViewModel vm)
            vm.OpenRequest?.Invoke(card);
    }
}
