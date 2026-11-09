using System.Text.Json.Serialization;

namespace SoundLibert.Infra.InnerTube.Models;

public class SearchResponse
{
    [JsonPropertyName("contents")]
    public SearchResponseContents? Contents { get; set; }

    [JsonPropertyName("continuationContents")]
    public ContinuationContents? ContinuationContents { get; set; }
}

public class SearchResponseContents
{
    [JsonPropertyName("tabbedSearchResultsRenderer")]
    public TabbedSearchResultsRenderer? TabbedSearchResultsRenderer { get; set; }
}

public class TabbedSearchResultsRenderer
{
    [JsonPropertyName("tabs")]
    public List<TabRenderer>? Tabs { get; set; }
}

public class TabRenderer
{
    [JsonPropertyName("tabRenderer")]
    public TabRendererInner? TabRendererInner { get; set; }
}

public class TabRendererInner
{
    [JsonPropertyName("content")]
    public SectionListRendererContent? Content { get; set; }
}

public class SectionListRendererContent
{
    [JsonPropertyName("sectionListRenderer")]
    public SectionListRenderer? SectionListRenderer { get; set; }
}

public class SectionListRenderer
{
    [JsonPropertyName("contents")]
    public List<SectionContent>? Contents { get; set; }

    [JsonPropertyName("continuations")]
    public List<Continuation>? Continuations { get; set; }

    [JsonPropertyName("header")]
    public SectionListHeader? Header { get; set; }
}

public class SectionListHeader
{
    [JsonPropertyName("chipCloudRenderer")]
    public ChipCloudRenderer? ChipCloudRenderer { get; set; }
}

public class ChipCloudRenderer
{
    [JsonPropertyName("chips")]
    public List<ChipCloudChip>? Chips { get; set; }
}

public class ChipCloudChip
{
    [JsonPropertyName("chipCloudChipRenderer")]
    public ChipCloudChipRenderer? ChipCloudChipRenderer { get; set; }
}

public class ChipCloudChipRenderer
{
    [JsonPropertyName("text")]
    public RunsText? Text { get; set; }

    [JsonPropertyName("navigationEndpoint")]
    public NavigationEndpoint? NavigationEndpoint { get; set; }
}

public class SectionContent
{
    [JsonPropertyName("musicShelfRenderer")]
    public MusicShelfRenderer? MusicShelfRenderer { get; set; }

    [JsonPropertyName("musicCarouselShelfRenderer")]
    public MusicCarouselShelfRenderer? MusicCarouselShelfRenderer { get; set; }

    [JsonPropertyName("musicCardShelfRenderer")]
    public MusicCardShelfRenderer? MusicCardShelfRenderer { get; set; }

    [JsonPropertyName("musicResponsiveHeaderRenderer")]
    public MusicResponsiveHeaderRenderer? MusicResponsiveHeaderRenderer { get; set; }

    [JsonPropertyName("musicPlaylistShelfRenderer")]
    public MusicPlaylistShelfRenderer? MusicPlaylistShelfRenderer { get; set; }

    [JsonPropertyName("gridRenderer")]
    public GridRenderer? GridRenderer { get; set; }

    [JsonPropertyName("musicDescriptionShelfRenderer")]
    public MusicDescriptionShelfRenderer? MusicDescriptionShelfRenderer { get; set; }

    // The library can wrap its list in an item section (next to a message).
    [JsonPropertyName("itemSectionRenderer")]
    public ItemSectionRenderer? ItemSectionRenderer { get; set; }
}

public class ItemSectionRenderer
{
    [JsonPropertyName("contents")]
    public List<SectionContent>? Contents { get; set; }
}

public class MusicDescriptionShelfRenderer
{
    [JsonPropertyName("description")]
    public RunsText? Description { get; set; }

    /// <summary>On an artist page: "23,688,076,706 views".</summary>
    [JsonPropertyName("subheader")]
    public RunsText? Subheader { get; set; }

    [JsonPropertyName("text")]
    public RunsText? Text { get; set; }
}

public class MusicPlaylistShelfRenderer
{
    [JsonPropertyName("contents")]
    public List<MusicShelfContent>? Contents { get; set; }

    [JsonPropertyName("continuations")]
    public List<Continuation>? Continuations { get; set; }
}

public class GridRenderer
{
    [JsonPropertyName("items")]
    public List<GridItem>? Items { get; set; }

    [JsonPropertyName("continuations")]
    public List<Continuation>? Continuations { get; set; }

    [JsonPropertyName("header")]
    public GridHeader? Header { get; set; }
}

public class GridItem
{
    [JsonPropertyName("musicTwoRowItemRenderer")]
    public MusicTwoRowItemRenderer? MusicTwoRowItemRenderer { get; set; }

