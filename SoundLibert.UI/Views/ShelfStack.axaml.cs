using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SoundLibert.UI.Controls;
using SoundLibert.UI.ViewModels;

namespace SoundLibert.UI.Views;

/// <summary>
/// The shelves of a page, one under the other. Every screen that shows shelves draws them
/// here, so a card, a chart row or a mood tile looks and behaves the same everywhere.
/// </summary>
public partial class ShelfStack : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ShelvesProperty =
        AvaloniaProperty.Register<ShelfStack, IEnumerable?>(nameof(Shelves));

    public static readonly StyledProperty<double> RailBleedProperty =
        AvaloniaProperty.Register<ShelfStack, double>(nameof(RailBleed), 32);

    public IEnumerable? Shelves { get => GetValue(ShelvesProperty); set => SetValue(ShelvesProperty, value); }

    /// <summary>How far a rail reaches past its margins: the padding around the stack.</summary>
    public double RailBleed { get => GetValue(RailBleedProperty); set => SetValue(RailBleedProperty, value); }

    public ShelfStack()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShelvesProperty) Stack.ItemsSource = Shelves;
    }

    private MainWindowViewModel? Host =>
        (TopLevel.GetTopLevel(this) as Window)?.DataContext as MainWindowViewModel;

    private void Rail_NearEnd(object? sender, EventArgs e)
    {
        if (sender is Rail { DataContext: ShelfVm shelf }) shelf.ShowMore();
    }

    private void Card_Tapped(object? sender, TappedEventArgs e)
    {
        if (InputGuards.FromButtonInteracted(e)) return;
        if (sender is Control { Tag: ShelfCard card }) Host?.OpenShelfCard(card);
    }

    private void CardPlay_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control { Tag: ShelfCard card }) Host?.PlayShelfCard(card);
    }

    private void ShowAll_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: ShelfVm shelf }) Host?.ShowAll(shelf);
    }

    private void Tile_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { Tag: MoodTileVm tile }) Host?.OpenMood(tile.Tile);
    }
}
