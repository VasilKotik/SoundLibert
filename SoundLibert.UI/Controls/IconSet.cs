using System.Globalization;
using Avalonia.Media;

namespace SoundLibert.UI.Controls;

/// <summary>
/// The one icon family of the app. Every glyph is vector geometry on a 24×24
/// grid, so the same set stays crisp at any size and on any display, and does
/// not depend on which icon font a Windows edition ships.
/// </summary>
public enum IconKind
{
    None,

    // Navigation and window
    Home, Library, Search, Settings, User, Back, Forward,
    ChevronDown, ChevronUp, ChevronLeft, ChevronRight,
    Close, More, Plus, Check,
    WindowMinimize, WindowMaximize, WindowRestore, WindowClose,

    // Transport
    Play, Pause, Next, Previous, Shuffle, Repeat, RepeatOne,
    VolumeHigh, VolumeLow, VolumeMute,

    // Now playing
    Heart, Queue, Lyrics, Moon, MiniPlayer, Expand, Collapse, Video, Music,
    Download, Downloaded, Sparkles, Radio,

    // Library
    Album, Artist, Clock, History, Folder, Bookmark, Trash, Edit, Grip,
    AddToPlaylist, PlayNext, AddToQueue, ArrowUp, ArrowDown, Duplicate,

    // Settings and feedback
    Audio, Monitor, Sliders, Refresh, Update, Bug, External, Warning, Info,
    Offline, Link, Copy, SignOut, SignIn, Keyboard, Globe, Code, Sort, Filter, Star
}

/// <summary>Geometry for one icon: outline, optional solid form.</summary>
internal sealed record IconShape(Geometry? Outline, Geometry? Solid);

/// <summary>
/// Path data for <see cref="IconKind"/>. Drawn in the Lucide construction
/// (24 grid, round caps and joins, see CREDITS.md). Icons that have a closed
/// silhouette also get a solid form, used for the "on" state; transport glyphs
/// are solid only, because a hollow play button reads as disabled.
/// </summary>
internal static class IconSet
{
    private static readonly Dictionary<IconKind, IconShape> Cache = new();

    public static IconShape Get(IconKind kind)
    {
        if (Cache.TryGetValue(kind, out var shape)) return shape;

        var (outline, solid) = Source(kind);
        shape = new IconShape(
            outline == null ? null : Geometry.Parse(outline),
            solid == null ? null : Geometry.Parse(solid));
        Cache[kind] = shape;
        return shape;
    }

    private static string C(double cx, double cy, double r) => string.Create(CultureInfo.InvariantCulture,
        $"M{cx - r} {cy}a{r} {r} 0 1 0 {2 * r} 0a{r} {r} 0 1 0 {-2 * r} 0z");

    private static string R(double x, double y, double w, double h, double r) => string.Create(CultureInfo.InvariantCulture,
        $"M{x + r} {y}h{w - 2 * r}a{r} {r} 0 0 1 {r} {r}v{h - 2 * r}a{r} {r} 0 0 1 {-r} {r}h{-(w - 2 * r)}a{r} {r} 0 0 1 {-r} {-r}v{-(h - 2 * r)}a{r} {r} 0 0 1 {r} {-r}z");

    private const string HomeBody = "M3 10a2 2 0 0 1 .71-1.53l7-6a2 2 0 0 1 2.58 0l7 6A2 2 0 0 1 21 10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z";
    private const string HeartBody = "M19 14c1.49-1.46 3-3.21 3-5.5A5.5 5.5 0 0 0 16.5 3c-1.76 0-3 .5-4.5 2-1.5-1.5-2.74-2-4.5-2A5.5 5.5 0 0 0 2 8.5c0 2.3 1.5 4.05 3 5.5l7 7z";
    private const string StarBody = "M12 2.8l2.7 6.28 6.81.63-5.14 4.51 1.5 6.67L12 17.4l-5.88 3.49 1.5-6.67-5.13-4.51 6.81-.63z";
    private const string Speaker = "M11 4.7a.7.7 0 0 0-1.2-.5L6.41 7.59A1.4 1.4 0 0 1 5.42 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.42a1.4 1.4 0 0 1 .99.41l3.39 3.39a.7.7 0 0 0 1.2-.5z";
    private const string BookmarkBody = "M19 21l-7-4-7 4V5a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2z";
    private const string MoonBody = "M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9z";

