using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Threading;

namespace SoundLibert.UI.Splash;

/// <summary>Hosts the drawing on the compositor.</summary>
internal sealed class SplashSurface : Control
{
    private readonly SplashArt _art;
    private readonly Action _leaving;
    private CompositionCustomVisual? _visual;
    private Action? _whenGone;

    public SplashSurface(SplashArt art, Action leaving)
    {
        _art = art;
        _leaving = leaving;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var host = ElementComposition.GetElementVisual(this);
        if (host == null) return;
        _visual = host.Compositor.CreateCustomVisual(new SplashHandler(_art,
            () => Dispatcher.UIThread.Post(_leaving),
            () => Dispatcher.UIThread.Post(Gone)));
        ElementComposition.SetElementChildVisual(this, _visual);
        _visual.Size = new Vector(Bounds.Width, Bounds.Height);
        _visual.SendHandlerMessage(SplashHandler.Begin);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_visual != null) _visual.Size = new Vector(e.NewSize.Width, e.NewSize.Height);
    }

    public void HandOver(Action whenGone)
    {
        _whenGone = whenGone;
        if (_visual == null)
        {
            _leaving();
            Gone();
        }
        else _visual.SendHandlerMessage(SplashHandler.Leave);
    }

    private void Gone()
    {
        var whenGone = _whenGone;
        _whenGone = null;
        whenGone?.Invoke();
    }
}

/// <summary>Runs on the render thread: one frame of the opening per screen refresh.</summary>
internal sealed class SplashHandler : CompositionCustomVisualHandler
{
    public static readonly object Begin = new();
    public static readonly object Leave = new();

    private readonly SplashArt _art;
    private readonly Action _fading;
    private readonly Action _gone;
    private TimeSpan? _start;
    private TimeSpan? _exitStart;
    private bool _leaving;
    private bool _finished;

    public SplashHandler(SplashArt art, Action fading, Action gone)
    {
        _art = art;
        _fading = fading;
        _gone = gone;
    }

    public override void OnMessage(object message)
    {
        if (message == Leave) _leaving = true;
        RegisterForNextAnimationFrameUpdate();
    }

    public override void OnAnimationFrameUpdate()
    {
        if (_finished) return;
        Invalidate();
        RegisterForNextAnimationFrameUpdate();
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        if (_finished) return;
        var now = CompositionNow;
        _start ??= now;
        var t = (now - _start.Value).TotalSeconds;
        if (_leaving && _exitStart == null && t >= SplashArt.ShortestStay)
        {
            _exitStart = now;
            _fading();
        }
        var exit = _exitStart is { } began ? Math.Clamp((now - began).TotalSeconds / SplashArt.ExitLength, 0, 1) : 0;

        var size = EffectiveSize;
        if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is { } skia)
        {
            using var lease = skia.Lease();
            _art.Draw(lease.SkCanvas, (float)size.X, (float)size.Y, t, exit);
        }
        else if (exit < 1)
        {
            // No Skia to draw with: the ground alone, and the app's window as soon as it is ready.
            context.DrawRectangle(new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x11)), null,
                new Rect(0, 0, size.X, size.Y));
            if (_leaving)
            {
                if (_exitStart == null) _fading();
                _exitStart ??= now;
                exit = 1;
            }
        }

        if (exit >= 1)
        {
            _finished = true;
            _gone();
        }
    }
}
