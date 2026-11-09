using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SoundLibert.UI.Controls;

/// <summary>
/// Makes a page header scroll away with a list that keeps its virtualisation (a big
/// playlist cannot live inside an outer scroller). The header floats over the list,
/// the list reserves its height as top padding, and every scroll moves the header
/// up by the same amount, fading as it goes. An optional compact bar takes over at
/// the top once the header has gone. A footer works the same way from the other end:
/// the list leaves room for it after its last row and it moves along.
/// </summary>
public sealed class ScrollHeader
{
    private readonly Control _header;
    private readonly Control? _compact;
    private readonly List<ListBox> _boxes = new();
    private readonly List<ScrollViewer> _plain = new();
    private readonly List<ScrollViewer> _scrollers = new();
    private readonly TranslateTransform _shift = new();
    private readonly TranslateTransform _footerShift = new();
    private Control? _footer;

    private const double BottomRoom = 28;
    private const double FooterGap = 26;

    private ScrollHeader(Control header, Control? compact)
    {
        _header = header;
        _compact = compact;
        _header.RenderTransform = _shift;
        _header.SizeChanged += (_, _) => ApplyHeight();
    }

    /// <summary>Binds a header to the lists under it (list boxes or plain scrollers).</summary>
    public static ScrollHeader Attach(Control header, Control? compact, params Control[] lists)
    {
        var binder = new ScrollHeader(header, compact);
        foreach (var list in lists)
        {
            switch (list)
            {
                case ListBox box:
                    binder.AddBox(box);
                    break;
                case ScrollViewer scroller:
                    binder._plain.Add(scroller);
                    binder.Track(scroller);
                    break;
            }
        }
        return binder;
    }

    /// <summary>What comes after the last row.</summary>
    public ScrollHeader WithFooter(Control footer)
    {
        _footer = footer;
        footer.RenderTransform = _footerShift;
        footer.SizeChanged += (_, _) => ApplyHeight();
        footer.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty) ApplyHeight();
        };
        ApplyHeight();
        return this;
    }

    private double FooterHeight => _footer is { IsVisible: true } ? _footer.Bounds.Height : 0;

    private void AddBox(ListBox box)
    {
        _boxes.Add(box);

        void Find()
        {
            var inner = box.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (inner != null && !_scrollers.Contains(inner)) Track(inner);
        }

        box.TemplateApplied += (_, _) => Dispatcher.UIThread.Post(Find);
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty) Dispatcher.UIThread.Post(() => { Find(); Update(); });
        };
    }

    private void Track(ScrollViewer scroller)
    {
        _scrollers.Add(scroller);
        scroller.ScrollChanged += (_, _) => Update();
        scroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty) Update();
        };
        ApplyHeight();
    }

    private void ApplyHeight()
    {
        var height = _header.Bounds.Height;
        var bottom = FooterHeight > 0 ? BottomRoom + FooterGap + FooterHeight : BottomRoom;
        foreach (var box in _boxes) box.Padding = new Thickness(0, height, 0, bottom);
        foreach (var scroller in _plain)
            if (scroller.Content is Control content) content.Margin = new Thickness(0, height, 0, bottom);
        Update();
    }

    private void Update()
    {
        var active = _scrollers.FirstOrDefault(s => s.IsEffectivelyVisible);
        SmoothScroller.SetForwardTo(_header, active);

        var offset = active?.Offset.Y ?? 0;
        var height = Math.Max(1, _header.Bounds.Height);

        _shift.Y = -offset;
        _header.Opacity = Math.Clamp(1.2 - offset / (height * 0.8), 0, 1);
        _header.IsHitTestVisible = offset < height - 8;

        if (_compact != null)
        {
            var shown = offset > height - 90;
            _compact.Opacity = shown ? 1 : 0;
            _compact.IsHitTestVisible = shown;
        }

        if (_footer != null)
        {
            SmoothScroller.SetForwardTo(_footer, active);
            _footerShift.Y = (active?.Extent.Height ?? 0) - BottomRoom - FooterHeight - offset;
        }
    }

    /// <summary>A fresh collection opened: start again from the top.</summary>
    public void Reset()
    {
        foreach (var scroller in _scrollers) scroller.Offset = default;
        Update();
    }
}
