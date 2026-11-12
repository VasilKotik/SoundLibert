using Avalonia;

namespace SoundLibert.UI.Controls;

/// <summary>
/// How far the pages keep from the edges. A window has room for wide margins; a phone
/// gives that room to the content. The phone's head sets <see cref="Compact"/> before
/// the first page is built (pages read these values as they are created).
/// </summary>
public static class PageLayout
{
    /// <summary>True on a phone: narrow margins, a shorter top.</summary>
    public static bool Compact { get; set; }

    /// <summary>Side margin of every page; rails bleed this far to reach the edge.</summary>
    public static double Inset => Compact ? 16 : 32;

    /// <summary>Space above a page's first line.</summary>
    public static double Top => Compact ? 14 : 30;

    public static Thickness Side => new(Inset, 0);
    public static Thickness SideAbove8 => new(Inset, 0, Inset, 8);
    public static Thickness SideAbove4 => new(Inset, 0, Inset, 4);

    /// <summary>Home and Library: the page's one long column.</summary>
    public static Thickness Body => new(Inset, Top, Inset, 40);

    /// <summary>Search: the field and filters, then the results.</summary>
    public static Thickness SearchHead => new(Inset, Top, Inset, 6);
    public static Thickness SearchBody => new(Inset, 14, Inset, 36);

    public static Thickness SettingsBody => new(Inset, Top, Inset, 0);
    public static Thickness ShelfPageBody => new(Inset, Compact ? 18 : 36, Inset, 40);

    /// <summary>Play counts beside a row's title: a phone gives that room to the title.</summary>
    public static bool ShowRowStats => !Compact;

    /// <summary>A row in a sideways shelf: narrower on a phone, so the next column shows.</summary>
    public static double ShelfRowWidth => Compact ? 300 : 352;

    /// <summary>The big header of an album, playlist or artist.</summary>
    public static Thickness Hero => Compact ? new(Inset, 20, Inset, 18) : new(32, 36, 32, 24);
}