    [JsonPropertyName("musicNavigationButtonRenderer")]
    public MusicNavigationButtonRenderer? MusicNavigationButtonRenderer { get; set; }

    [JsonPropertyName("musicResponsiveListItemRenderer")]
    public MusicResponsiveListItemRenderer? MusicResponsiveListItemRenderer { get; set; }

    // Newer answers put the next-page token here instead of "continuations".
    [JsonPropertyName("continuationItemRenderer")]
    public ContinuationItemRenderer? ContinuationItemRenderer { get; set; }
}

/// <summary>A mood or genre button.</summary>
public class MusicNavigationButtonRenderer
{
    [JsonPropertyName("buttonText")]
    public RunsText? ButtonText { get; set; }

    [JsonPropertyName("solid")]
    public NavigationButtonColor? Solid { get; set; }

    [JsonPropertyName("clickCommand")]
    public NavigationEndpoint? ClickCommand { get; set; }
}

public class NavigationButtonColor
{
    [JsonPropertyName("leftStripeColor")]
    public long? LeftStripeColor { get; set; }
}

public class GridHeader
{
    [JsonPropertyName("gridHeaderRenderer")]
    public GridHeaderRenderer? GridHeaderRenderer { get; set; }
}

public class GridHeaderRenderer
{
    [JsonPropertyName("title")]
    public RunsText? Title { get; set; }
}

public class MusicShelfRenderer
{
    [JsonPropertyName("contents")]
    public List<MusicShelfContent>? Contents { get; set; }

    [JsonPropertyName("continuations")]
    public List<Continuation>? Continuations { get; set; }

    [JsonPropertyName("title")]
    public RunsText? Title { get; set; }

    /// <summary>The shelf's "Show all".</summary>
    [JsonPropertyName("bottomEndpoint")]
    public NavigationEndpoint? BottomEndpoint { get; set; }
}

public class MusicShelfContent
{
    [JsonPropertyName("musicResponsiveListItemRenderer")]
    public MusicResponsiveListItemRenderer? MusicResponsiveListItemRenderer { get; set; }

    // Newer answers put the next-page token here instead of "continuations".
    [JsonPropertyName("continuationItemRenderer")]
    public ContinuationItemRenderer? ContinuationItemRenderer { get; set; }
}

public class MusicCarouselShelfRenderer
{
    [JsonPropertyName("contents")]
    public List<CarouselContent>? Contents { get; set; }

    [JsonPropertyName("header")]
    public CarouselHeader? Header { get; set; }
}

public class MusicCardShelfRenderer
{
    [JsonPropertyName("title")]
    public RunsText? Title { get; set; }

    [JsonPropertyName("subtitle")]
    public RunsText? Subtitle { get; set; }

    [JsonPropertyName("thumbnail")]
    public ThumbnailRenderer? Thumbnail { get; set; }

    [JsonPropertyName("onTap")]
    public NavigationEndpoint? OnTap { get; set; }

    [JsonPropertyName("contents")]
    public List<MusicShelfContent>? Contents { get; set; }

    [JsonPropertyName("header")]
    public CardShelfHeader? Header { get; set; }
}

public class CardShelfHeader
{
    [JsonPropertyName("musicCardShelfHeaderBasicRenderer")]
    public MusicCardShelfHeaderBasicRenderer? MusicCardShelfHeaderBasicRenderer { get; set; }
}

public class MusicCardShelfHeaderBasicRenderer
{
    [JsonPropertyName("title")]
    public RunsText? Title { get; set; }
}

public class CarouselContent
{
    [JsonPropertyName("musicResponsiveListItemRenderer")]
    public MusicResponsiveListItemRenderer? MusicResponsiveListItemRenderer { get; set; }

    [JsonPropertyName("musicTwoRowItemRenderer")]
    public MusicTwoRowItemRenderer? MusicTwoRowItemRenderer { get; set; }
}

public class CarouselHeader
{
    [JsonPropertyName("musicCarouselShelfBasicHeaderRenderer")]
    public MusicCarouselShelfBasicHeaderRenderer? MusicCarouselShelfBasicHeaderRenderer { get; set; }
}

public class MusicCarouselShelfBasicHeaderRenderer
{
    [JsonPropertyName("title")]
    public RunsText? Title { get; set; }

    /// <summary>
    /// The "More" button of a carousel. Its browse endpoint (id + params)
    /// returns the FULL shelf — the initial carousel only carries ~10 items.
    /// </summary>
    [JsonPropertyName("moreContentButton")]
    public PlayButton? MoreContentButton { get; set; }
}

public class MusicResponsiveListItemRenderer
{
    [JsonPropertyName("flexColumns")]
    public List<FlexColumn>? FlexColumns { get; set; }

    [JsonPropertyName("fixedColumns")]
    public List<FixedColumn>? FixedColumns { get; set; }

