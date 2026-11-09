using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SoundLibert.UI.Controls;

/// <summary>
/// One wheel behaviour for the whole window: every notch glides to its target
/// instead of jumping, the deepest list that can still move takes the wheel, then
/// the page behind it, and a horizontal shelf answers to Shift + wheel.
/// </summary>
public static class SmoothScroller
{
    private const double NotchPixels = 110;
    private const double Sharpness = 15;

    /// <summary>
    /// For content that floats over a list (a page header): wheel over it scrolls
    /// that list.
    /// </summary>
    public static readonly AttachedProperty<ScrollViewer?> ForwardToProperty =
        AvaloniaProperty.RegisterAttached<Control, ScrollViewer?>("ForwardTo", typeof(SmoothScroller));

    public static ScrollViewer? GetForwardTo(Control control) => control.GetValue(ForwardToProperty);
    public static void SetForwardTo(Control control, ScrollViewer? value) => control.SetValue(ForwardToProperty, value);

    private sealed class Glide
    {
        public Vector Target;
        public bool Running;
        public long LastTick;
        public DispatcherTimer? HideBar;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, Glide> Glides = new();

    public static void Attach(TopLevel top) =>
        top.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled) return;

        var vertical = e.Delta.Y;
        var horizontal = e.Delta.X;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && horizontal == 0)
        {
            horizontal = vertical;
            vertical = 0;
        }

        for (var visual = e.Source as Visual; visual != null; visual = visual.GetVisualParent())
        {
            var scroller = visual as ScrollViewer ?? (visual is Control c ? GetForwardTo(c) : null);
            if (scroller == null) continue;

            if (ScrollBy(scroller, new Vector(-horizontal * NotchPixels, -vertical * NotchPixels)))
            {
                e.Handled = true;
                return;
            }
        }
    }

    /// <summary>Glides by a distance. False when it cannot move that way (the next one up may).</summary>
    public static bool ScrollBy(ScrollViewer scroller, Vector delta)
    {
        var glide = Glides.GetOrCreateValue(scroller);
        var from = glide.Running ? glide.Target : scroller.Offset;
        var to = Clamp(scroller, from + delta);
        if (Math.Abs(to.X - from.X) < 0.5 && Math.Abs(to.Y - from.Y) < 0.5) return false;

        ShowBar(scroller, glide);
        Start(scroller, glide, to);
        return true;
    }

    /// <summary>Glides to a position (the current lyric line), with no scrollbar flash.</summary>
    public static void ScrollTo(ScrollViewer scroller, Vector target) =>
        Start(scroller, Glides.GetOrCreateValue(scroller), Clamp(scroller, target));

    private static bool Start(ScrollViewer scroller, Glide glide, Vector to)
    {
        glide.Target = to;

        if (!Motion.Enabled)
        {
            scroller.Offset = to;
            return true;
        }

        if (!glide.Running)
        {
            glide.Running = true;
            glide.LastTick = Stopwatch.GetTimestamp();
            var top = TopLevel.GetTopLevel(scroller);
            if (top == null)
            {
                glide.Running = false;
                scroller.Offset = to;
                return true;
            }
            top.RequestAnimationFrame(_ => Step(scroller, glide, top));
        }
        return true;
    }

    private static void Step(ScrollViewer scroller, Glide glide, TopLevel top)
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = Math.Min((now - glide.LastTick) / (double)Stopwatch.Frequency, 0.05);
        glide.LastTick = now;

        var target = Clamp(scroller, glide.Target);
        var offset = scroller.Offset;
        var left = target - offset;

        if (Math.Abs(left.X) < 0.6 && Math.Abs(left.Y) < 0.6)
        {
            scroller.Offset = target;
            glide.Running = false;
            return;
        }

        var share = 1 - Math.Exp(-Sharpness * seconds);
        scroller.Offset = offset + left * share;
        top.RequestAnimationFrame(_ => Step(scroller, glide, top));
    }

    private static Vector Clamp(ScrollViewer scroller, Vector value)
    {
        var maxX = Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width);
        var maxY = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        if (scroller.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled) maxX = 0;
        if (scroller.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled) maxY = 0;
        return new Vector(Math.Clamp(value.X, 0, maxX), Math.Clamp(value.Y, 0, maxY));
    }

    /// <summary>The thin scrollbar shows only while the page is moving.</summary>
    private static void ShowBar(ScrollViewer scroller, Glide glide)
    {
        scroller.Classes.Set("scrolling", true);
        if (glide.HideBar == null)
        {
            glide.HideBar = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            glide.HideBar.Tick += (_, _) =>
            {
                glide.HideBar?.Stop();
                scroller.Classes.Set("scrolling", false);
            };
        }
        glide.HideBar.Stop();
        glide.HideBar.Start();
    }
}
