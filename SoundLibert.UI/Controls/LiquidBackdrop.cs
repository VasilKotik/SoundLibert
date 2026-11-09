using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace SoundLibert.UI.Controls;

/// <summary>
/// The living colour behind the app: the cover, blown up until it is pure colour,
/// turning slowly in several layers so the colours of the song flow into each
/// other, with a soft glow breathing on top. It moves only while music plays,
/// cross-fades when the track changes and draws nothing while the window is hidden.
/// A cheap redraw on a graphics card: a tiny bitmap and a few gradients, no blur pass.
/// Without one (software rendering) it would take the whole app down to a few frames
/// per second, so there it stays a still picture.
/// </summary>
public sealed class LiquidBackdrop : Control
{
    private const double FadeSeconds = 1.1;

    /// <summary>Rendering on the processor: measured at 3-5 fps with moving colour, 20 without.</summary>
    private static bool _softwareRendering;
    private static bool _gpuChecked;

    private static bool CanMove => Motion.Enabled && !_softwareRendering;

    /// <summary>The window is drawn on the processor: what would move should stand still.</summary>
    public static bool OnProcessor => _softwareRendering;

    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<LiquidBackdrop, Bitmap?>(nameof(Source));

    public static readonly StyledProperty<Color> GlowColorProperty =
        AvaloniaProperty.Register<LiquidBackdrop, Color>(nameof(GlowColor), Color.FromRgb(0xFF, 0x7B, 0x3A));

    /// <summary>True while the music plays: the colours flow.</summary>
    public static readonly StyledProperty<bool> IsFlowingProperty =
        AvaloniaProperty.Register<LiquidBackdrop, bool>(nameof(IsFlowing));

    /// <summary>Layers of colour: more reads richer (the fullscreen player).</summary>
    public static readonly StyledProperty<int> LayersProperty =
        AvaloniaProperty.Register<LiquidBackdrop, int>(nameof(Layers), 3);

    /// <summary>Darkening over the colour, 0 (none) to 1 (black).</summary>
    public static readonly StyledProperty<double> DimProperty =
        AvaloniaProperty.Register<LiquidBackdrop, double>(nameof(Dim), 0.4);

    /// <summary>Strength of the breathing glow.</summary>
    public static readonly StyledProperty<double> GlowProperty =
        AvaloniaProperty.Register<LiquidBackdrop, double>(nameof(Glow), 0.3);

    public Bitmap? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public Color GlowColor { get => GetValue(GlowColorProperty); set => SetValue(GlowColorProperty, value); }
    public bool IsFlowing { get => GetValue(IsFlowingProperty); set => SetValue(IsFlowingProperty, value); }
    public int Layers { get => GetValue(LayersProperty); set => SetValue(LayersProperty, value); }
    public double Dim { get => GetValue(DimProperty); set => SetValue(DimProperty, value); }
    public double Glow { get => GetValue(GlowProperty); set => SetValue(GlowProperty, value); }

    private double _previousLight;
    private double _currentLight;
    private Bitmap? _previous;
    private Bitmap? _current;
    private Color _previousGlow;
    private Color _currentGlow;
    private double _mix = 1;
    private double _phase;

    private DispatcherTimer? _timer;
    private readonly Stopwatch _clock = new();

    // Each layer turns and wanders at its own pace, so the picture never repeats.
    private static readonly (double Spin, double Orbit, double Start, double Alpha)[] Motions =
    {
        (0.021, 0.037, 0.0, 1.00),
        (-0.034, 0.053, 2.1, 0.62),
        (0.047, 0.029, 4.0, 0.44),
        (-0.016, 0.071, 5.4, 0.30)
    };

    static LiquidBackdrop()
    {
        AffectsRender<LiquidBackdrop>(DimProperty, GlowProperty, LayersProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<LiquidBackdrop>(false);
        ClipToBoundsProperty.OverrideDefaultValue<LiquidBackdrop>(true);
    }

    public LiquidBackdrop()
    {
        _currentGlow = GlowColor;
        _previousGlow = GlowColor;
        Motion.EnabledChanged += UpdateTimer;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty || change.Property == GlowColorProperty)
        {
            BeginFade();
        }
        else if (change.Property == IsFlowingProperty || change.Property == IsVisibleProperty)
        {
            UpdateTimer();
        }
    }

    private void BeginFade()
    {
        // A second change during a fade restarts it from what is on screen now.
        if (!ReferenceEquals(_current, Source) || _currentGlow != GlowColor)
        {
            if (!ReferenceEquals(_current, Source))
            {
                _previous = _mix >= 0.5 ? _current : _previous;
                _previousLight = _mix >= 0.5 ? _currentLight : _previousLight;
                _current = Source;
                _currentLight = Lightness(Source);
            }
            _previousGlow = _mix >= 0.5 ? _currentGlow : _previousGlow;
            _currentGlow = GlowColor;
            _mix = !_attached || !CanMove ? 1 : 0;
            InvalidateVisual();
            UpdateTimer();
        }
    }