    [JsonPropertyName("thumbnail")]
    public ThumbnailRenderer? Thumbnail { get; set; }

    [JsonPropertyName("overlay")]
    public OverlayRenderer? Overlay { get; set; }

    [JsonPropertyName("navigationEndpoint")]
    public NavigationEndpoint? NavigationEndpoint { get; set; }

    [JsonPropertyName("playlistItemData")]
    public PlaylistItemData? PlaylistItemData { get; set; }

    [JsonPropertyName("badges")]
    public List<BadgeRenderer>? Badges { get; set; }

    /// <summary>The place on a chart and where it is heading.</summary>
    [JsonPropertyName("customIndexColumn")]
    public CustomIndexColumn? CustomIndexColumn { get; set; }
}

public class CustomIndexColumn
{
    [JsonPropertyName("musicCustomIndexColumnRenderer")]
    public MusicCustomIndexColumnRenderer? MusicCustomIndexColumnRenderer { get; set; }
}

public class MusicCustomIndexColumnRenderer
{
    [JsonPropertyName("text")]
    public RunsText? Text { get; set; }

    [JsonPropertyName("icon")]
    public IconRenderer? Icon { get; set; }
}

public class MusicTwoRowItemRenderer
{
    [JsonPropertyName("title")]
    public RunsText? Title { get; set; }

    [JsonPropertyName("subtitle")]
    public RunsText? Subtitle { get; set; }

    [JsonPropertyName("thumbnailRenderer")]
    public ThumbnailRenderer? ThumbnailRenderer { get; set; }

    [JsonPropertyName("navigationEndpoint")]
    public NavigationEndpoint? NavigationEndpoint { get; set; }

    [JsonPropertyName("thumbnailOverlay")]
    public ThumbnailOverlay? ThumbnailOverlay { get; set; }

    [JsonPropertyName("subtitleBadges")]
    public List<BadgeRenderer>? SubtitleBadges { get; set; }
}

public class FlexColumn
{
    [JsonPropertyName("musicResponsiveListItemFlexColumnRenderer")]
    public MusicResponsiveListItemFlexColumnRenderer? MusicResponsiveListItemFlexColumnRenderer { get; set; }
}

public class MusicResponsiveListItemFlexColumnRenderer
{
    [JsonPropertyName("text")]
    public RunsText? Text { get; set; }
}

public class FixedColumn
{
    [JsonPropertyName("musicResponsiveListItemFixedColumnRenderer")]
    public MusicResponsiveListItemFixedColumnRenderer? MusicResponsiveListItemFixedColumnRenderer { get; set; }
}

public class MusicResponsiveListItemFixedColumnRenderer
{
    [JsonPropertyName("text")]
    public RunsText? Text { get; set; }
}

public class RunsText
{
    [JsonPropertyName("runs")]
    public List<Run>? Runs { get; set; }

    [JsonPropertyName("simpleText")]
    public string? SimpleText { get; set; }
}

public class Run
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("navigationEndpoint")]
    public NavigationEndpoint? NavigationEndpoint { get; set; }
}

public class ThumbnailRenderer
{
    [JsonPropertyName("musicThumbnailRenderer")]
    public MusicThumbnailRenderer? MusicThumbnailRenderer { get; set; }
}

public class MusicThumbnailRenderer
{
    [JsonPropertyName("thumbnail")]
    public Thumbnail? Thumbnail { get; set; }

    public string? GetThumbnailUrl() =>
        Thumbnail?.Thumbnails?.LastOrDefault()?.Url;
}

public class Thumbnail
{
    [JsonPropertyName("thumbnails")]
    public List<ThumbnailImage>? Thumbnails { get; set; }
}

public class ThumbnailImage
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}

public class OverlayRenderer
{
    [JsonPropertyName("musicItemThumbnailOverlayRenderer")]
    public MusicItemThumbnailOverlayRenderer? MusicItemThumbnailOverlayRenderer { get; set; }
}

public class MusicItemThumbnailOverlayRenderer
{
    [JsonPropertyName("content")]
    public PlayButtonRenderer? Content { get; set; }
}

public class PlayButtonRenderer
{
    [JsonPropertyName("musicPlayButtonRenderer")]
    public MusicPlayButtonRenderer? MusicPlayButtonRenderer { get; set; }
}

public class MusicPlayButtonRenderer
{
    [JsonPropertyName("playNavigationEndpoint")]
    public NavigationEndpoint? PlayNavigationEndpoint { get; set; }
}

public class ThumbnailOverlay
{
    [JsonPropertyName("musicItemThumbnailOverlayRenderer")]
    public MusicItemThumbnailOverlayRenderer? MusicItemThumbnailOverlayRenderer { get; set; }
}

