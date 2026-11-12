using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media.Transformation;

namespace SoundLibert.UI.Controls;

/// <summary>
/// The play / pause glyph of a play button: the two symbols cross over with a small
/// turn and scale, instead of swapping in a single frame.
/// </summary>
public sealed class PlayGlyph : Control
{
    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<PlayGlyph, bool>(nameof(IsPlaying));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<PlayGlyph, double>(nameof(Size), 22);

    public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    private static readonly TransformOperations Shown = TransformOperations.Parse("rotate(0deg) scale(1)");
    private static readonly TransformOperations HiddenBefore = TransformOperations.Parse("rotate(-70deg) scale(0.55)");
    private static readonly TransformOperations HiddenAfter = TransformOperations.Parse("rotate(70deg) scale(0.55)");

    private readonly Icon _play = new() { Kind = IconKind.Play };
    private readonly Icon _pause = new() { Kind = IconKind.Pause, Opacity = 0, RenderTransform = HiddenBefore };

    static PlayGlyph()
    {
        AffectsMeasure<PlayGlyph>(SizeProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<PlayGlyph>(false);
    }

    public PlayGlyph()
    {
        _play.RenderTransform = Shown;
        foreach (var icon in new[] { _play, _pause })
        {
            icon.RenderTransformOrigin = RelativePoint.Center;
            icon.Transitions = new Transitions
            {
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(150) },
                new TransformOperationsTransition
                {
                    Property = RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(300),
                    Easing = Motion.Spring
                }
            };
            VisualChildren.Add(icon);
            LogicalChildren.Add(icon);
        }
        ApplySize();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsPlayingProperty)
        {
            var playing = IsPlaying;
            _play.Opacity = playing ? 0 : 1;
            _play.RenderTransform = playing ? HiddenAfter : Shown;
            _pause.Opacity = playing ? 1 : 0;
            _pause.RenderTransform = playing ? Shown : HiddenBefore;
        }
        else if (change.Property == SizeProperty)
        {
            ApplySize();
        }
    }

    private void ApplySize()
    {
        _play.Size = Size;
        _pause.Size = Size;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _play.Measure(availableSize);
        _pause.Measure(availableSize);
        return new Size(Size, Size);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rect = new Rect(finalSize);
        _play.Arrange(rect);
        _pause.Arrange(rect);
        return finalSize;
    }
}
