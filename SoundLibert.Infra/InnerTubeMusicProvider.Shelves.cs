using SoundLibert.Domain.Enums;
using SoundLibert.Domain.Models;
using SoundLibert.Infra.InnerTube;
using SoundLibert.Infra.InnerTube.Models;

namespace SoundLibert.Infra;

public partial class InnerTubeMusicProvider
{
    // ---- Pages ----

    /// <inheritdoc cref="Domain.Interfaces.IMusicProvider.GetHomePageAsync" />
    public async Task<HomePage> GetHomePageAsync(string? chipParams = null, CancellationToken ct = default)
    {
        // The visitor id is what makes the feed local ("Ukrainian hits"); without
        // one YouTube only has a generic page of two shelves.
        await _client.EnsureVisitorDataAsync(ct);
        var response = await _client.BrowseAsync(
            YouTubeClient.WEB_REMIX, "FEmusic_home", chipParams, setLogin: true, ct: ct);

        var list = response.Contents?.SingleColumnBrowseResultsRenderer?.Tabs?.FirstOrDefault()
            ?.TabRenderer?.Content?.SectionListRenderer;

        return new HomePage
        {
            Shelves = ReadShelves(list?.Contents),
            Chips = (list?.Header?.ChipCloudRenderer?.Chips ?? new List<ChipCloudChip>())
                .Select(c => c.ChipCloudChipRenderer)
                .Where(c => !string.IsNullOrWhiteSpace(c?.Text?.FullText()))
                .Select(c => new HomeChip
                {
                    Text = c!.Text!.FullText(),
                    Params = c.NavigationEndpoint?.BrowseEndpoint?.Params
                })
                .ToList(),
            ContinuationToken = list?.Continuations?.FirstOrDefault()?.AnyToken
        };
    }

    /// <inheritdoc cref="Domain.Interfaces.IMusicProvider.GetHomeContinuationAsync" />
    public async Task<HomePage> GetHomeContinuationAsync(string token, CancellationToken ct = default)
    {
        var response = await _client.BrowseAsync(
            YouTubeClient.WEB_REMIX, continuation: token, setLogin: true, ct: ct);
        var next = response.ContinuationContents?.SectionListContinuation;
        return new HomePage
        {
            Shelves = ReadShelves(next?.Contents),
            ContinuationToken = next?.Continuations?.FirstOrDefault()?.AnyToken
        };
    }

    /// <inheritdoc cref="Domain.Interfaces.IMusicProvider.GetExplorePageAsync" />
    public async Task<ExplorePage> GetExplorePageAsync(CancellationToken ct = default)
    {
        await _client.EnsureVisitorDataAsync(ct);
        var response = await _client.BrowseAsync(
            YouTubeClient.WEB_REMIX, "FEmusic_explore", setLogin: true, ct: ct);
        var shelves = ReadShelves(GetBrowseContents(response));

        Shelf? Find(string word) => shelves.FirstOrDefault(s => s.Title.Contains(word, StringComparison.OrdinalIgnoreCase));
        return new ExplorePage
        {
            NewReleases = Find("album") ?? Find("new release"),
            Trending = Find("trending"),
            NewVideos = Find("video")
        };
    }

