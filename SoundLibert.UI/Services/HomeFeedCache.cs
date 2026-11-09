using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SoundLibert.Domain.Models;
using SoundLibert.UI.ViewModels;

namespace SoundLibert.UI.Services;

/// <summary>
/// The last network-built Home feed on disk, so a launch shows the recommendations at
/// once and swaps in fresh ones when the network answers — instead of an empty page for
/// several seconds. Plain DTOs only: view-model types are rebuilt on load.
/// </summary>
public static class HomeFeedCache
{
    private const int Version = 2;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoundLibert", "cache", "home-feed.json");

    public sealed record Snapshot(
        string? Scope,
        IReadOnlyList<ShelfVm> Shelves,
        IReadOnlyList<SpotlightEntry> Spotlight,
        IReadOnlyList<HomeChip> Chips);

    public static async Task SaveAsync(Snapshot snapshot)
    {
        try
        {
            var dto = new FeedDto
            {
                Version = Version,
                Scope = snapshot.Scope,
                SavedAt = DateTime.UtcNow,
                Shelves = snapshot.Shelves.Select(ShelfDto.From).Where(s => s != null).Cast<ShelfDto>().ToList(),
                Spotlight = snapshot.Spotlight.Select(s => new SpotlightDto { Eyebrow = s.Eyebrow, Item = ItemDto.From(s.Card.Item) }).ToList(),
                Chips = snapshot.Chips.Select(c => new ChipDto { Text = c.Text, Params = c.Params }).ToList()
            };
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(dto));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Home] feed cache save failed: {ex.Message}");
        }
    }

    /// <summary>The cached feed for this account scope, or null (none / other scope / unreadable).</summary>
    public static async Task<Snapshot?> TryLoadAsync(string? scope)
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var dto = JsonSerializer.Deserialize<FeedDto>(await File.ReadAllTextAsync(FilePath));
            if (dto is not { Version: Version } || !string.Equals(dto.Scope, scope, StringComparison.Ordinal))
                return null;
            return new Snapshot(
                dto.Scope,
                (dto.Shelves ?? new()).Select(s => s.ToVm()).ToList(),
                (dto.Spotlight ?? new()).Select(s => new SpotlightEntry(s.Eyebrow, new ShelfCard(s.Item.ToItem()))).ToList(),
                (dto.Chips ?? new()).Select(c => new HomeChip { Text = c.Text, Params = c.Params }).ToList());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Home] feed cache load failed: {ex.Message}");
            return null;
        }
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }

    // ---- DTOs ----

    private sealed class FeedDto
    {
        public int Version { get; set; }
        public string? Scope { get; set; }
        public DateTime SavedAt { get; set; }
        public List<ShelfDto>? Shelves { get; set; }
        public List<SpotlightDto>? Spotlight { get; set; }
        public List<ChipDto>? Chips { get; set; }
    }

    private sealed class SpotlightDto
    {
        public string Eyebrow { get; set; } = string.Empty;
        public ItemDto Item { get; set; } = new();
    }

    private sealed class ChipDto
    {
        public string Text { get; set; } = string.Empty;
        public string? Params { get; set; }
    }

    private sealed class ShelfDto
    {
        public string Kind { get; set; } = string.Empty;
        public string? Eyebrow { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? MoreBrowseId { get; set; }
        public string? MoreParams { get; set; }
        public string? FeedKey { get; set; }
        public List<ItemDto> Items { get; set; } = new();
        public List<TileDto> Tiles { get; set; } = new();

        public static ShelfDto? From(ShelfVm shelf)
        {
            var dto = new ShelfDto
            {
                Eyebrow = shelf.Eyebrow,
                Title = shelf.Title,
                MoreBrowseId = shelf.MoreBrowseId,
                MoreParams = shelf.MoreParams,
                FeedKey = shelf.FeedKey
            };
            switch (shelf)
            {
                case CardShelfVm cards:
                    dto.Kind = "cards";
                    dto.Items = cards.Items.Select(c => ItemDto.From(c.Item)).ToList();
                    return dto;
                case VideoShelfVm videos:
                    dto.Kind = "videos";
                    dto.Items = videos.Items.Select(c => ItemDto.From(c.Item)).ToList();
                    return dto;
                case RowShelfVm rows:
                    dto.Kind = "rows";
                    dto.Items = rows.Items.Select(c => ItemDto.From(c.Item)).ToList();
                    return dto;
                case TileRailVm tiles:
                    dto.Kind = "tiles";
                    dto.Tiles = tiles.Tiles.Select(t => TileDto.From(t.Tile)).ToList();
                    return dto;
                default:
                    return null;
            }
        }

        public ShelfVm ToVm()
        {
            var cards = Items.Select(i => new ShelfCard(i.ToItem())).ToList();
            return Kind switch
            {
                "videos" => new VideoShelfVm(cards) { Eyebrow = Eyebrow, Title = Title, MoreBrowseId = MoreBrowseId, MoreParams = MoreParams, FeedKey = FeedKey },
                "rows" => new RowShelfVm(cards) { Eyebrow = Eyebrow, Title = Title, MoreBrowseId = MoreBrowseId, MoreParams = MoreParams, FeedKey = FeedKey },
                "tiles" => new TileRailVm { Eyebrow = Eyebrow, Title = Title, Tiles = Tiles.Select(t => new MoodTileVm(t.ToTile())).ToList() },
                _ => new CardShelfVm(cards) { Eyebrow = Eyebrow, Title = Title, MoreBrowseId = MoreBrowseId, MoreParams = MoreParams, FeedKey = FeedKey }
            };
        }
    }

    private sealed class TileDto
    {
        public string Name { get; set; } = string.Empty;
        public string BrowseId { get; set; } = string.Empty;
        public string? Params { get; set; }
        public uint? Color { get; set; }

        public static TileDto From(MoodTile tile) => new()
        {
            Name = tile.Name,
            BrowseId = tile.BrowseId,
            Params = tile.Params,
            Color = tile.Color
        };

        public MoodTile ToTile() => new() { Name = Name, BrowseId = BrowseId, Params = Params, Color = Color };
    }

    private sealed class ItemDto
    {
        public ShelfItemKind Kind { get; set; }
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string ThumbnailUrl { get; set; } = string.Empty;
        public int? Rank { get; set; }
        public ChartTrend Trend { get; set; }
        public SongDto? Song { get; set; }

        public static ItemDto From(ShelfItem item) => new()
        {
            Kind = item.Kind,
            Id = item.Id,
            Title = item.Title,
            Subtitle = item.Subtitle,
            ThumbnailUrl = item.ThumbnailUrl,
            Rank = item.Rank,
            Trend = item.Trend,
            Song = item.Song is { } s ? SongDto.From(s) : null
        };

        public ShelfItem ToItem() => new()
        {
            Kind = Kind,
            Id = Id,
            Title = Title,
            Subtitle = Subtitle,
            ThumbnailUrl = ThumbnailUrl,
            Rank = Rank,
            Trend = Trend,
            Song = Song?.ToSong()
        };
    }

    private sealed class SongDto
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public List<string> ArtistNames { get; set; } = new();
        public List<string?> ArtistIds { get; set; } = new();
        public string? AlbumTitle { get; set; }
        public string? AlbumId { get; set; }
        public int DurationSeconds { get; set; }
        public string ThumbnailUrl { get; set; } = string.Empty;
        public string? VideoType { get; set; }
        public string? VideoVersionId { get; set; }
        public string? SongVersionId { get; set; }
        public long ViewCount { get; set; }
        public bool IsExplicit { get; set; }

        public static SongDto From(SongItem s) => new()
        {
            Id = s.Id,
            Title = s.Title,
            ArtistNames = s.Artists.Select(a => a.Name).ToList(),
            ArtistIds = s.Artists.Select(a => a.Id).ToList(),
            AlbumTitle = s.AlbumTitle,
            AlbumId = s.AlbumId,
            DurationSeconds = s.DurationSeconds,
            ThumbnailUrl = s.ThumbnailUrl,
            VideoType = s.VideoType,
            VideoVersionId = s.VideoVersionId,
            SongVersionId = s.SongVersionId,
            ViewCount = s.ViewCount,
            IsExplicit = s.IsExplicit
        };

        public SongItem ToSong() => new()
        {
            Id = Id,
            Title = Title,
            Artists = ArtistNames.Select((name, i) => new Artist
            {
                Name = name,
                Id = i < ArtistIds.Count ? ArtistIds[i] : null
            }).ToList(),
            AlbumTitle = AlbumTitle,
            AlbumId = AlbumId,
            DurationSeconds = DurationSeconds,
            ThumbnailUrl = ThumbnailUrl,
            VideoType = VideoType,
            VideoVersionId = VideoVersionId,
            SongVersionId = SongVersionId,
            ViewCount = ViewCount,
            IsExplicit = IsExplicit
        };
    }
}
