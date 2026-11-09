using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SoundLibert.UI.ViewModels;

namespace SoundLibert.UI.Views;

/// <summary>
/// Compact always-on-top player. Shares the main ViewModel, so controls and
/// artwork stay in sync. Closing it restores the full window (handled by the
/// owner).
/// </summary>
public partial class MiniPlayerWindow : Window
{
    private MainWindowViewModel? _viewModel;

    public MiniPlayerWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => _viewModel = DataContext as MainWindowViewModel;

        // Same tunnel wiring as the main window: the Slider Thumb swallows
        // pointer events, so seek handlers must run on the tunnel phase.
        SeekMini.AddHandler(PointerPressedEvent, (_, _) => Seek(true), RoutingStrategies.Tunnel);
        SeekMini.AddHandler(PointerReleasedEvent, (_, _) => Seek(false), RoutingStrategies.Tunnel);

        // Fade/scale in once the window is shown (the Root starts hidden).
        Opened += (_, _) =>
        {
            Root.Opacity = 1;
            Root.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1)");
        };

        // Frameless windows don't drag by themselves: a left-press on any
        // non-interactive area starts an OS move. Presses that belong to
        // buttons/sliders are left alone so clicks and seeks keep working.
        Root.AddHandler(PointerPressedEvent, (_, e) => DragWindow(e), RoutingStrategies.Bubble);
    }

    private void DragWindow(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Control source &&
            (source.GetVisualAncestors().OfType<Button>().Any() ||
             source.GetVisualAncestors().OfType<Slider>().Any()))
            return;
        BeginMoveDrag(e);
    }

    private void Seek(bool start)
    {
        if (_viewModel == null) return;
        _viewModel.IsSeeking = start;
        if (!start) _viewModel.CommitSeekCommand.Execute(null);
    }

    private void Restore_Click(object? sender, RoutedEventArgs e) => Close();
}
