using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using SoundLibert.UI.Services;

namespace SoundLibert.UI.Controls;

/// <summary>
/// The wash of colour at the top of an album, playlist or artist page: taken from
/// that page's own cover, so every page opens in its own mood. Fades in once the
/// colour is known and melts into the sheet at the bottom.
/// </summary>
public sealed class CoverGlow : Control
{
    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<CoverGlow, string?>(nameof(Url));

    public string? Url { get => GetValue(UrlProperty); set => SetValue(UrlProperty, value); }

    private LinearGradientBrush? _brush;
    private int _generation;

    static CoverGlow()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<CoverGlow>(false);
        UrlProperty.Changed.AddClassHandler<CoverGlow>((glow, _) => glow.Load());
    }

    public CoverGlow()
    {
        Opacity = 0;
        Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(700) }
        };
    }

    private async void Load()
    {
        var generation = ++_generation;
        var url = Url;
        Opacity = 0;
        if (string.IsNullOrWhiteSpace(url)) return;

        var artwork = App.ServiceProvider?.GetService<ArtworkService>();
        if (artwork == null) return;

        var info = await artwork.GetAsync(url);
        if (generation != _generation || info.Image == null) return;

        var color = info.Color;
        _brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0xB0, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(0x48, color.R, color.G, color.B), 0.55),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1)
            }
        };
        InvalidateVisual();
        Opacity = 1;
    }

    public override void Render(DrawingContext context)
    {
        if (_brush != null)
            context.FillRectangle(_brush, new Rect(Bounds.Size));
    }
}