    private static (string? Outline, string? Solid) Source(IconKind kind) => kind switch
    {
        // ---- Navigation and window ----
        IconKind.Home => (HomeBody + "M15 21v-8a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v8",
            "M3 10a2 2 0 0 1 .71-1.53l7-6a2 2 0 0 1 2.58 0l7 6A2 2 0 0 1 21 10v9a2 2 0 0 1-2 2h-5v-7a1.5 1.5 0 0 0-1.5-1.5h-1A1.5 1.5 0 0 0 10 14v7H5a2 2 0 0 1-2-2z"),
        IconKind.Library => ("M16 6l4 14M12 6v14M8 8v12M4 4v16",
            "M3 4a1 1 0 0 1 2 0v16a1 1 0 0 1-2 0zM7 8a1 1 0 0 1 2 0v12a1 1 0 0 1-2 0zM11 6a1 1 0 0 1 2 0v14a1 1 0 0 1-2 0zM15.04 6.28a1 1 0 0 1 1.92-.56l4 14a1 1 0 0 1-1.92.56z"),
        IconKind.Search => (C(11, 11, 8) + "M21 21l-4.3-4.3", null),
        IconKind.Settings => ("M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z" + C(12, 12, 3), null),
        IconKind.User => (C(12, 8, 5) + "M20 21a8 8 0 0 0-16 0", null),
        IconKind.Back => ("M12 19l-7-7 7-7M19 12H5", null),
        IconKind.Forward => ("M5 12h14M12 5l7 7-7 7", null),
        IconKind.ChevronDown => ("M6 9l6 6 6-6", null),
        IconKind.ChevronUp => ("M18 15l-6-6-6 6", null),
        IconKind.ChevronLeft => ("M15 18l-6-6 6-6", null),
        IconKind.ChevronRight => ("M9 18l6-6-6-6", null),
        IconKind.Close => ("M18 6L6 18M6 6l12 12", null),
        IconKind.More => (null, C(5, 12, 1.15) + C(12, 12, 1.15) + C(19, 12, 1.15)),
        IconKind.Plus => ("M5 12h14M12 5v14", null),
        IconKind.Check => ("M20 6L9 17l-5-5", null),
        IconKind.WindowMinimize => ("M6 12h12", null),
        IconKind.WindowMaximize => (R(6, 6, 12, 12, 1.5), null),
        IconKind.WindowRestore => (R(5.5, 8.5, 10, 10, 1.5) + "M8.5 5.5h8.5a1.5 1.5 0 0 1 1.5 1.5v8.5", null),
        IconKind.WindowClose => ("M6.5 6.5l11 11M17.5 6.5l-11 11", null),

        // ---- Transport ----
        IconKind.Play => (null, "M7 5.1c0-1.1 1.2-1.8 2.2-1.2l10.6 6.9c.9.6.9 1.9 0 2.5L9.2 20.1c-1 .6-2.2-.1-2.2-1.2z"),
        IconKind.Pause => (null, R(6, 4, 4.5, 16, 1.4) + R(13.5, 4, 4.5, 16, 1.4)),
        IconKind.Next => (null, "M4.5 6c0-1.1 1.2-1.8 2.2-1.2l9 6c.9.6.9 1.9 0 2.5l-9 6c-1 .6-2.2-.1-2.2-1.2z" + R(17.8, 4.6, 2.4, 14.8, 1.2)),
        IconKind.Previous => (null, "M19.5 6c0-1.1-1.2-1.8-2.2-1.2l-9 6c-.9.6-.9 1.9 0 2.5l9 6c1 .6 2.2-.1 2.2-1.2z" + R(3.8, 4.6, 2.4, 14.8, 1.2)),
        IconKind.Shuffle => ("M18 14l4 4-4 4M18 2l4 4-4 4M2 18h1.97a4 4 0 0 0 3.3-1.7l5.45-8.6a4 4 0 0 1 3.3-1.7H22M2 6h1.97a4 4 0 0 1 3.6 2.2M22 18h-6.04a4 4 0 0 1-3.3-1.8l-.36-.45", null),
        IconKind.Repeat => ("M17 2l4 4-4 4M3 11v-1a4 4 0 0 1 4-4h14M7 22l-4-4 4-4M21 13v1a4 4 0 0 1-4 4H3", null),
        IconKind.RepeatOne => ("M17 2l4 4-4 4M3 11v-1a4 4 0 0 1 4-4h14M7 22l-4-4 4-4M21 13v1a4 4 0 0 1-4 4H3M11 10.5l1.2-.5v4.2", null),
        IconKind.VolumeHigh => (Speaker + "M16 9a5 5 0 0 1 0 6M19.36 18.36a9 9 0 0 0 0-12.72", null),
        IconKind.VolumeLow => (Speaker + "M16 9a5 5 0 0 1 0 6", null),
        IconKind.VolumeMute => (Speaker + "M22 9l-6 6M16 9l6 6", null),

        // ---- Now playing ----
        IconKind.Heart => (HeartBody, HeartBody),
        IconKind.Queue => ("M21 15V6M12 12H3M16 6H3M12 18H3" + C(18.5, 15.5, 2.5), null),
        IconKind.Lyrics => ("M11 7.6L5 15.79a1 1 0 0 0 .1 1.3l.82.82a1 1 0 0 0 1.31.08L15.09 12M16.5 21.17C15.5 20.5 14.37 20 13 20c-2.06 0-3.93 2.36-6 2-2.07-.36-2.78-3.37-1.5-4.5" + C(16, 7, 5), null),
        IconKind.Moon => (MoonBody, MoonBody),
        IconKind.MiniPlayer => ("M21 9V6a2 2 0 0 0-2-2H4a2 2 0 0 0-2 2v10a2 2 0 0 0 2 2h4" + R(12, 13, 10, 7, 2), null),
        IconKind.Expand => ("M15 3h6v6M21 3l-7 7M3 21l7-7M9 21H3v-6", null),
        IconKind.Collapse => ("M14 10l7-7M20 10h-6V4M3 21l7-7M4 14h6v6", null),
        IconKind.Video => ("M16 13l5.22 3.48a.5.5 0 0 0 .78-.42V7.87a.5.5 0 0 0-.75-.43L16 10.5" + R(2, 6, 14, 12, 2), null),
        IconKind.Music => ("M9 18V5l12-2v13" + C(6, 18, 3) + C(18, 16, 3), null),
        IconKind.Download => ("M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M7 10l5 5 5-5M12 15V3", null),
        IconKind.Downloaded => (C(12, 12, 10) + "M8 12.2l2.8 2.8L16 9.5", null),
        IconKind.Sparkles => ("M9.94 15.5A2 2 0 0 0 8.5 14.06l-6.14-1.58a.5.5 0 0 1 0-.96L8.5 9.94A2 2 0 0 0 9.94 8.5l1.58-6.14a.5.5 0 0 1 .96 0L14.06 8.5A2 2 0 0 0 15.5 9.94l6.14 1.58a.5.5 0 0 1 0 .96L15.5 14.06a2 2 0 0 0-1.44 1.44l-1.58 6.14a.5.5 0 0 1-.96 0z", null),
        IconKind.Radio => ("M4.9 19.1C1 15.2 1 8.8 4.9 4.9M7.8 16.2c-2.3-2.3-2.3-6.1 0-8.5M16.2 7.8c2.3 2.3 2.3 6.1 0 8.5M19.1 4.9C23 8.8 23 15.1 19.1 19" + C(12, 12, 2), null),

        // ---- Library ----
        IconKind.Album => (C(12, 12, 10) + C(12, 12, 2.5) + "M6 12c0-1.7.7-3.2 1.8-4.2M18 12c0 1.7-.7 3.2-1.8 4.2", null),
        IconKind.Artist => ("M11 7.6L5 15.79a1 1 0 0 0 .1 1.3l.82.82a1 1 0 0 0 1.31.08L15.09 12" + C(16, 7, 5), null),
        IconKind.Clock => (C(12, 12, 10) + "M12 6v6l4 2", null),
        IconKind.History => ("M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8M3 3v5h5M12 7v5l4 2", null),
        IconKind.Folder => ("M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2z", null),
        IconKind.Bookmark => (BookmarkBody, BookmarkBody),
        IconKind.Trash => ("M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2M10 11v6M14 11v6", null),
        IconKind.Edit => ("M21.17 6.81a1 1 0 0 0-3.99-3.99L3.84 16.17a2 2 0 0 0-.5.83l-1.32 4.35a.5.5 0 0 0 .62.62l4.35-1.32a2 2 0 0 0 .83-.5zM15 5l4 4", null),
        IconKind.Grip => (null, C(9, 6, 1.2) + C(9, 12, 1.2) + C(9, 18, 1.2) + C(15, 6, 1.2) + C(15, 12, 1.2) + C(15, 18, 1.2)),
        IconKind.AddToPlaylist => ("M11 12H3M16 6H3M16 18H3M18 9v6M21 12h-6", null),
        IconKind.PlayNext => ("M16 12H3M16 18H3M10 6H3M21 18V8a2 2 0 0 0-2-2h-5M16 8l-2-2 2-2", null),
        IconKind.AddToQueue => ("M16 12H3M16 6H3M10 18H3M21 6v10a2 2 0 0 1-2 2h-5M16 16l-2 2 2 2", null),
        IconKind.ArrowUp => ("M5 12l7-7 7 7M12 19V5", null),
        IconKind.ArrowDown => ("M12 5v14M19 12l-7 7-7-7", null),
        IconKind.Duplicate => (R(8, 8, 14, 14, 2) + "M4 16a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2", null),

        // ---- Settings and feedback ----
        IconKind.Audio => ("M2 10v3M6 6v11M10 3v18M14 8v7M18 5v13M22 10v3", null),
        IconKind.Monitor => (R(2, 3, 20, 14, 2) + "M8 21h8M12 17v4", null),
        IconKind.Sliders => ("M21 4h-7M10 4H3M21 12h-9M8 12H3M21 20h-5M12 20H3M14 2v4M8 10v4M16 18v4", null),
        IconKind.Refresh => ("M21 12a9 9 0 1 1-9-9c2.52 0 4.93 1 6.74 2.74L21 8M21 3v5h-5", null),
        IconKind.Update => (C(12, 12, 10) + "M16 12l-4-4-4 4M12 16V8", null),
        IconKind.Star => (StarBody, StarBody),
        IconKind.Bug => ("M8 2l1.88 1.88M14.12 3.88L16 2M9 7.13v-1a3 3 0 1 1 6 0v1M12 20c-3.3 0-6-2.7-6-6v-3a4 4 0 0 1 4-4h4a4 4 0 0 1 4 4v3c0 3.3-2.7 6-6 6M12 20v-9M6.53 9C4.6 8.8 3 7.1 3 5M6 13H2M3 21c0-2.1 1.7-3.9 3.8-4M20.97 5c0 2.1-1.6 3.8-3.5 4M22 13h-4M17.2 17c2.1.1 3.8 1.9 3.8 4", null),
        IconKind.External => ("M15 3h6v6M10 14L21 3M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6", null),
        IconKind.Warning => ("M21.73 18l-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3zM12 9v4M12 17h.01", null),
        IconKind.Info => (C(12, 12, 10) + "M12 16v-4M12 8h.01", null),
        IconKind.Offline => ("M12 20h.01M8.5 16.43a5 5 0 0 1 7 0M5 12.86a10 10 0 0 1 5.17-2.69M19 12.86a10 10 0 0 0-2.01-1.52M2 8.82a15 15 0 0 1 4.18-2.64M22 8.82a15 15 0 0 0-11.29-3.76M2 2l20 20", null),
        IconKind.Link => ("M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71", null),
        IconKind.Copy => (R(8, 8, 14, 14, 2) + "M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2", null),
        IconKind.SignOut => ("M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M16 17l5-5-5-5M21 12H9", null),
        IconKind.SignIn => ("M15 3h4a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2h-4M10 17l5-5-5-5M15 12H3", null),
        IconKind.Keyboard => (R(2, 4, 20, 16, 2) + "M6 8h.01M10 8h.01M14 8h.01M18 8h.01M8 12h.01M12 12h.01M16 12h.01M7 16h10", null),
        IconKind.Globe => (C(12, 12, 10) + "M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20M2 12h20", null),
        IconKind.Code => ("M16 18l6-6-6-6M8 6l-6 6 6 6", null),
        IconKind.Sort => ("M3 6h18M7 12h10M10 18h4", null),
        IconKind.Filter => ("M3 6h18M7 12h10M10 18h4", null),

        _ => (null, null)
    };
}
