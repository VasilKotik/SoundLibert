using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace SoundLibert.UI.Controls;

/// <summary>
/// What is on screen or about to be. A page of shelves holds far more than anyone reaches, so
/// its shelves are built and its covers fetched only near the window. One clock serves them
/// all: it looks quickly while something moves, and now and then anyway, because a tab can come
/// back into view or the window grow without anything scrolling.
/// </summary>
internal static class NearScreen
{
    private static readonly TimeSpan Soon = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan NowAndThen = TimeSpan.FromMilliseconds(1200);

    private static readonly List<Func<bool>> Lookers = new();
    private static DispatcherTimer? _timer;
    private static bool _moved;

    static NearScreen()
    {
        ScrollViewer.ScrollChangedEvent.AddClassHandler<ScrollViewer>((_, _) => SomethingMoved(), handledEventsToo: true);
    }

    /// <summary>Look at the next tick, not at the next round.</summary>
    public static void SomethingMoved()
    {
        _moved = true;
        if (_timer != null && _timer.Interval != Soon) _timer.Interval = Soon;
    }

    /// <summary>Keeps asking <paramref name="look"/> for as long as anybody says they are still waiting.</summary>
    public static void Watch(Func<bool> look)
    {
        if (!Lookers.Contains(look)) Lookers.Add(look);
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = Soon };
            _timer.Tick += (_, _) => Tick();
        }
        SomethingMoved();
        _timer.Start();
    }

    private static void Tick()
    {
        _timer!.Interval = _moved ? Soon : NowAndThen;
        _moved = false;

        // A copy: building a shelf brings covers, and the first of them signs up here.
        var waiting = false;
        foreach (var look in Lookers.ToArray()) waiting |= look();
        if (!waiting && !_moved) _timer.Stop();
    }

    /// <summary>How many screens away it is: 0 on screen, null when that cannot be told yet.</summary>
    public static double? GapInScreens(Layoutable visual)
    {
        if (!visual.IsEffectivelyVisible || !visual.IsArrangeValid) return null;
        if (TopLevel.GetTopLevel(visual) is not { } window) return null;
        if (visual.TranslatePoint(default, window) is not { } at) return null;

        var screen = window.Bounds.Size;
        if (screen.Width <= 0 || screen.Height <= 0) return null;

        var across = Math.Max(Math.Max(-(at.X + visual.Bounds.Width), at.X - screen.Width), 0) / screen.Width;
        var down = Math.Max(Math.Max(-(at.Y + visual.Bounds.Height), at.Y - screen.Height), 0) / screen.Height;
        return Math.Max(across, down);
    }
}
