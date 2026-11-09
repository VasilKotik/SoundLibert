using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;

namespace SoundLibert.UI.Controls;

/// <summary>
/// The small "now playing" bars. They dance while music plays and settle low on
/// pause. All instances share one clock, which only runs while one is on screen.
/// </summary>
public sealed class EqBars : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<EqBars>();

    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private static readonly List<EqBars> Live = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static DispatcherTimer? _timer;
    private static bool _isPlaying;

    /// <summary>0 = settled, 1 = dancing; eases between the two.</summary>
    private static double _energy;

    /// <summary>Set by the shell from the player state.</summary>
    public static bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            UpdateTimer();
        }
    }

    static EqBars()
    {
        AffectsRender<EqBars>(ForegroundProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<EqBars>(false);
        Motion.EnabledChanged += UpdateTimer;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Live.Add(this);
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Live.Remove(this);
        UpdateTimer();
    }

    private static void UpdateTimer()
    {
        var wanted = Live.Count > 0 && (_isPlaying || _energy > 0.01) && Motion.Enabled;
        if (wanted)
        {
            _timer ??= CreateTimer();
            if (!_timer.IsEnabled) _timer.Start();
        }
        else
        {
            _timer?.Stop();
        }
    }

    private static DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            _energy += ((_isPlaying ? 1 : 0) - _energy) * 0.14;
            foreach (var bars in Live)
                if (bars.IsEffectivelyVisible) bars.InvalidateVisual();
            UpdateTimer();
        };
        return timer;
    }

    protected override Size MeasureOverride(Size availableSize) => new(14, 12);

    public override void Render(DrawingContext context)
    {
        var brush = Foreground ?? Brushes.White;
        var height = Bounds.Height;
        const int count = 4;
        var gap = Bounds.Width * 0.12;
        var barWidth = (Bounds.Width - gap * (count - 1)) / count;
        var t = Clock.Elapsed.TotalSeconds;

        for (var i = 0; i < count; i++)
        {
            // Two unrelated sines per bar read as music, not as a loop.
            var wave = 0.5 + 0.28 * Math.Sin(t * (5.3 + i * 1.7) + i * 1.9) + 0.22 * Math.Sin(t * (8.9 - i * 1.1) + i);
            var level = 0.22 + (Motion.Enabled ? _energy : 0.6) * 0.78 * Math.Clamp(wave, 0, 1);
            var barHeight = Math.Max(barWidth, height * level);
            var x = i * (barWidth + gap);
            var rect = new Rect(x, height - barHeight, barWidth, barHeight);
            context.DrawRectangle(brush, null, new RoundedRect(rect, barWidth / 2));
        }
    }
}