    private bool _attached;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _current = Source;
        _currentLight = Lightness(Source);
        _currentGlow = GlowColor;
        UpdateTimer();
        CheckForGraphicsCard();
    }

    /// <summary>No graphics interop means the compositor draws on the processor.</summary>
    private async void CheckForGraphicsCard()
    {
        if (_gpuChecked) return;
        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor == null) return;

        _gpuChecked = true;
        try
        {
            _softwareRendering = await compositor.TryGetCompositionGpuInterop() == null;
            Trace.WriteLine($"[Ambient] rendering on the {(_softwareRendering ? "processor: the background stays still" : "graphics card")}");
            if (_softwareRendering) Motion.NotifyChanged();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Ambient] graphics check failed: {ex.Message}");
        }
    }

    /// <summary>Average lightness of a cover, 0 to 1: a white album must not blind the UI.</summary>
    private static unsafe double Lightness(Bitmap? bitmap)
    {
        if (bitmap == null) return 0;
        try
        {
            var size = bitmap.PixelSize;
            var stride = size.Width * 4;
            var buffer = new byte[stride * size.Height];
            fixed (byte* p = buffer)
                bitmap.CopyPixels(new PixelRect(size), (IntPtr)p, buffer.Length, stride);

            long sum = 0;
            for (var i = 0; i < buffer.Length; i += 4)
                sum += buffer[i] + buffer[i + 1] + buffer[i + 2];
            return sum / (3.0 * 255 * Math.Max(1, buffer.Length / 4));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Ambient] lightness failed: {ex.Message}");
            return 0.3;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        _timer?.Stop();
    }

    private bool WantsFrames => _attached && IsVisible && (_mix < 1 || (IsFlowing && CanMove));

    private void UpdateTimer()
    {
        if (!WantsFrames)
        {
            _timer?.Stop();
            return;
        }

        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += OnFrame;
        }
        if (!_timer.IsEnabled)
        {
            _clock.Restart();
            _timer.Start();
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var elapsed = Math.Min(_clock.Elapsed.TotalSeconds, 0.25);
        _clock.Restart();

        if (_mix < 1)
        {
            _mix = Math.Min(1, _mix + elapsed / FadeSeconds);
            if (_mix >= 1) _previous = null;
        }

        var flowing = IsFlowing && CanMove;
        if (flowing) _phase += elapsed;

        // Nobody is looking: keep the clock, skip the drawing.
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window is { IsVisible: false } or { WindowState: WindowState.Minimized })
        {
            if (_timer != null) _timer.Interval = TimeSpan.FromMilliseconds(400);
            return;
        }
        if (_timer != null)
            _timer.Interval = TimeSpan.FromMilliseconds(window is { IsActive: false } ? 66 : 33);

        InvalidateVisual();
        if (!WantsFrames) _timer?.Stop();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var ease = _mix * _mix * (3 - 2 * _mix);

        var quality = _softwareRendering ? BitmapInterpolationMode.MediumQuality : BitmapInterpolationMode.HighQuality;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = quality }))
        {
            // The old picture stays solid under the new one; alone it fades out.
            if (_previous != null && _mix < 1)
                DrawCover(context, _previous, bounds, _current != null ? 1 : 1 - ease);
            if (_current != null)
                DrawCover(context, _current, bounds, ease);
        }

        if (Glow > 0)
        {
            if (_mix < 1) DrawGlows(context, bounds, _previousGlow, 1 - ease);
            DrawGlows(context, bounds, _currentGlow, ease);
        }

        // Bright covers get a little more veil, so the colour field keeps one level.
        var light = _previousLight + (_currentLight - _previousLight) * ease;
        var dim = Math.Clamp(Dim + Math.Max(0, light - 0.28) * 0.75, 0, 0.92);
        if (dim > 0)
            context.FillRectangle(new SolidColorBrush(Colors.Black, dim), bounds);
    }

    private void DrawCover(DrawingContext context, Bitmap bitmap, Rect bounds, double opacity)
    {
        var source = new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        var diagonal = Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height);
        var side = diagonal * 1.9;
        var layers = _softwareRendering ? 1 : Math.Clamp(Layers, 1, Motions.Length);

        for (var i = 0; i < layers; i++)
        {
            var (spin, orbit, start, alpha) = Motions[i];
            var angle = start + _phase * spin;
            // Wandering stays small: every layer must keep covering the window,
            // or its hard edge would cross the picture.
            var cx = bounds.Width * (0.5 + 0.16 * Math.Sin(_phase * orbit + start));
            var cy = bounds.Height * (0.5 + 0.16 * Math.Cos(_phase * orbit * 0.8 + start * 1.7));

            var transform = Matrix.CreateTranslation(-side / 2, -side / 2) *
                            Matrix.CreateRotation(angle) *
                            Matrix.CreateTranslation(cx, cy);

            using (context.PushTransform(transform))
            using (context.PushOpacity(alpha * opacity))
                context.DrawImage(bitmap, source, new Rect(0, 0, side, side));
        }
    }

    private void DrawGlows(DrawingContext context, Rect bounds, Color color, double opacity)
    {
        if (opacity <= 0.01) return;

        var reach = Math.Max(bounds.Width, bounds.Height);
        DrawGlow(context, color, opacity,
            bounds.Width * (0.18 + 0.1 * Math.Sin(_phase * 0.11)),
            bounds.Height * (0.12 + 0.12 * Math.Cos(_phase * 0.09)),
            reach * 0.62, 0.75 + 0.25 * Math.Sin(_phase * 0.31));

        DrawGlow(context, color, opacity,
            bounds.Width * (0.84 + 0.1 * Math.Sin(_phase * 0.07 + 2)),
            bounds.Height * (0.86 + 0.1 * Math.Cos(_phase * 0.13 + 1)),
            reach * 0.72, 0.75 + 0.25 * Math.Sin(_phase * 0.23 + 2.2));
    }

    private void DrawGlow(DrawingContext context, Color color, double opacity,
        double x, double y, double radius, double breath)
    {
        var strength = Glow * opacity * breath;
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(255 * Math.Clamp(strength, 0, 1)), color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb((byte)(120 * Math.Clamp(strength, 0, 1)), color.R, color.G, color.B), 0.45),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1)
            }
        };
        context.DrawEllipse(brush, null, new Point(x, y), radius, radius);
    }
}
