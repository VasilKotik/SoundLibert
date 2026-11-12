using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SoundLibert.Domain.Models;
using SoundLibert.UI.Controls;
using SoundLibert.UI.ViewModels;

namespace SoundLibert.UI.Views;

public partial class HomeView : UserControl
{
    /// <summary>Fetches ahead by about two screens, so the end is rarely reached.</summary>
    private const double LoadAheadPixels = 1400;

    /// <summary>A page left open changes too, though never under the pointer.</summary>
    private readonly DispatcherTimer _freshness = new() { Interval = TimeSpan.FromMinutes(1) };

    public HomeView()
    {
        InitializeComponent();
        _freshness.Tick += (_, _) =>
        {
            if (!IsPointerOver && !SoundLibert.UI.Services.AppServices.Hidden && TopLevel.GetTopLevel(this) is { } top
                && top is not Window { IsVisible: false } && top is not Window { WindowState: WindowState.Minimized })
                Vm?.RefreshIfStale();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _freshness.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _freshness.Stop();
    }

    /// <summary>Dev/QA helper: scroll to the end of the page so far.</summary>
    public void ScrollToBottom() => BoardScroll.Offset = new Vector(0, double.MaxValue);

    /// <summary>Dev/QA helper: scroll to an absolute Y offset.</summary>
    public void ScrollToOffset(double y) => BoardScroll.Offset = new Vector(0, y);

    private MainWindowViewModel? Host =>
        SoundLibert.UI.Services.AppServices.HostOf(this);

    private HomeViewModel? Vm => DataContext as HomeViewModel;

    private void BoardScroll_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.ReportScroll(BoardScroll.Offset.Y > 60);
        if (BoardScroll.Offset.Y + BoardScroll.Viewport.Height >= BoardScroll.Extent.Height - LoadAheadPixels)
            _ = vm.LoadMoreAsync();
    }

    private void Chip_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: HomeChipVm chip } && Vm is { } vm)
            vm.SelectChipCommand.Execute(chip);
    }

    private void Recent_Tapped(object? sender, TappedEventArgs e)
    {
        if (InputGuards.FromButtonInteracted(e)) return;
        if (sender is Control { DataContext: SongItem song } && Vm is { } vm)
            vm.PlayRecentCommand.Execute(song);
    }

    private void Artist_Tapped(object? sender, TappedEventArgs e)
    {
        if (InputGuards.FromButtonInteracted(e)) return;
        if (sender is Control { DataContext: HomeArtistItem artist } && Host is { } host)
            host.OpenArtist(artist.Id, artist.Name);
    }
}
