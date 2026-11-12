using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace SoundLibert.UI.Controls;

/// <summary>
/// Loading placeholder shaped like the content that is about to appear —
/// song rows or a strip of cover cards — so every page loads the same way
/// (a thin progress bar over an empty page before). The pulse comes from the
/// shared <c>Border.skeleton</c> style; layout is built in code so each page
/// only sets <see cref="Kind"/> and <see cref="Count"/>.
/// </summary>
public sealed class SkeletonPlaceholder : ContentControl
{
    public enum SkeletonKind { Rows, Cards, WideCards }

    public static readonly StyledProperty<SkeletonKind> KindProperty =
        AvaloniaProperty.Register<SkeletonPlaceholder, SkeletonKind>(nameof(Kind));

    public static readonly StyledProperty<int> CountProperty =
        AvaloniaProperty.Register<SkeletonPlaceholder, int>(nameof(Count), 6);

    public SkeletonKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public int Count
    {
        get => GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    static SkeletonPlaceholder()
    {
        KindProperty.Changed.AddClassHandler<SkeletonPlaceholder>((s, _) => s.Build());
        CountProperty.Changed.AddClassHandler<SkeletonPlaceholder>((s, _) => s.Build());
    }

    public SkeletonPlaceholder()
    {
        IsHitTestVisible = false;
        Build();
    }

    private void Build()
    {
        var count = Math.Clamp(Count, 1, 24);
        Content = Kind switch
        {
            SkeletonKind.Cards => BuildCards(count),
            SkeletonKind.WideCards => BuildWideCards(count),
            _ => BuildRows(count)
        };
    }

    private static Control BuildRows(int count)
    {
        var panel = new StackPanel { Spacing = 6 };
        for (var i = 0; i < count; i++)
        {
            // Vary the title widths a little so the block reads as a list.
            var titleWidth = 180 + (i * 47 % 140);
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Margin = new Thickness(8, 6)
            };
            row.Children.Add(Bar(44, 44, 7));
            var text = new StackPanel
            {
                Spacing = 7,
                Margin = new Thickness(14, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            text.Children.Add(Bar(titleWidth, 12, 6));
            text.Children.Add(Bar(titleWidth * 0.55, 10, 5));
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var right = Bar(38, 10, 5);
            right.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(right, 2);
            row.Children.Add(right);
            panel.Children.Add(row);
        }
        return panel;
    }

    private static Control BuildCards(int count)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        for (var i = 0; i < count; i++)
        {
            var card = new StackPanel { Width = 164, Spacing = 10 };
            card.Children.Add(Bar(164, 164, 12));
            card.Children.Add(Bar(120 + (i * 31 % 40), 12, 6));
            card.Children.Add(Bar(80, 10, 5));
            panel.Children.Add(card);
        }
        return panel;
    }

    /// <summary>The same grid and shape as a page of video cards.</summary>
    private static Control BuildWideCards(int count)
    {
        var grid = new AdaptiveGrid { MinItemWidth = 240 };
        for (var i = 0; i < count; i++)
        {
            var picture = new Border { CornerRadius = new CornerRadius(12) };
            picture.Classes.Add("skeleton");

            var card = new StackPanel { Spacing = 10 };
            card.Children.Add(new AspectBox { Ratio = 16.0 / 9, Child = picture });
            card.Children.Add(Bar(160 + (i * 31 % 60), 12, 6));
            card.Children.Add(Bar(90, 10, 5));
            grid.Children.Add(card);
        }
        return grid;
    }

    private static Border Bar(double width, double height, double radius)
    {
        var bar = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(radius),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        bar.Classes.Add("skeleton");
        return bar;
    }
}
