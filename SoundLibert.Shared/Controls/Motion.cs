using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace SoundLibert.UI.Controls;

/// <summary>
/// The motion vocabulary of the app, so every screen moves the same way.
/// Everything here is a Transition with a posted final state: keyframes cannot
/// animate RenderTransform in Avalonia, and the pattern is proven not to leave
/// a control stuck invisible.
/// </summary>
public static class Motion
{
    /// <summary>The standard curve: quick start, long soft landing.</summary>
    public static readonly Easing Emphasized = new SplineEasing(0.2, 0, 0, 1);

    /// <summary>A small overshoot for things that "land" (a heart, a play button).</summary>
    public static readonly Easing Spring = new SpringEasing(1, 320, 17, 0);

    private static bool _enabled = true;

    /// <summary>False when Windows has animations off or the user chose Reduce motion.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            EnabledChanged?.Invoke();
        }
    }

    /// <summary>Continuous motion (the colours, the bars) restarts or stops with this.</summary>
    public static event Action? EnabledChanged;

    /// <summary>Something else decided continuous motion must stop or may start.</summary>
    public static void NotifyChanged() => EnabledChanged?.Invoke();

    /// <summary>Plain tiles join the entrance wave with this attached flag.</summary>
    public static readonly AttachedProperty<bool> RevealOnLoadProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("RevealOnLoad", typeof(Motion));

    public static bool GetRevealOnLoad(Control control) => control.GetValue(RevealOnLoadProperty);
    public static void SetRevealOnLoad(Control control, bool value) => control.SetValue(RevealOnLoadProperty, value);

    static Motion()
    {
        RevealOnLoadProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is true)
                control.AttachedToVisualTree += (_, _) => Reveal(control);
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint flags);

    /// <summary>Windows Settings, Accessibility, Animation effects.</summary>
    public static bool SystemAnimationsOn()
    {
        const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
        try
        {
            return !OperatingSystem.IsWindows() ||
                   !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out var on, 0) || on;
        }
        catch
        {
            return true;
        }
    }

    private static readonly TransformOperations Rest = TransformOperations.Parse("translateY(0px)");
    private static readonly TransformOperations Lowered = TransformOperations.Parse("translateY(14px)");

    private static readonly Transitions RevealTransitions = new()
    {
        new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(320), Easing = new CubicEaseOut() },
        new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(460), Easing = Emphasized }
    };

    /// <summary>
    /// Entrance of a card or row when it joins the page: rises and fades in, with
    /// a delay taken from its place on screen, so a whole page arrives as one
    /// diagonal wave. Items outside the window skip the animation.
    /// </summary>
    public static void Reveal(Control control)
    {
        if (!Enabled) return;

        // Local values for the entrance; its own hover transitions (from a style)
        // come back afterwards.
        control.Transitions = null;
        control.Opacity = 0;
        control.RenderTransform = Lowered;

        Dispatcher.UIThread.Post(() =>
        {
            var top = TopLevel.GetTopLevel(control);
            var at = top == null ? null : control.TranslatePoint(default, top);

            void Land()
            {
                control.Transitions = RevealTransitions;
                control.Opacity = 1;
                control.RenderTransform = Rest;
                DispatcherTimer.RunOnce(() => control.ClearValue(Animatable.TransitionsProperty),
                    TimeSpan.FromMilliseconds(520));
            }

            if (top == null || at is not { } p || p.Y > top.Bounds.Height || p.X > top.Bounds.Width)
            {
                control.ClearValue(Animatable.TransitionsProperty);
                control.Opacity = 1;
                control.RenderTransform = Rest;
                return;
            }

            var delay = Math.Clamp(p.X * 0.11 + p.Y * 0.16 - 40, 0, 240);
            if (delay < 12) Land();
            else DispatcherTimer.RunOnce(Land, TimeSpan.FromMilliseconds(delay));
        }, DispatcherPriority.Loaded);
    }

    /// <summary>A quick swell and a springy return: confirms a tap (like, add).</summary>
    public static void Pop(Control control, double peak = 1.32)
    {
        if (!Enabled) return;

        control.RenderTransformOrigin = RelativePoint.Center;
        control.Transitions ??= new Transitions();
        var transition = control.Transitions.OfType<TransformOperationsTransition>()
            .FirstOrDefault(t => t.Property == Visual.RenderTransformProperty);
        if (transition == null)
        {
            transition = new TransformOperationsTransition { Property = Visual.RenderTransformProperty };
            control.Transitions.Add(transition);
        }

        transition.Duration = TimeSpan.FromMilliseconds(110);
        transition.Easing = new CubicEaseOut();
        control.RenderTransform = TransformOperations.Parse(FormattableString.Invariant($"scale({peak})"));

        DispatcherTimer.RunOnce(() =>
        {
            transition.Duration = TimeSpan.FromMilliseconds(420);
            transition.Easing = Spring;
            control.RenderTransform = TransformOperations.Parse("scale(1)");
        }, TimeSpan.FromMilliseconds(115));
    }

    private static readonly Color[] SparkColors =
    {
        Color.FromRgb(0xFF, 0x7A, 0x33), Color.FromRgb(0xFF, 0xB0, 0x78), Color.FromRgb(0xFF, 0x5C, 0x6E)
    };

    /// <summary>
    /// The moment of a like: a ring opens around the heart and a few sparks fly out of it
    /// and fade. Drawn on the window's overlay, so no row, card or list clips them.
    /// </summary>
    public static void Burst(Control source)
    {
        if (!Enabled) return;
        var layer = Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(source);
        if (layer == null ||
            source.TranslatePoint(new Point(source.Bounds.Width / 2, source.Bounds.Height / 2), layer) is not { } centre)
            return;

        var stage = new Canvas { IsHitTestVisible = false };
        layer.Children.Add(stage);
        var reach = Math.Max(source.Bounds.Width, 14);

        Shape Place(Shape shape, double size)
        {
            shape.Width = size;
            shape.Height = size;
            Canvas.SetLeft(shape, centre.X - size / 2);
            Canvas.SetTop(shape, centre.Y - size / 2);
            shape.RenderTransformOrigin = RelativePoint.Center;
            stage.Children.Add(shape);
            return shape;
        }

        var ring = Place(new Ellipse { Stroke = new SolidColorBrush(SparkColors[0]), StrokeThickness = 1.6 }, reach * 1.1);
        ring.RenderTransform = TransformOperations.Parse("scale(0.5)");
        ring.Opacity = 0.9;

        var sparks = new List<(Shape Dot, double X, double Y)>();
        var turn = Random.Shared.NextDouble() * Math.PI;
        for (var i = 0; i < 8; i++)
        {
            var angle = turn + i * Math.PI / 4 + (Random.Shared.NextDouble() - 0.5) * 0.35;
            var distance = reach * (0.95 + Random.Shared.NextDouble() * 0.45);
            var dot = Place(new Ellipse { Fill = new SolidColorBrush(SparkColors[i % SparkColors.Length]) },
                i % 2 == 0 ? 4.2 : 3.2);
            dot.RenderTransform = TransformOperations.Parse("translateX(0px) translateY(0px) scale(1)");
            sparks.Add((dot, Math.Cos(angle) * distance, Math.Sin(angle) * distance));
        }

        foreach (var shape in stage.Children)
        {
            shape.Transitions = new Transitions
            {
                new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(560), Easing = new CubicEaseOut() },
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(560), Easing = new QuadraticEaseIn() }
            };
        }

        // The first values reach the screen, then everything flies.
        Dispatcher.UIThread.Post(() =>
        {
            ring.RenderTransform = TransformOperations.Parse("scale(1.7)");
            ring.Opacity = 0;
            foreach (var (dot, x, y) in sparks)
            {
                dot.RenderTransform = TransformOperations.Parse(FormattableString.Invariant(
                    $"translateX({x:0.#}px) translateY({y:0.#}px) scale(0.3)"));
                dot.Opacity = 0;
            }
        }, DispatcherPriority.Background);
        DispatcherTimer.RunOnce(() => layer.Children.Remove(stage), TimeSpan.FromMilliseconds(700));
    }

    /// <summary>Enters from an offset (pixels) while fading in.</summary>
    public static void SlideIn(Control control, double fromX, double fromY, int durationMs = 300)
    {
        var transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(durationMs * 0.8), Easing = new CubicEaseOut() },
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(durationMs), Easing = Emphasized }
        };

        control.Transitions = null;
        control.Opacity = Enabled ? 0 : 1;
        control.RenderTransform = TransformOperations.Parse(FormattableString.Invariant(
            $"translateX({(Enabled ? fromX : 0)}px) translateY({(Enabled ? fromY : 0)}px)"));
        control.Transitions = transitions;

        Dispatcher.UIThread.Post(() =>
        {
            control.Opacity = 1;
            control.RenderTransform = TransformOperations.Parse("translateX(0px) translateY(0px)");
        }, DispatcherPriority.Background);
    }
}
