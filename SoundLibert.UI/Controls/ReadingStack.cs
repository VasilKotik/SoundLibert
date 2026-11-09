using Avalonia;
using Avalonia.Controls;

namespace SoundLibert.UI.Controls;

/// <summary>
/// A vertical stack with a comfortable reading width, aligned to the left edge of the
/// page: a setting's switch stays near its label on a wide window, and on a narrow one
/// the stack simply takes what there is.
/// </summary>
public sealed class ReadingStack : StackPanel
{
    public static readonly StyledProperty<double> ColumnWidthProperty =
        AvaloniaProperty.Register<ReadingStack, double>(nameof(ColumnWidth), 760);

    public double ColumnWidth { get => GetValue(ColumnWidthProperty); set => SetValue(ColumnWidthProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        base.MeasureOverride(availableSize.WithWidth(Math.Min(availableSize.Width, ColumnWidth)));

    protected override Size ArrangeOverride(Size finalSize)
    {
        base.ArrangeOverride(finalSize.WithWidth(Math.Min(finalSize.Width, ColumnWidth)));
        return finalSize;
    }
}
