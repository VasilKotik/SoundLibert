using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace SoundLibert.UI.Controls;

/// <summary>
/// Draws one glyph of <see cref="IconSet"/>. Takes its colour from the
/// surrounding <c>Foreground</c> (so it follows a button's hover fade), keeps
/// its stroke optically even across sizes and fades between the outline and the
/// solid form when <see cref="IsFilled"/> changes.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<IconKind> KindProperty =
        AvaloniaProperty.Register<Icon, IconKind>(nameof(Kind));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Size), 20);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    public static readonly StyledProperty<bool> IsFilledProperty =
        AvaloniaProperty.Register<Icon, bool>(nameof(IsFilled));

    /// <summary>Stroke width on screen in pixels; 0 picks one that suits the size.</summary>
    public static readonly StyledProperty<double> WeightProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Weight));

    private static readonly StyledProperty<double> FillAmountProperty =
        AvaloniaProperty.Register<Icon, double>("FillAmount");

    public IconKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public bool IsFilled
    {
        get => GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    public double Weight
    {
        get => GetValue(WeightProperty);
        set => SetValue(WeightProperty, value);
    }

    static Icon()
    {
        AffectsRender<Icon>(KindProperty, ForegroundProperty, FillAmountProperty, WeightProperty);
        AffectsMeasure<Icon>(SizeProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<Icon>(false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsFilledProperty) return;

        // Created on the first real toggle: most icons never change state.
        if (Transitions == null && _attached)
        {
            Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = FillAmountProperty,
                    Duration = TimeSpan.FromMilliseconds(180),
                    Easing = new CubicEaseOut()
                }
            };
        }
        SetValue(FillAmountProperty, IsFilled ? 1.0 : 0.0);
    }

    private bool _attached;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        var shape = IconSet.Get(Kind);
        var brush = Foreground ?? Brushes.White;
        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0) return;

        var scale = side / 24;
        var transform = Matrix.CreateScale(scale, scale) *
                        Matrix.CreateTranslation((Bounds.Width - side) / 2, (Bounds.Height - side) / 2);

        var weightPx = Weight > 0 ? Weight : Math.Max(1.4, side * 0.078);
        var pen = new Pen(brush, weightPx / scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var fill = GetValue(FillAmountProperty);

        using (context.PushTransform(transform))
        {
            if (shape.Outline == null)
            {
                // Solid-only glyph: the thin stroke rounds its corners.
                if (shape.Solid != null)
                    context.DrawGeometry(brush, new Pen(brush, 0.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), shape.Solid);
                return;
            }

            if (shape.Solid != null && fill > 0.001)
            {
                using (context.PushOpacity(fill))
                    context.DrawGeometry(brush, pen, shape.Solid);
                if (fill >= 0.999) return;
                using (context.PushOpacity(1 - fill))
                    context.DrawGeometry(null, pen, shape.Outline);
                return;
            }

            context.DrawGeometry(null, pen, shape.Outline);
        }
    }
}
