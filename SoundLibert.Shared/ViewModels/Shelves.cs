using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using SoundLibert.Domain.Models;
using SoundLibert.Domain.Video;
using SoundLibert.UI.Controls;

namespace SoundLibert.UI.ViewModels;

/// <summary>One entry of a shelf, as the screen shows it.</summary>
public sealed class ShelfCard
{
    public ShelfCard(ShelfItem item) => Item = item;

    public ShelfItem Item { get; }

    /// <summary>The shelf it sits on: playing a song queues its neighbours.</summary>
    public ShelfVm? Shelf { get; internal set; }

    public string Title => Item.Title;
    public string Subtitle => Item.Subtitle;
    public string ThumbnailUrl => Item.ThumbnailUrl;
    public bool IsArtist => Item.Kind == ShelfItemKind.Artist;

    public CoverCard.CardShape Shape => Item.Kind switch
    {
        ShelfItemKind.Artist => CoverCard.CardShape.Round,
        ShelfItemKind.Video => CoverCard.CardShape.Wide,
        _ => CoverCard.CardShape.Square
    };

    public double CardWidth => IsArtist ? 148 : 164;
    public bool CanPlay => !IsArtist;
    public string? Badge => Item.Song?.DurationBadge;
    public bool HasRank => Item.Rank != null;
    public string RankText => Item.Rank?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    public bool TrendsUp => Item.Trend == ChartTrend.Up;
    public bool TrendsDown => Item.Trend == ChartTrend.Down;
    public CornerRadius ThumbRadius => new(IsArtist ? 24 : 7);
}

/// <summary>A titled row of a page: what <c>ShelfStack</c> draws.</summary>
public abstract class ShelfVm
{
    /// <summary>The small line above the title ("BECAUSE YOU LISTENED TO").</summary>
    public string? Eyebrow { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>Where "Show all" leads on YouTube's side.</summary>
    public string? MoreBrowseId { get; init; }

    public string? MoreParams { get; init; }

    /// <summary>"Show all" opens an endless page of videos.</summary>
    public Func<IVideoFeed>? OpenFeed { get; set; }

    /// <summary>"Show all" does something the page knows about.</summary>
    public Action? ShowAllAction { get; set; }

    /// <summary>Which feed to reattach when the shelf comes back from the launch cache.</summary>
    public string? FeedKey { get; init; }

    /// <summary>How far its rail was scrolled when the page let the shelf go.</summary>
    public double RailOffset { get; set; }

    /// <summary>Its rail has been scrolled near the end of what it draws.</summary>
    public virtual void ShowMore()
    {
    }

    public bool HasEyebrow => !string.IsNullOrWhiteSpace(Eyebrow);
    public bool HasTitle => !string.IsNullOrWhiteSpace(Title);
    public bool HasShowAll => !string.IsNullOrEmpty(MoreBrowseId) || OpenFeed != null || ShowAllAction != null;

    /// <summary>A shelf can be longer than a page should load at once; its "Show all" has the rest.</summary>
    public const int RailLimit = 24;

    public static ShelfVm From(Shelf shelf, string? title = null, string? eyebrow = null, string? feedKey = null, Func<IVideoFeed>? openFeed = null)
    {
        var cards = shelf.Items.Take(shelf.Layout == ShelfLayout.Rows ? 40 : RailLimit).Select(i => new ShelfCard(i)).ToList();
        var name = title ?? shelf.Title;
        if (shelf.Layout == ShelfLayout.Rows)
            return new RowShelfVm(cards) { Title = name, Eyebrow = eyebrow, MoreBrowseId = shelf.MoreBrowseId, MoreParams = shelf.MoreParams, FeedKey = feedKey, OpenFeed = openFeed };
        if (cards.Count > 0 && cards.All(c => c.Item.Kind == ShelfItemKind.Video))
            return new VideoShelfVm(cards) { Title = name, Eyebrow = eyebrow, MoreBrowseId = shelf.MoreBrowseId, MoreParams = shelf.MoreParams, FeedKey = feedKey, OpenFeed = openFeed };
        return new CardShelfVm(cards) { Title = name, Eyebrow = eyebrow, MoreBrowseId = shelf.MoreBrowseId, MoreParams = shelf.MoreParams, FeedKey = feedKey, OpenFeed = openFeed };
    }
}

/// <summary>
/// A shelf that is a rail. It draws what fits and a little more: of 24 covers six or seven
/// are on screen, and building the others is what made a long page stutter on its way down.
/// The rest comes as the rail is scrolled that far.
/// </summary>
public abstract class RailShelfVm<T> : ShelfVm
{
    private readonly int _step;
    private ObservableCollection<T>? _shown;

    protected RailShelfVm(int step) => _step = step;

    protected abstract IReadOnlyList<T> All { get; }

    /// <summary>What the rail draws so far.</summary>
    public ObservableCollection<T> Shown => _shown ??= new ObservableCollection<T>(All.Take(_step));

    public override void ShowMore()
    {
        foreach (var item in All.Skip(Shown.Count).Take(_step).ToList()) Shown.Add(item);
    }
}

/// <summary>Covers side by side.</summary>
public sealed class CardShelfVm : RailShelfVm<ShelfCard>
{
    public CardShelfVm(IReadOnlyList<ShelfCard> items) : base(step: 10)
    {
        Items = items;
        foreach (var card in items) card.Shelf = this;
    }

    public IReadOnlyList<ShelfCard> Items { get; }

    protected override IReadOnlyList<ShelfCard> All => Items;
}

/// <summary>Wide video pictures side by side.</summary>
public sealed class VideoShelfVm : RailShelfVm<ShelfCard>
{
    public VideoShelfVm(IReadOnlyList<ShelfCard> items) : base(step: 6)
    {
        Items = items;
        foreach (var card in items) card.Shelf = this;
    }

