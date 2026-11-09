using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Metadata;
using Avalonia.Threading;

namespace SoundLibert.UI.Controls;

/// <summary>
/// A horizontal shelf: no scrollbar, arrows that fade in when the pointer is over it
/// and only on sides that have more, and Shift + wheel. Content runs under the edges
/// of the page instead of being cut at its margin. Laid out by hand, so the arrows
/// float over the shelf and never make it taller (a row of chips stays a row of chips).
/// </summary>
public sealed class Rail : Control
{
    public static readonly StyledProperty<Control?> ContentProperty =
        AvaloniaProperty.Register<Rail, Control?>(nameof(Content));

    public static readonly StyledProperty<double> BleedProperty =
        AvaloniaProperty.Register<Rail, double>(nameof(Bleed), 32);

    [Content]
    public Control? Content { get => GetValue(ContentProperty); set => SetValue(ContentProperty, value); }

    /// <summary>How far the shelf reaches past its margins: the page's own padding.</summary>
    public double Bleed { get => GetValue(BleedProperty); set => SetValue(BleedProperty, value); }

    /// <summary>It has come close to the end of what it holds.</summary>
    public event EventHandler? NearEnd;

    /// <summary>How far the shelf has been scrolled sideways.</summary>
    public double ScrollOffset
    {
        get => _scroll.Offset.X;
        set => _scroll.Offset = new Vector(value, 0);
    }

    private readonly ScrollViewer _scroll;
    private readonly Decorator _inset = new();
    private readonly Button _previous;
    private readonly Button _next;

    public Rail()
    {
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _inset
        };
        _previous = Arrow(IconKind.ChevronLeft);
        _next = Arrow(IconKind.ChevronRight);
        _previous.Click += (_, _) => Page(-1);
        _next.Click += (_, _) => Page(1);

        foreach (var child in new Control[] { _scroll, _previous, _next })
        {
            VisualChildren.Add(child);
            LogicalChildren.Add(child);
        }

        _scroll.ScrollChanged += (_, _) => UpdateArrows();
        _scroll.SizeChanged += (_, _) => UpdateArrows();
        _inset.Margin = new Thickness(Bleed, 0);
    }

    private static Button Arrow(IconKind kind)
    {
        var button = new Button
        {
            Content = new Icon { Kind = kind, Size = 20 },
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        button.Classes.Add("railArrow");
        return button;
    }

    private void Page(int direction)
    {
        var distance = Math.Max(240, _scroll.Viewport.Width * 0.8);
        var left = _scroll.Extent.Width - _scroll.Viewport.Width - _scroll.Offset.X;

        // More comes first, so the step is a whole one and does not stop at what is drawn so far.
        if (direction > 0 && left < distance && NearEnd != null)
        {
            NearEnd.Invoke(this, EventArgs.Empty);
            Dispatcher.UIThread.Post(
                () => SmoothScroller.ScrollBy(_scroll, new Vector(distance, 0)), DispatcherPriority.Background);
            return;
        }

        SmoothScroller.ScrollBy(_scroll, new Vector(distance * direction, 0));
    }

    private void UpdateArrows()
    {
        var max = _scroll.Extent.Width - _scroll.Viewport.Width;
        _previous.Classes.Set("available", _scroll.Offset.X > 1);
        _next.Classes.Set("available", _scroll.Offset.X < max - 1);

        if (_scroll.Extent.Width > 0 && max - _scroll.Offset.X < _scroll.Viewport.Width / 4)
            NearEnd?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty)
        {
            _inset.Child = change.NewValue as Control;
        }
        else if (change.Property == BleedProperty)
        {
            _inset.Margin = new Thickness(Bleed, 0);
            InvalidateMeasure();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(UpdateArrows, DispatcherPriority.Background);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = availableSize.Width + 2 * Bleed;
        _scroll.Measure(new Size(width, availableSize.Height));
        _previous.Measure(Size.Infinity);
        _next.Measure(Size.Infinity);
        return new Size(availableSize.Width, _scroll.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var bleed = Bleed;
        _scroll.Arrange(new Rect(-bleed, 0, finalSize.Width + 2 * bleed, finalSize.Height));

        // A tall shelf of cards: centred on the covers, above their captions.
        // A low one (chips): centred, and small enough to fit.
        var tall = finalSize.Height > 90;
        var side = tall ? 40 : Math.Min(32, finalSize.Height);
        var y = tall ? (finalSize.Height - 44 - side) / 2 : (finalSize.Height - side) / 2;
        var inset = Math.Max(4, bleed - 22);

        foreach (var arrow in new[] { _previous, _next })
        {
            arrow.Width = side;
            arrow.Height = side;
        }
        _previous.Arrange(new Rect(-bleed + inset, y, side, side));
        _next.Arrange(new Rect(finalSize.Width + bleed - inset - side, y, side, side));
        return finalSize;
    }
}
