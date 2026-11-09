using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SoundLibert.UI.Controls;

namespace SoundLibert.UI.Welcome;

/// <summary>
/// A tilted wall of covers behind the welcome, drifting in slow columns. Neighbouring
/// columns go opposite ways, each at its own pace, and every column holds its covers twice,
/// so the drift never ends and never jumps. It stands still for those who asked for less
/// motion, and when the picture is drawn on the processor.
/// </summary>
internal sealed class CoverWall : Canvas
{
    private const double Tile = 172;
    private const double Gap = 18;
    private const double Step = Tile + Gap;
    private const double Tilt = -11;

    private readonly Canvas _wall = new();
    private readonly List<(Border Column, double Speed, double Start)> _columns = new();
    private IReadOnlyList<string> _covers = Array.Empty<string>();
    private int _builtFor;
    private double _period;
    private bool _running;
    private TimeSpan? _clockStart;

    public CoverWall()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
        _wall.RenderTransformOrigin = RelativePoint.Center;
        _wall.RenderTransform = new RotateTransform(Tilt);
        Children.Add(_wall);
    }

    /// <summary>The covers to show; the wall fills in as they load.</summary>
    public void SetCovers(IReadOnlyList<string> covers)
    {
        _covers = covers;
        _builtFor = 0;
        Build();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Build();
    }

    private void Build()
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0 || _covers.Count == 0) return;

        // What the tilted wall must cover so no corner of the window is left bare.
        var angle = Math.Abs(Tilt) * Math.PI / 180;
        var width = size.Width * Math.Cos(angle) + size.Height * Math.Sin(angle) + Step;
        var height = size.Width * Math.Sin(angle) + size.Height * Math.Cos(angle) + Step;
        var columns = (int)Math.Ceiling(width / Step) + 1;
        var perColumn = (int)Math.Ceiling(height / Step) + 1;
        var key = columns * 1000 + perColumn;

        _wall.Width = columns * Step;
        _wall.Height = height;
        SetLeft(_wall, (size.Width - _wall.Width) / 2);
        SetTop(_wall, (size.Height - height) / 2);
        if (key == _builtFor) return;
        _builtFor = key;

        _wall.Children.Clear();
        _columns.Clear();
        _period = perColumn * Step;
        var random = new Random(7);
        // New albums and trending songs mixed, the same way every time.
        var covers = _covers.OrderBy(_ => random.Next()).ToList();
        for (var c = 0; c < columns; c++)
        {
            var stack = new StackPanel { Spacing = Gap };
            // Twice the same covers: when one round has passed, the picture is where it began.
            for (var copy = 0; copy < 2; copy++)
                for (var k = 0; k < perColumn; k++)
                    stack.Children.Add(MakeTile(covers[(c * perColumn + k) % covers.Count]));

            var column = new Border { Child = stack, RenderTransform = new TranslateTransform() };
            SetLeft(column, c * Step);
            SetTop(column, 0);
            _wall.Children.Add(column);

            // Slow, each its own pace; one up, the next down.
            var speed = (9 + random.NextDouble() * 7) * (c % 2 == 0 ? -1 : 1);
            _columns.Add((column, speed, random.NextDouble() * _period));
        }
        Place(0);
        UpdateRunning();
    }

    private static Control MakeTile(string url) => new Border
    {
        Width = Tile,
        Height = Tile,
        CornerRadius = new CornerRadius(14),
        ClipToBounds = true,
        Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
        Child = new RemoteImage { Url = url, Stretch = Stretch.UniformToFill }
    };

    private void Place(double seconds)
    {
        foreach (var (column, speed, start) in _columns)
        {
            var travelled = (start + speed * seconds) % _period;
            if (travelled < 0) travelled += _period;
            ((TranslateTransform)column.RenderTransform!).Y = -travelled;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Motion.EnabledChanged += UpdateRunning;
        UpdateRunning();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Motion.EnabledChanged -= UpdateRunning;
        _running = false;
    }

    private void UpdateRunning()
    {
        var wanted = VisualRoot != null && _columns.Count > 0 && Motion.Enabled && !LiquidBackdrop.OnProcessor;
        if (wanted == _running) return;
        _running = wanted;
        if (wanted) TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        if (!_running) return;
        _clockStart ??= now;
        Place((now - _clockStart.Value).TotalSeconds);
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
    }
}