public class NavigationEndpoint
{
    [JsonPropertyName("watchEndpoint")]
    public WatchEndpoint? WatchEndpoint { get; set; }

    [JsonPropertyName("browseEndpoint")]
    public BrowseEndpoint? BrowseEndpoint { get; set; }

    [JsonPropertyName("watchPlaylistEndpoint")]
    public WatchPlaylistEndpoint? WatchPlaylistEndpoint { get; set; }

    [JsonPropertyName("continuationCommand")]
    public ContinuationCommand? ContinuationCommand { get; set; }
}

public class ContinuationCommand
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }
}

public class WatchEndpoint
{
    [JsonPropertyName("videoId")]
    public string? VideoId { get; set; }

    [JsonPropertyName("playlistId")]
    public string? PlaylistId { get; set; }

    [JsonPropertyName("playlistSetVideoId")]
    public string? PlaylistSetVideoId { get; set; }

    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("params")]
    public string? Params { get; set; }

    [JsonPropertyName("watchEndpointMusicSupportedConfigs")]
    public WatchEndpointMusicSupportedConfigs? WatchEndpointMusicSupportedConfigs { get; set; }
}

public class BrowseEndpoint
{
    [JsonPropertyName("browseId")]
    public string? BrowseId { get; set; }

    [JsonPropertyName("params")]
    public string? Params { get; set; }
}

public class WatchPlaylistEndpoint
{
    [JsonPropertyName("playlistId")]
    public string? PlaylistId { get; set; }
}

public class PlaylistItemData
{
    [JsonPropertyName("videoId")]
    public string? VideoId { get; set; }
}

public class BadgeRenderer
{
    [JsonPropertyName("musicInlineBadgeRenderer")]
    public MusicInlineBadgeRenderer? MusicInlineBadgeRenderer { get; set; }
}

public class MusicInlineBadgeRenderer
{
    [JsonPropertyName("icon")]
    public IconRenderer? Icon { get; set; }
}

public class IconRenderer
{
    [JsonPropertyName("iconType")]
    public string? IconType { get; set; }
}

public class Continuation
{
    [JsonPropertyName("continuation")]
    public string? Token { get; set; }

    // YouTube returns shelf continuations in two shapes: a flat
    // {"continuation": "..."} and the older {"nextContinuationData":{"continuation":"..."}}.
    [JsonPropertyName("nextContinuationData")]
    public ContinuationData? NextContinuationData { get; set; }

    public string? AnyToken => Token ?? NextContinuationData?.Continuation;
}

public class ContinuationContents
{
    [JsonPropertyName("musicShelfContinuation")]
    public MusicShelfContinuation? MusicShelfContinuation { get; set; }

    [JsonPropertyName("musicPlaylistShelfContinuation")]
    public MusicShelfContinuation? MusicPlaylistShelfContinuation { get; set; }

    [JsonPropertyName("sectionListContinuation")]
    public SectionListContinuation? SectionListContinuation { get; set; }

    [JsonPropertyName("gridContinuation")]
    public GridContinuation? GridContinuation { get; set; }
}

public class GridContinuation
{
    [JsonPropertyName("items")]
    public List<GridItem>? Items { get; set; }

    [JsonPropertyName("continuations")]
    public List<Continuation>? Continuations { get; set; }
}

public class MusicShelfContinuation
{
    [JsonPropertyName("contents")]
    public List<MusicShelfContent>? Contents { get; set; }

    [JsonPropertyName("continuations")]
    public List<Continuation>? Continuations { get; set; }
}

public class SectionListContinuation
{
    [JsonPropertyName("contents")]
    public List<SectionContent>? Contents { get; set; }

    [JsonPropertyName("continuations")]
    public List<Continuation>? Continuations { get; set; }
}

public class GetSearchSuggestionsResponse
{
    [JsonPropertyName("contents")]
    public List<SuggestionsSection>? Contents { get; set; }
}

public class SuggestionsSection
{
    [JsonPropertyName("searchSuggestionsSectionRenderer")]
    public SearchSuggestionsSectionRenderer? SearchSuggestionsSectionRenderer { get; set; }
}

public class SearchSuggestionsSectionRenderer
{
    [JsonPropertyName("contents")]
    public List<SuggestionContent>? Contents { get; set; }
}

public class SuggestionContent
{
    [JsonPropertyName("searchSuggestionRenderer")]
    public SearchSuggestionRenderer? SearchSuggestionRenderer { get; set; }

    [JsonPropertyName("musicResponsiveListItemRenderer")]
    public MusicResponsiveListItemRenderer? MusicResponsiveListItemRenderer { get; set; }
}

public class SearchSuggestionRenderer
{
    [JsonPropertyName("suggestion")]
    public RunsText? Suggestion { get; set; }
}
