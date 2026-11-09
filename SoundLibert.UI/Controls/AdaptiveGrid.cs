using Avalonia;
using Avalonia.Controls;

namespace SoundLibert.UI.Controls;

/// <summary>
/// A grid whose columns always fill the width exactly: as many columns as fit
/// at <see cref="MinItemWidth"/>, then every item stretches to share what is left.
/// Both page margins stay aligned at any window size, which a wrap panel cannot do.
/// </summary>
public sealed class AdaptiveGrid : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<AdaptiveGrid, double>(nameof(MinItemWidth), 160);

    public static readonly StyledProperty<double> ColumnGapProperty =
        AvaloniaProperty.Register<AdaptiveGrid, double>(nameof(ColumnGap), 18);

    public static readonly StyledProperty<double> RowGapProperty =
        AvaloniaProperty.Register<AdaptiveGrid, double>(nameof(RowGap), 26);

    public double MinItemWidth { get => GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double ColumnGap { get => GetValue(ColumnGapProperty); set => SetValue(ColumnGapProperty, value); }
    public double RowGap { get => GetValue(RowGapProperty); set => SetValue(RowGapProperty, value); }

    static AdaptiveGrid()
    {
        AffectsMeasure<AdaptiveGrid>(MinItemWidthProperty, ColumnGapProperty, RowGapProperty);
    }

    private (int Columns, double ItemWidth) Layout(double width)
    {
        if (double.IsInfinity(width) || width <= 0) width = MinItemWidth * 5 + ColumnGap * 4;
        var columns = Math.Max(1, (int)Math.Floor((width + ColumnGap) / (MinItemWidth + ColumnGap)));
        var itemWidth = Math.Floor((width - ColumnGap * (columns - 1)) / columns);
        return (columns, itemWidth);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, itemWidth) = Layout(availableSize.Width);
        double height = 0, rowHeight = 0;
        var column = 0;
        var first = true;

        foreach (var child in Children)
        {
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            if (!child.IsVisible) continue;

            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (++column == columns)
            {
                height += rowHeight + (first ? 0 : RowGap);
                first = false;
                rowHeight = 0;
                column = 0;
            }
        }
        if (column > 0) height += rowHeight + (first ? 0 : RowGap);

        var measuredWidth = double.IsInfinity(availableSize.Width)
            ? itemWidth * columns + ColumnGap * (columns - 1)
            : availableSize.Width;
        return new Size(measuredWidth, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, itemWidth) = Layout(finalSize.Width);
        double y = 0, rowHeight = 0;
        var column = 0;
        var rowStart = 0;
        var visible = Children.Where(c => c.IsVisible).ToList();

        for (var i = 0; i < visible.Count; i++)
        {
            rowHeight = Math.Max(rowHeight, visible[i].DesiredSize.Height);
            if (++column < columns && i < visible.Count - 1) continue;

            for (var j = rowStart; j <= i; j++)
            {
                var x = (j - rowStart) * (itemWidth + ColumnGap);
                visible[j].Arrange(new Rect(x, y, itemWidth, rowHeight));
            }
            y += rowHeight + RowGap;
            rowHeight = 0;
            column = 0;
            rowStart = i + 1;
        }
        return finalSize;
    }
}

/// <summary>Gives its child a fixed shape: the height follows the width.</summary>
public sealed class AspectBox : Decorator
{
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<AspectBox, double>(nameof(Ratio), 1);

    /// <summary>Width divided by height.</summary>
    public double Ratio { get => GetValue(RatioProperty); set => SetValue(RatioProperty, value); }

    static AspectBox()
    {
        AffectsMeasure<AspectBox>(RatioProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width;
        var size = new Size(width, width / Math.Max(0.01, Ratio));
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = new Size(finalSize.Width, finalSize.Width / Math.Max(0.01, Ratio));
        Child?.Arrange(new Rect(size));
        return size;
    }
}
