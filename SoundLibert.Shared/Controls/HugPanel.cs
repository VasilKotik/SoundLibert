using Avalonia;
using Avalonia.Controls;

namespace SoundLibert.UI.Controls;

/// <summary>
/// Two children in a row, the second right after the first (a title and its heart). When
/// both do not fit, the first gives way and trims, so the second always stays beside it
/// and inside the row. A Grid cannot do this: its Auto columns never trim.
/// </summary>
public sealed class HugPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<HugPanel, double>(nameof(Spacing));

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    static HugPanel()
    {
        AffectsMeasure<HugPanel>(SpacingProperty);
    }

    private (Control? Lead, Control? Trail) Parts() => (
        Children.Count > 0 ? Children[0] : null,
        Children.Count > 1 ? Children[1] : null);

    /// <summary>The space between them, only while the second is there.</summary>
    private double Gap(Control? trail) => trail is { IsVisible: true } ? Spacing : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var (lead, trail) = Parts();
        trail?.Measure(availableSize);
        var trailSize = trail?.DesiredSize ?? default;
        var room = Math.Max(0, availableSize.Width - trailSize.Width - Gap(trail));
        lead?.Measure(new Size(room, availableSize.Height));
        var leadSize = lead?.DesiredSize ?? default;
        return new Size(leadSize.Width + Gap(trail) + trailSize.Width, Math.Max(leadSize.Height, trailSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (lead, trail) = Parts();
        var trailSize = trail?.DesiredSize ?? default;
        var room = Math.Max(0, finalSize.Width - trailSize.Width - Gap(trail));
        var leadWidth = Math.Min(lead?.DesiredSize.Width ?? 0, room);
        lead?.Arrange(new Rect(0, 0, leadWidth, finalSize.Height));
        trail?.Arrange(new Rect(leadWidth + Gap(trail), (finalSize.Height - trailSize.Height) / 2,
            trailSize.Width, trailSize.Height));
        return finalSize;
    }
}