    public IReadOnlyList<ShelfCard> Items { get; }

    protected override IReadOnlyList<ShelfCard> All => Items;
}

/// <summary>Compact rows standing in columns: quick picks, charts.</summary>
public sealed class RowShelfVm : RailShelfVm<IReadOnlyList<ShelfCard>>
{
    private const int RowsPerColumn = 4;

    public RowShelfVm(IReadOnlyList<ShelfCard> items) : base(step: 4)
    {
        Items = items;
        foreach (var card in items) card.Shelf = this;
        Columns = items.Chunk(RowsPerColumn).Select(c => (IReadOnlyList<ShelfCard>)c).ToList();
    }

    public IReadOnlyList<ShelfCard> Items { get; }
    public IReadOnlyList<IReadOnlyList<ShelfCard>> Columns { get; }

    protected override IReadOnlyList<IReadOnlyList<ShelfCard>> All => Columns;
}

/// <summary>A page-wide grid of covers that grows as the page scrolls.</summary>
public sealed class GridShelfVm : ShelfVm
{
    public ObservableCollection<ShelfCard> Items { get; } = new();

    public void Add(IEnumerable<ShelfCard> cards)
    {
        foreach (var card in cards)
        {
            card.Shelf = this;
            Items.Add(card);
        }
    }
}

/// <summary>Coloured mood and genre tiles in a row.</summary>
public sealed class TileRailVm : RailShelfVm<MoodTileVm>
{
    public TileRailVm() : base(step: 10)
    {
    }

    public IReadOnlyList<MoodTileVm> Tiles { get; init; } = Array.Empty<MoodTileVm>();

    protected override IReadOnlyList<MoodTileVm> All => Tiles;
}

/// <summary>Coloured mood and genre tiles filling the page's width.</summary>
public sealed class TileGridVm : ShelfVm
{
    public IReadOnlyList<MoodTileVm> Tiles { get; init; } = Array.Empty<MoodTileVm>();
}

public sealed class MoodTileVm
{
    private static readonly Color Sheet = Color.FromRgb(0x0B, 0x0C, 0x11);
    private IBrush? _background;

    public MoodTileVm(MoodTile tile) => Tile = tile;

    public MoodTile Tile { get; }
    public string Name => Tile.Name;

    /// <summary>YouTube's colour, deepened so white text always reads on it.</summary>
    public IBrush Background => _background ??= Wash(Tile.Color);

    private static IBrush Wash(uint? argb)
    {
        var color = Color.FromUInt32(argb ?? 0xFF8A8FA3);
        return new LinearGradientBrush
        {
            StartPoint = RelativePoint.TopLeft,
            EndPoint = RelativePoint.BottomRight,
            GradientStops =
            {
                new GradientStop(Mix(color, 0.68), 0),
                new GradientStop(Mix(color, 0.24), 1)
            }
        };
    }

    private static Color Mix(Color color, double amount) => Color.FromRgb(
        (byte)(Sheet.R + (color.R - Sheet.R) * amount),
        (byte)(Sheet.G + (color.G - Sheet.G) * amount),
        (byte)(Sheet.B + (color.B - Sheet.B) * amount));
}

/// <summary>One moment of the big block at the top of Home.</summary>
public sealed class SpotlightEntry
{
    public SpotlightEntry(string eyebrow, ShelfCard card)
    {
        Eyebrow = eyebrow;
        Card = card;
    }

    public string Eyebrow { get; }
    public ShelfCard Card { get; }
}

public sealed partial class HomeChipVm : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public HomeChipVm(HomeChip chip) => Chip = chip;

    public HomeChip Chip { get; }
    public string Text => Chip.Text;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _isSelected;
}

/// <summary>Shelf entries from what other parts of the catalogue answer with.</summary>
public static class ShelfItems
{
    public static ShelfItem Album(AlbumItem album) => new()
    {
        Kind = album.Id.StartsWith("MPRE", StringComparison.Ordinal) ? ShelfItemKind.Album : ShelfItemKind.Playlist,
        Id = album.Id,
        Title = album.Title,
        Subtitle = album.Subtitle ?? album.Year ?? album.ArtistDisplay,
        ThumbnailUrl = album.ThumbnailUrl
    };

    public static ShelfItem Playlist(PlaylistItem playlist) => new()
    {
        Kind = ShelfItemKind.Playlist,
        Id = playlist.Id,
        Title = playlist.Title,
        Subtitle = playlist.Subtitle ?? playlist.Author ?? string.Empty,
        ThumbnailUrl = playlist.ThumbnailUrl
    };

    public static ShelfItem Artist(ArtistItem artist) => new()
    {
        Kind = ShelfItemKind.Artist,
        Id = artist.Id,
        Title = artist.Name,
        Subtitle = artist.Subtitle ?? string.Empty,
        ThumbnailUrl = artist.ThumbnailUrl
    };

    public static ShelfItem Video(SongItem video) => new()
    {
        Kind = ShelfItemKind.Video,
        Id = video.Id,
        Title = video.Title,
        Subtitle = video.ViewCount > 0 ? video.ViewCountText : video.ArtistDisplay,
        ThumbnailUrl = video.ThumbnailUrl,
        Song = video
    };
}

/// <summary>The country the charts are read for.</summary>
public static class ListenerCountry
{
    public static (string Code, string? Name) Current
    {
        get
        {
            try
            {
                var region = RegionInfo.CurrentRegion;
                return region.TwoLetterISORegionName.Length == 2
                    ? (region.TwoLetterISORegionName, region.EnglishName)
                    : ("ZZ", null);
            }
            catch (ArgumentException)
            {
                return ("ZZ", null);
            }
        }
    }
}
