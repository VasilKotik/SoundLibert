using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoundLibert.UI.ViewModels;

namespace SoundLibert.UI.Controls;

/// <summary>
/// The place of one shelf on a page. The shelf exists only near the screen: further away the
/// slot remembers its height, lets the shelf go and builds it again on the way back. A scrolled
/// Home of 74 shelves used to hold 38 000 visuals and 1.3 GB; the page can now grow for as long
/// as it likes for the price of what is around the screen.
/// </summary>
public sealed class ShelfSlot : Decorator
{
    /// <summary>Built this far ahead, so scrolling rarely arrives first.</summary>
    private const double BuildAheadScreens = 1;

    /// <summary>Let go only this far away, so a little scrolling back and forth costs nothing.</summary>
    private const double KeepScreens = 2.5;

    /// <summary>The top of a page is there from its first frame.</summary>
    private const int BuiltAtOnce = 4;

    private static readonly HashSet<ShelfSlot> Slots = new();
    private static readonly List<ShelfSlot> SlotsNow = new();
    private static readonly Dictionary<(Type Kind, bool HasEyebrow), double> UsualHeights = new();

    private bool _isAttached;
    private bool _isBuilt;
    private double? _ownHeight;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        Prepare();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttached = false;
        Slots.Remove(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Child = null;
        _isBuilt = false;
        _ownHeight = null;
        Prepare();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != BoundsProperty) return;

        NearScreen.SomethingMoved();
        if (_isBuilt && DataContext is ShelfVm shelf && Bounds.Height > 0)
            UsualHeights[(shelf.GetType(), shelf.HasEyebrow)] = Bounds.Height;
    }

    private void Prepare()
    {
        if (!_isAttached || _isBuilt || DataContext is not ShelfVm shelf) return;

        // A grid grows with its page: it is the page.
        if (shelf is GridShelfVm)
        {
            Slots.Remove(this);
            Build();
            return;
        }

        Slots.Add(this);
        if (IsEffectivelyVisible && IsAmongTheFirst(shelf)) Build();
        else Height = _ownHeight ?? UsualHeight(shelf);
        NearScreen.Watch(Look);
    }

    private bool IsAmongTheFirst(ShelfVm shelf) =>
        this.FindAncestorOfType<ItemsControl>() is { } stack && stack.Items.IndexOf(shelf) is >= 0 and < BuiltAtOnce;

    private static double UsualHeight(ShelfVm shelf)
    {
        // Rows are known exactly: the rail holds up to four, 62 tall.
        if (shelf is RowShelfVm rows)
            return (rows.HasEyebrow ? 58 : 37) + 12 + 62 * Math.Min(4, rows.Items.Count);

        if (UsualHeights.TryGetValue((shelf.GetType(), shelf.HasEyebrow), out var known)) return known;
        return shelf switch
        {
            VideoShelfVm => 252,
            TileRailVm => 146,
            TileGridVm => 420,
            _ => 258
        };
    }

    private static bool Look()
    {
        SlotsNow.AddRange(Slots);
        ShelfSlot? next = null;
        var nextGap = double.MaxValue;
        foreach (var slot in SlotsNow)
        {
            if (NearScreen.GapInScreens(slot) is not { } gap) continue;

            if (slot._isBuilt)
            {
                if (gap > KeepScreens) slot.LetGo();
            }
            else if (gap <= 0)
            {
                // On screen: this one cannot wait.
                slot.Build();
            }
            else if (gap <= BuildAheadScreens && gap < nextGap)
            {
                next = slot;
                nextGap = gap;
            }
        }
        SlotsNow.Clear();

        // One a look, the nearest first: building takes a moment, and scrolling goes on.
        if (next != null)
        {
            next.Build();
            NearScreen.SomethingMoved();
        }
        return Slots.Count > 0;
    }

    private void Build()
    {
        if (_isBuilt || DataContext is not ShelfVm shelf) return;

        _isBuilt = true;
        ClearValue(HeightProperty);

        // Finds its template in the stack above. Not a ContentControl: the theme's clips
        // the rails, which are meant to run past the page's margin.
        Child = new ContentPresenter { Content = shelf };

        // A rail comes back where it was left. Its width is known only after layout.
        if (shelf.RailOffset > 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_isBuilt && this.GetVisualDescendants().OfType<Rail>().FirstOrDefault() is { } rail)
                    rail.ScrollOffset = shelf.RailOffset;
            }, DispatcherPriority.Background);
        }
    }

    private void LetGo()
    {
        if (!_isBuilt || DataContext is not ShelfVm shelf) return;

        if (this.GetVisualDescendants().OfType<Rail>().FirstOrDefault() is { } rail)
            shelf.RailOffset = rail.ScrollOffset;

        _ownHeight = Bounds.Height;
        Height = _ownHeight.Value;
        Child = null;
        _isBuilt = false;
    }
}