    /// <inheritdoc cref="Domain.Interfaces.IMusicProvider.GetMoodsAndGenresAsync" />
    public async Task<IReadOnlyList<MoodCategory>> GetMoodsAndGenresAsync(CancellationToken ct = default)
    {
        var response = await _client.BrowseAsync(
            YouTubeClient.WEB_REMIX, "FEmusic_moods_and_genres", ct: ct);

        var categories = new List<MoodCategory>();
        foreach (var content in GetBrowseContents(response) ?? new List<SectionContent>())
        {
            if (content.GridRenderer is not { Items: { } items } grid) continue;

            var tiles = new List<MoodTile>();
            foreach (var item in items)
            {
                var button = item.MusicNavigationButtonRenderer;
                var endpoint = button?.ClickCommand?.BrowseEndpoint;
                var name = button?.ButtonText?.FullText();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(endpoint?.BrowseId)) continue;
                tiles.Add(new MoodTile
                {
                    Name = name,
                    BrowseId = endpoint.BrowseId,
                    Params = endpoint.Params,
                    Color = button?.Solid?.LeftStripeColor is { } color ? (uint)color : null
                });
            }
            if (tiles.Count > 0)
                categories.Add(new MoodCategory
                {
                    Title = grid.Header?.GridHeaderRenderer?.Title?.FullText() ?? string.Empty,
                    Tiles = tiles
                });
        }
        return categories;
    }

    /// <inheritdoc cref="Domain.Interfaces.IMusicProvider.GetShelfPageAsync" />
    public async Task<ShelfPage> GetShelfPageAsync(string browseId, string? @params = null, CancellationToken ct = default)
    {
        await _client.EnsureVisitorDataAsync(ct);
        var response = await _client.BrowseAsync(
            YouTubeClient.WEB_REMIX, browseId, @params, setLogin: true, ct: ct);
        return new ShelfPage
        {
            Title = response.Header?.MusicHeaderRenderer?.Title?.FullText() ?? string.Empty,
            Shelves = ReadShelves(GetBrowseContents(response))
        };
    }

    /// <inheritdoc cref="Domain.Interfaces.IMusicProvider.GetCollectionExtrasAsync" />
    public async Task<CollectionExtras> GetCollectionExtrasAsync(string browseId, CancellationToken ct = default)
    {
        await _client.EnsureVisitorDataAsync(ct);
        // A playlist's page answers only under its VL id.
        var page = browseId.StartsWith("MPRE", StringComparison.Ordinal) ? browseId : WithVl(browseId);
        var response = await _client.BrowseAsync(YouTubeClient.WEB_REMIX, page, setLogin: true, ct: ct);
        var list = response.Contents?.TwoColumnBrowseResultsRenderer?.SecondaryContents?.SectionListRenderer;

        // The tracks stand on a shelf of their own: only the carousels are extra.
        var shelves = ReadShelves(list?.Contents?.Where(c => c.MusicCarouselShelfRenderer != null).ToList()).ToList();

        // A playlist's related ones are the next part of its page.
        if (list?.Continuations?.FirstOrDefault()?.AnyToken is { Length: > 0 } token)
        {
            var next = await _client.BrowseAsync(YouTubeClient.WEB_REMIX, continuation: token, setLogin: true, ct: ct);
            shelves.AddRange(ReadShelves(next.ContinuationContents?.SectionListContinuation?.Contents));
        }

        return new CollectionExtras
        {
            Shelves = shelves,
            Artist = GetBrowseHeader(response)?.StraplineTextOne.ExtractArtists().FirstOrDefault()
        };
    }

    /// <inheritdoc cref="Domain.Interfaces.IMusicProvider.GetChartsAsync" />
    public async Task<IReadOnlyList<Shelf>> GetChartsAsync(string country, CancellationToken ct = default)
    {
        var response = await _client.BrowseAsync(
            YouTubeClient.WEB_REMIX, "FEmusic_charts", country: country, ct: ct);
        return ReadShelves(GetBrowseContents(response));
    }

    /// <inheritdoc cref="Domain.Interfaces.IMusicProvider.GetSongRelatedAsync" />
    public async Task<SongRelated> GetSongRelatedAsync(string videoId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoId)) return new SongRelated();

        await _client.EnsureVisitorDataAsync(ct);
        var next = await _client.NextAsync(YouTubeClient.WEB_REMIX, videoId, ct: ct);
        var related = next.Contents?.SingleColumn?.TabbedRenderer?.WatchNext?.Tabs
            ?.Select(t => t.TabRenderer)
            .FirstOrDefault(t => string.Equals(t?.Title, "Related", StringComparison.OrdinalIgnoreCase))
            ?.Endpoint?.BrowseEndpoint?.BrowseId;
        if (string.IsNullOrWhiteSpace(related)) return new SongRelated();

        var response = await _client.BrowseAsync(YouTubeClient.WEB_REMIX, related, ct: ct);
        var contents = GetBrowseContents(response);
        return new SongRelated
        {
            Shelves = ReadShelves(contents),
            About = contents?.Select(c => c.MusicDescriptionShelfRenderer).FirstOrDefault(d => d != null)
                is { } about ? ReadableStory(about.Description?.FullText() ?? about.Text?.FullText()) : null
        };
    }

    /// <summary>Some artists leave only a link for a story. A bare link says nothing, so those lines go.</summary>
    private static string? ReadableStory(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        static bool IsBareLink(string line) =>
            !line.Contains(' ', StringComparison.Ordinal)
            && (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("www.", StringComparison.OrdinalIgnoreCase));

        var story = string.Join('\n', text.Split('\n').Where(line => !IsBareLink(line.Trim()))).Trim();
        return story.Length == 0 ? null : story;
    }

    /// <inheritdoc cref="Domain.Interfaces.IMusicProvider.GetSuggestionsAsync" />
    public async Task<SearchSuggestions> GetSuggestionsAsync(string query, CancellationToken ct = default)
    {
        var response = await _client.GetSearchSuggestionsAsync(YouTubeClient.WEB_REMIX, query, ct);

        var texts = new List<string>();
        var items = new List<ShelfItem>();
        foreach (var section in response.Contents ?? new List<SuggestionsSection>())
        {
            foreach (var content in section.SearchSuggestionsSectionRenderer?.Contents ?? new List<SuggestionContent>())
            {
                var text = content.SearchSuggestionRenderer?.Suggestion?.FullText();
                if (!string.IsNullOrWhiteSpace(text)) texts.Add(text);
                if (content.MusicResponsiveListItemRenderer is { } row && ReadRow(row) is { } item) items.Add(item);
            }
        }
        return new SearchSuggestions { Texts = texts, Items = items };
    }

    /// <inheritdoc cref="Domain.Interfaces.IMusicProvider.SearchOverviewAsync" />
    public async Task<SearchOverview> SearchOverviewAsync(string query, CancellationToken ct = default)
    {
        var top = ReadTopResultAsync(query, ct);
        var songs = SearchSoftlyAsync(query, SearchFilter.Songs, ct);
        var videos = SearchSoftlyAsync(query, SearchFilter.Videos, ct);
        var artists = SearchSoftlyAsync(query, SearchFilter.Artists, ct);
        var albums = SearchSoftlyAsync(query, SearchFilter.Albums, ct);
        var playlists = SearchSoftlyAsync(query, SearchFilter.CommunityPlaylists, ct);
        var featured = SearchSoftlyAsync(query, SearchFilter.FeaturedPlaylists, ct);
        await Task.WhenAll(top, songs, videos, artists, albums, playlists, featured);

        var (best, bestSongs) = await top;
        var list = new[] { await songs, await videos, await artists, await albums, await playlists, await featured };
        // Every request failed: that is no answer, not "nothing found".
        if (best == null && list.All(r => r == null))
            throw new HttpRequestException("Every search request failed.");

        return new SearchOverview
        {
            TopResult = best,
            TopResultSongs = bestSongs,
            Songs = OneShelf("Songs", ShelfLayout.Rows, (await songs)?.Songs.Select(SongEntry)),
            Videos = OneShelf("Videos", ShelfLayout.Cards, (await videos)?.Songs.Select(VideoEntry)),
            Artists = OneShelf("Artists", ShelfLayout.Cards, (await artists)?.Artists.Select(ArtistEntry)),
            Albums = OneShelf("Albums", ShelfLayout.Cards, (await albums)?.Albums.Select(AlbumEntry)),
            Playlists = OneShelf("Playlists", ShelfLayout.Cards,
                ((await featured)?.Playlists ?? Array.Empty<PlaylistItem>())
                .Concat((await playlists)?.Playlists ?? Array.Empty<PlaylistItem>())
                .DistinctBy(p => p.Id)
                .Select(PlaylistEntry))
        };
    }

    private async Task<SearchResult?> SearchSoftlyAsync(string query, SearchFilter filter, CancellationToken ct)
    {
        try
        {
            return await SearchAsync(query, filter, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Search] {filter} failed: {ex.Message}");
            return null;
        }
    }

    private async Task<(ShelfItem? Top, IReadOnlyList<ShelfItem> Songs)> ReadTopResultAsync(string query, CancellationToken ct)
    {
        try
        {
            // No filter: only the unfiltered answer names a best match.
            var response = await _client.SearchAsync(YouTubeClient.WEB_REMIX, query, ct: ct);
            var sections = response.Contents?.TabbedSearchResultsRenderer?.Tabs?.FirstOrDefault()
                ?.TabRendererInner?.Content?.SectionListRenderer?.Contents;
            var card = sections?.Select(s => s.MusicCardShelfRenderer).FirstOrDefault(c => c != null);
            if (card == null) return (null, Array.Empty<ShelfItem>());

            var songs = (card.Contents ?? new List<MusicShelfContent>())
                .Select(c => c.MusicResponsiveListItemRenderer)
                .Where(r => r != null)
                .Select(r => ReadRow(r!))
                .Where(i => i != null)
                .Cast<ShelfItem>()
                .ToList();
            return (ReadCardShelf(card), songs);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Search] best match failed: {ex.Message}");
            return (null, Array.Empty<ShelfItem>());
        }
    }

    private static ShelfItem? ReadCardShelf(MusicCardShelfRenderer card)
    {
        var title = card.Title?.FullText();
        if (string.IsNullOrWhiteSpace(title)) return null;

        var endpoint = card.OnTap ?? card.Title?.Runs?.FirstOrDefault()?.NavigationEndpoint;
        var thumbnail = card.Thumbnail?.GetThumbnailUrl() ?? string.Empty;
        var subtitle = card.Subtitle?.FullText() ?? string.Empty;

        if (endpoint?.WatchEndpoint?.VideoId is { Length: > 0 } videoId)
        {
            var type = endpoint.WatchEndpoint.WatchEndpointMusicSupportedConfigs?.WatchEndpointMusicConfig?.MusicVideoType;
            var song = new SongItem
            {
                Id = videoId,
                Title = title,
                Artists = ArtistsWithoutType(card.Subtitle),
                ThumbnailUrl = thumbnail,
                VideoType = type,
                ViewCount = ParseViewCount(card.Subtitle)
            };
            var isVideo = IsVideoType(type);
            if (isVideo) PinToItself(song);
            return new ShelfItem
            {
                Kind = isVideo ? ShelfItemKind.Video : ShelfItemKind.Song,
                Id = videoId,
                Title = title,
                Subtitle = subtitle,
                ThumbnailUrl = thumbnail,
                Song = song
            };
        }

        var browseId = endpoint?.BrowseEndpoint?.BrowseId;
        return KindOfPage(browseId) is { } kind
            ? new ShelfItem { Kind = kind, Id = browseId!, Title = title, Subtitle = subtitle, ThumbnailUrl = thumbnail }
            : null;
    }

    private static IReadOnlyList<Shelf> OneShelf(string title, ShelfLayout layout, IEnumerable<ShelfItem>? items)
    {
        var list = items?.ToList();
        return list is { Count: > 0 }
            ? new[] { new Shelf { Title = title, Layout = layout, Items = list } }
            : Array.Empty<Shelf>();
    }

    // ---- The shapes YouTube answers with ----

    private List<Shelf> ReadShelves(List<SectionContent>? contents)
    {
        var shelves = new List<Shelf>();
        foreach (var content in contents ?? new List<SectionContent>())
            if (ReadShelf(content) is { Items.Count: > 0 } shelf)
                shelves.Add(shelf);
        return shelves;
    }

    private Shelf? ReadShelf(SectionContent content)
    {
        if (content.MusicCarouselShelfRenderer is { } carousel)
        {
            var header = carousel.Header?.MusicCarouselShelfBasicHeaderRenderer;
            var more = header?.MoreContentButton?.ButtonRenderer?.NavigationEndpoint?.BrowseEndpoint;
            var items = new List<ShelfItem>();
            var rows = 0;
            foreach (var entry in carousel.Contents ?? new List<CarouselContent>())
            {
                if (entry.MusicResponsiveListItemRenderer is { } row)
                {
                    if (ReadRow(row) is { } item) { items.Add(item); rows++; }
                }
                else if (entry.MusicTwoRowItemRenderer is { } card && ReadCard(card) is { } item)
                {
                    items.Add(item);
                }
            }
            return new Shelf
            {
                Title = header?.Title?.FullText() ?? string.Empty,
                Layout = rows > items.Count / 2 ? ShelfLayout.Rows : ShelfLayout.Cards,
                Items = items,
                MoreBrowseId = more?.BrowseId,
                MoreParams = more?.Params
            };
        }

        if (content.GridRenderer is { Items: { } grid } gridRenderer)
        {
            var items = new List<ShelfItem>();
            foreach (var entry in grid)
            {
                if (entry.MusicTwoRowItemRenderer is { } card && ReadCard(card) is { } item) items.Add(item);
                else if (entry.MusicResponsiveListItemRenderer is { } row && ReadRow(row) is { } rowItem) items.Add(rowItem);
            }
            return new Shelf { Title = gridRenderer.Header?.GridHeaderRenderer?.Title?.FullText() ?? string.Empty, Items = items };
        }

        var rowsShelf = content.MusicShelfRenderer?.Contents ?? content.MusicPlaylistShelfRenderer?.Contents;
        if (rowsShelf != null)
        {
            var items = new List<ShelfItem>();
            foreach (var entry in rowsShelf)
                if (entry.MusicResponsiveListItemRenderer is { } row && ReadRow(row) is { } item)
                    items.Add(item);
            var more = content.MusicShelfRenderer?.BottomEndpoint?.BrowseEndpoint;
            return new Shelf
            {
                Title = content.MusicShelfRenderer?.Title?.FullText() ?? string.Empty,
                Layout = ShelfLayout.Rows,
                Items = items,
                MoreBrowseId = more?.BrowseId,
                MoreParams = more?.Params
            };
        }

        return null;
    }

    /// <summary>A cover card. Null for what the app has no page for (podcasts, episodes).</summary>
    private static ShelfItem? ReadCard(MusicTwoRowItemRenderer card)
    {
        var title = card.Title?.FullText();
        if (string.IsNullOrWhiteSpace(title)) return null;

        var thumbnail = card.ThumbnailRenderer?.GetThumbnailUrl() ?? string.Empty;
        var subtitle = card.Subtitle?.FullText() ?? string.Empty;
        var endpoint = card.NavigationEndpoint;

        if (endpoint?.WatchEndpoint?.VideoId is { Length: > 0 } videoId)
        {
            var type = endpoint.WatchEndpoint.WatchEndpointMusicSupportedConfigs?.WatchEndpointMusicConfig?.MusicVideoType;
            var isVideo = IsVideoType(type) || string.IsNullOrWhiteSpace(type);
            var song = new SongItem
            {
                Id = videoId,
                Title = title,
                Artists = ArtistsWithoutType(card.Subtitle),
                ThumbnailUrl = thumbnail,
                VideoType = type,
                ViewCount = ParseViewCount(card.Subtitle)
            };
            if (isVideo) PinToItself(song);
            return new ShelfItem
            {
                Kind = isVideo ? ShelfItemKind.Video : ShelfItemKind.Song,
                Id = videoId,
                Title = title,
                Subtitle = subtitle,
                ThumbnailUrl = thumbnail,
                Song = song
            };
        }

        var browseId = endpoint?.BrowseEndpoint?.BrowseId;
        return KindOfPage(browseId) is { } kind
            ? new ShelfItem { Kind = kind, Id = browseId!, Title = title, Subtitle = subtitle, ThumbnailUrl = thumbnail }
            : null;
    }

    /// <summary>A compact row: quick picks, charts, the rows of a search.</summary>
    private ShelfItem? ReadRow(MusicResponsiveListItemRenderer row)
    {
        var columns = row.FlexColumns;
        var title = columns?.FirstOrDefault()?.MusicResponsiveListItemFlexColumnRenderer?.Text?.FullText();
        if (string.IsNullOrWhiteSpace(title)) return null;

        var second = columns!.Count > 1 ? columns[1].MusicResponsiveListItemFlexColumnRenderer?.Text : null;
        var subtitle = second?.FullText() ?? string.Empty;
        var thumbnail = row.Thumbnail?.GetThumbnailUrl() ?? string.Empty;
        var index = row.CustomIndexColumn?.MusicCustomIndexColumnRenderer;
        var rank = int.TryParse(index?.Text?.FullText(), out var place) ? place : (int?)null;
        var trend = index?.Icon?.IconType switch
        {
            "ARROW_DROP_UP" => ChartTrend.Up,
            "ARROW_DROP_DOWN" => ChartTrend.Down,
            "ARROW_CHART_NEUTRAL" => ChartTrend.Same,
            _ => ChartTrend.None
        };

        var videoId = row.PlaylistItemData?.VideoId ?? row.NavigationEndpoint?.WatchEndpoint?.VideoId;
        if (string.IsNullOrEmpty(videoId))
        {
            var browseId = row.NavigationEndpoint?.BrowseEndpoint?.BrowseId;
            return KindOfPage(browseId) is { } kind
                ? new ShelfItem
                {
                    Kind = kind,
                    Id = browseId!,
                    Title = title,
                    Subtitle = subtitle,
                    ThumbnailUrl = thumbnail,
                    Rank = rank,
                    Trend = trend
                }
                : null;
        }

        var songs = new List<SongItem>();
        ParseMusicListItem(row, songs, new(), new(), new());
        var song = songs.FirstOrDefault() ?? new SongItem { Id = videoId, Title = title, ThumbnailUrl = thumbnail };
        song.Artists = ArtistsWithoutType(second);
        song.VideoType ??= row.Overlay?.MusicItemThumbnailOverlayRenderer?.Content?.MusicPlayButtonRenderer
            ?.PlayNavigationEndpoint?.WatchEndpoint?.WatchEndpointMusicSupportedConfigs?.WatchEndpointMusicConfig?.MusicVideoType;

        var isVideo = IsVideoType(song.VideoType);
        if (isVideo) PinToItself(song);
        return new ShelfItem
        {
            Kind = isVideo ? ShelfItemKind.Video : ShelfItemKind.Song,
            Id = videoId,
            Title = title,
            Subtitle = subtitle,
            ThumbnailUrl = thumbnail,
            Song = song,
            Rank = rank,
            Trend = trend
        };
    }

    private static bool IsVideoType(string? type) =>
        type is "MUSIC_VIDEO_TYPE_OMV" or "MUSIC_VIDEO_TYPE_UGC";

    /// <summary>The page behind a browse id; null for what the app has no page for.</summary>
    private static ShelfItemKind? KindOfPage(string? browseId)
    {
        if (string.IsNullOrWhiteSpace(browseId)) return null;
        if (browseId.StartsWith("UC", StringComparison.Ordinal)) return ShelfItemKind.Artist;
        if (browseId.StartsWith("MPRE", StringComparison.Ordinal)) return ShelfItemKind.Album;
        if (browseId.StartsWith("VL", StringComparison.Ordinal) ||
            browseId.StartsWith("PL", StringComparison.Ordinal) ||
            browseId.StartsWith("RD", StringComparison.Ordinal) ||
            browseId.StartsWith("OLAK", StringComparison.Ordinal))
            return ShelfItemKind.Playlist;
        return null;
    }

    private static readonly HashSet<string> RowTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Song", "Video", "Album", "Single", "EP", "Playlist", "Artist", "Episode", "Podcast"
    };

    /// <summary>
    /// Search rows open with their type ("Song • Imagine Dragons • 7B plays"): the artists
    /// are the segment after it, or the first one when there is no type.
    /// </summary>
    private static List<Artist> ArtistsWithoutType(RunsText? subtitle)
    {
        var artists = new List<Artist>();
        var segments = subtitle?.Runs?.SplitBySeparator();
        if (segments is not { Count: > 0 }) return artists;

        var segment = segments[0];
        if (segment.Count == 1 && RowTypes.Contains(segment[0].Text.Trim()))
        {
            if (segments.Count < 2) return artists;
            segment = segments[1];
        }

        for (var i = 0; i < segment.Count; i += 2)
        {
            var name = segment[i].Text.Trim();
            if (name.Length == 0 || ThumbnailExtensions.ParseTime(name) > 0) continue;
            artists.Add(new Artist { Name = name, Id = segment[i].NavigationEndpoint?.BrowseEndpoint?.BrowseId });
        }
        return artists;
    }

    private static void PinToItself(SongItem video)
    {
        video.VideoVersionId = video.Id;
        video.SongVersionId = video.Id;
    }

    // ---- Search rows as shelf entries ----

    private static ShelfItem SongEntry(SongItem song) => new()
    {
        Kind = ShelfItemKind.Song,
        Id = song.Id,
        Title = song.Title,
        Subtitle = song.ViewCount > 0 ? $"{song.ArtistDisplay} • {song.ViewCountText}" : song.ArtistDisplay,
        ThumbnailUrl = song.ThumbnailUrl,
        Song = song
    };

    private static ShelfItem VideoEntry(SongItem video)
    {
        PinToItself(video);
        return new ShelfItem
        {
            Kind = ShelfItemKind.Video,
            Id = video.Id,
            Title = video.Title,
            Subtitle = video.ViewCount > 0 ? $"{video.ArtistDisplay} • {video.ViewCountText}" : video.ArtistDisplay,
            ThumbnailUrl = video.ThumbnailUrl,
            Song = video
        };
    }

    private static ShelfItem ArtistEntry(ArtistItem artist) => new()
    {
        Kind = ShelfItemKind.Artist,
        Id = artist.Id,
        Title = artist.Name,
        Subtitle = artist.Subtitle ?? string.Empty,
        ThumbnailUrl = artist.ThumbnailUrl
    };

    private static ShelfItem AlbumEntry(AlbumItem album) => new()
    {
        Kind = ShelfItemKind.Album,
        Id = album.Id,
        Title = album.Title,
        Subtitle = album.Subtitle ?? album.ArtistDisplay,
        ThumbnailUrl = album.ThumbnailUrl
    };

    private static ShelfItem PlaylistEntry(PlaylistItem playlist) => new()
    {
        Kind = ShelfItemKind.Playlist,
        Id = playlist.Id,
        Title = playlist.Title,
        Subtitle = playlist.Subtitle ?? playlist.Author ?? string.Empty,
        ThumbnailUrl = playlist.ThumbnailUrl
    };
}
