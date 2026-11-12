using SoundLibert.Domain.Enums;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;
using SoundLibert.Infra.InnerTube;
using SoundLibert.Infra.InnerTube.Models;

namespace SoundLibert.Infra;

public partial class InnerTubeMusicProvider : IMusicProvider
{
    private readonly InnerTubeClient _client;

    public InnerTubeMusicProvider(InnerTubeClient client)
    {
        _client = client;
    }

    private const string VideosFilterParams = "EgWKAQIQAWoKEAkQChAFEAMQBA%3D%3D";

    public async Task<SearchResult> SearchAsync(string query, SearchFilter filter = SearchFilter.Songs, CancellationToken ct = default)
    {
        var filterParam = filter switch
        {
            SearchFilter.Songs => "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D",
            SearchFilter.Videos => VideosFilterParams,
            SearchFilter.Albums => "EgWKAQIYAWoKEAkQChAFEAMQBA%3D%3D",
            SearchFilter.Artists => "EgWKAQIgAWoKEAkQChAFEAMQBA%3D%3D",
            SearchFilter.FeaturedPlaylists => "EgeKAQQoADgBagwQDhAKEAMQBRAJEAQ%3D",
            SearchFilter.CommunityPlaylists => "EgeKAQQoAEABagoQAxAEEAoQCRAF",
            _ => null
        };

        var response = await _client.SearchAsync(
            YouTubeClient.WEB_REMIX, query, filterParam, ct: ct);

        var songs = new List<SongItem>();
        var albums = new List<AlbumItem>();
        var artists = new List<ArtistItem>();
        var playlists = new List<PlaylistItem>();

        var tabs = response.Contents?.TabbedSearchResultsRenderer?.Tabs;
        if (tabs == null || tabs.Count == 0)
            return new SearchResult { Songs = songs, Albums = albums, Artists = artists, Playlists = playlists };

        var tabContent = tabs[0].TabRendererInner?.Content?.SectionListRenderer?.Contents;
        if (tabContent == null)
            return new SearchResult { Songs = songs, Albums = albums, Artists = artists, Playlists = playlists };

        foreach (var section in tabContent)
        {
            if (section.MusicShelfRenderer?.Contents != null)
            {
                foreach (var item in section.MusicShelfRenderer.Contents)
                {
                    if (item.MusicResponsiveListItemRenderer != null)
                        ParseMusicListItem(item.MusicResponsiveListItemRenderer, songs, albums, artists, playlists);
                }
            }
        }

        return new SearchResult { Songs = songs, Albums = albums, Artists = artists, Playlists = playlists };
    }

    public async Task<AlbumItem?> GetAlbumAsync(string browseId, CancellationToken ct = default)
    {
        var response = await _client.BrowseAsync(YouTubeClient.WEB_REMIX, browseId, ct: ct);
        return ParseAlbumFromBrowse(response, browseId);
    }

    public async Task<IReadOnlyList<SongItem>> GetAlbumSongsAsync(string playlistId, CancellationToken ct = default)
    {
        return await GetPlaylistSongsAsync(playlistId, ct);
    }

    /// <summary>
    /// Loads an album's tracks by browsing the album's own browseId (MPREb…).
    /// Album pages don't reliably expose a playlist id (the header buttons are
    /// not parsed), so we read the track shelf directly and accept either the
    /// playlist-shelf or plain-shelf shape.
    /// </summary>
    public async Task<IReadOnlyList<SongItem>> GetAlbumTracksAsync(string albumBrowseId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(albumBrowseId)) return Array.Empty<SongItem>();

        var response = await _client.BrowseAsync(YouTubeClient.WEB_REMIX, albumBrowseId, ct: ct);

        var contents = response.Contents?.TwoColumnBrowseResultsRenderer?
                .SecondaryContents?.SectionListRenderer?.Contents
            ?? response.Contents?.SingleColumnBrowseResultsRenderer?.Tabs?.FirstOrDefault()
                ?.TabRenderer?.Content?.SectionListRenderer?.Contents
            ?? response.Contents?.SectionListRenderer?.Contents;

        var songs = new List<SongItem>();
        if (contents == null) return songs;

        foreach (var content in contents)
        {
            if (content.MusicPlaylistShelfRenderer?.Contents != null)
            {
                foreach (var item in content.MusicPlaylistShelfRenderer.Contents)
                    if (item.MusicResponsiveListItemRenderer != null)
                        ParseMusicListItem(item.MusicResponsiveListItemRenderer, songs, new(), new(), new());
            }
            else if (content.MusicShelfRenderer?.Contents != null)
            {
                foreach (var item in content.MusicShelfRenderer.Contents)
                    if (item.MusicResponsiveListItemRenderer != null)
                        ParseMusicListItem(item.MusicResponsiveListItemRenderer, songs, new(), new(), new());
            }
        }

        return songs;
    }

    public async Task<ArtistItem?> GetArtistAsync(string browseId, CancellationToken ct = default)
    {
        var response = await _client.BrowseAsync(YouTubeClient.WEB_REMIX, browseId, ct: ct);

        var header = response.Header;
        var title = header?.MusicImmersiveHeaderRenderer?.Title?.FirstText()
            ?? header?.MusicResponsiveHeaderRenderer?.Title?.FirstText()
            ?? header?.MusicHeaderRenderer?.Title?.FirstText();

        if (string.IsNullOrEmpty(title)) return null;

        var thumbnail = header?.MusicImmersiveHeaderRenderer?.Thumbnail?.GetThumbnailUrl()
            ?? header?.MusicResponsiveHeaderRenderer?.Thumbnail?.GetThumbnailUrl()
            ?? string.Empty;

        return new ArtistItem
        {
            Id = browseId,
            Name = title,
            ThumbnailUrl = thumbnail
        };
    }

    public Task<ArtistPage> GetArtistPageAsync(string browseId, CancellationToken ct = default)
        => ReadArtistPageAsync(browseId, expandShelves: true, ct);

    /// <inheritdoc cref="IMusicProvider.GetArtistPreviewAsync" />
    public Task<ArtistPage> GetArtistPreviewAsync(string browseId, CancellationToken ct = default)
        => ReadArtistPageAsync(browseId, expandShelves: false, ct);

    private async Task<ArtistPage> ReadArtistPageAsync(string browseId, bool expandShelves, CancellationToken ct)
    {
        var response = await _client.BrowseAsync(YouTubeClient.WEB_REMIX, browseId, ct: ct);

        var page = new ArtistPage { Artist = ParseArtistHeader(response, browseId) };
        var immersive = response.Header?.MusicImmersiveHeaderRenderer;
        page.Description = ReadableStory(immersive?.Description?.FullText());
        page.MonthlyAudienceText = immersive?.MonthlyListenerCount?.FullText();
        page.SubscribersText = immersive?.SubscriptionButton?.SubscribeButtonRenderer?.LongSubscriberCountText?.FullText();

        var contents = GetBrowseContents(response);
        if (contents == null) return page;

        var songs = new List<SongItem>();
        var albums = new List<AlbumItem>();
        var singles = new List<AlbumItem>();
        var appearsOn = new List<AlbumItem>();
        var playlists = new List<PlaylistItem>();
        var related = new List<ArtistItem>();
        var videos = new List<SongItem>();
        var live = new List<SongItem>();
        var albumIds = new HashSet<string>(StringComparer.Ordinal);
        var singleIds = new HashSet<string>(StringComparer.Ordinal);
        var appearsIds = new HashSet<string>(StringComparer.Ordinal);
        var playlistIds = new HashSet<string>(StringComparer.Ordinal);
        var relatedIds = new HashSet<string>(StringComparer.Ordinal);

        // Carousels only carry ~10 items; their "More" button (browse id + params)
        // returns the FULL shelf. Capture it per classification and expand below.
        BrowseEndpoint? albumsMore = null, singlesMore = null, appearsMore = null, playlistsMore = null;

        foreach (var content in contents)
        {
            if (content.MusicShelfRenderer?.Contents != null)
            {
                foreach (var item in content.MusicShelfRenderer.Contents)
                    if (item.MusicResponsiveListItemRenderer != null)
                        ParseMusicListItem(item.MusicResponsiveListItemRenderer, songs, new(), new(), new());
                page.TopSongsListId ??= content.MusicShelfRenderer.BottomEndpoint?.BrowseEndpoint?.BrowseId;
            }
            else if (content.MusicCarouselShelfRenderer is { } carousel)
            {
                // Classify the shelf so albums, singles, playlists and features
                // don't get dumped into one mixed "Albums" list (the old code
                // put every non-single carousel into Albums).
                var title = carousel.Header?.MusicCarouselShelfBasicHeaderRenderer?.Title?.FirstText() ?? "";

                var isVideos = ContainsAny(title, "video");
                if (isVideos || ContainsAny(title, "live", "performance"))
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var entry in carousel.Contents ?? new List<CarouselContent>())
                        AddVideo(ReadVideoCard(entry.MusicTwoRowItemRenderer, page.Artist.Name), seen, isVideos ? videos : live);
                    continue;
                }

                var isRelated = ContainsAny(title, "also like", "related", "fans");
                var isSingle = SinglesShelfTitle.IsMatch(title);
                var isFeatured = ContainsAny(title, "featured on", "appears", "featuring", "soundtrack");
                var isPlaylists = ContainsAny(title, "playlist");

                var more = carousel.Header?.MusicCarouselShelfBasicHeaderRenderer?.MoreContentButton?.ButtonRenderer?.NavigationEndpoint?.BrowseEndpoint;
                if (!string.IsNullOrEmpty(more?.BrowseId))
                {
                    if (isRelated) { /* related artists have no full list */ }
                    else if (isPlaylists) playlistsMore ??= more;
                    else if (isSingle) singlesMore ??= more;
                    else if (isFeatured) appearsMore ??= more;
                    // Only the albums shelf: "Show all" of a Videos shelf must
                    // not stand in for the album list.
                    else if (ContainsAny(title, "album")) albumsMore ??= more;
                }

                foreach (var entry in carousel.Contents ?? new List<CarouselContent>())
                {
                    var renderer = entry.MusicTwoRowItemRenderer;
                    if (renderer == null) continue;
                    var itemBrowse = renderer.NavigationEndpoint?.BrowseEndpoint?.BrowseId;

                    if (isRelated && itemBrowse != null && itemBrowse.StartsWith("UC", StringComparison.Ordinal))
                    {
                        if (relatedIds.Add(itemBrowse))
                            related.Add(new ArtistItem
                            {
                                Id = itemBrowse,
                                Name = renderer.Title?.FirstText() ?? string.Empty,
                                ThumbnailUrl = renderer.ThumbnailRenderer?.GetThumbnailUrl() ?? string.Empty,
                                Subtitle = renderer.Subtitle?.FullText()
                            });
                        continue;
                    }

                    if (isPlaylists)
                    {
                        if (!string.IsNullOrEmpty(itemBrowse) && playlistIds.Add(itemBrowse))
                            playlists.Add(new PlaylistItem
                            {
                                Id = itemBrowse,
                                Title = renderer.Title?.FirstText() ?? string.Empty,
                                ThumbnailUrl = renderer.ThumbnailRenderer?.GetThumbnailUrl() ?? string.Empty,
                                Author = renderer.Subtitle?.FirstText(),
                                Subtitle = renderer.Subtitle?.FullText()
                            });
                        continue;
                    }

                    if (ParseTwoRowItem(renderer) is AlbumItem album)
                    {
                        if (isSingle)
                        {
                            if (singleIds.Add(album.Id)) singles.Add(album);
                        }
                        else if (isFeatured)
                        {
                            if (appearsIds.Add(album.Id)) appearsOn.Add(album);
                        }
                        else if (album.Id.StartsWith("VL", StringComparison.Ordinal))
                        {
                            // Playlist id in a non-playlist shelf → keep it out of Albums.
                            if (playlistIds.Add(album.Id))
                                playlists.Add(new PlaylistItem
                                {
                                    Id = album.Id,
                                    Title = album.Title,
                                    ThumbnailUrl = album.ThumbnailUrl
                                });
                        }
                        else if (albumIds.Add(album.Id))
                        {
                            albums.Add(album);
                        }
                    }
                }
            }
            else if (content.MusicDescriptionShelfRenderer is { } description &&
                     string.IsNullOrWhiteSpace(page.Description))
            {
                page.Description = ReadableStory(description.Description?.FullText() ?? description.Text?.FullText());
            }

            if (content.MusicDescriptionShelfRenderer?.Subheader is { } views)
                page.ViewsText = ShortViews(views.FullText());
        }

        // A made-up "artist" gets other people's uploads on these shelves.
        var wanted = NormName(page.Artist.Name);
        page.Videos = videos.Where(v => CreditsArtist(v, browseId, wanted)).ToList();
        page.LivePerformances = live.Where(v => CreditsArtist(v, browseId, wanted)).ToList();

        if (!expandShelves)
        {
            page.Songs = DedupeById(songs);
            page.Albums = albums;
            page.Singles = singles;
            page.AppearsOn = appearsOn;
            page.Playlists = playlists;
            page.RelatedArtists = related;
            return page;
        }

        // Expand each shelf via its "More" endpoint so ALL albums/singles/etc.
        // load (the carousel itself returns only the first ~10). The four
        // fetches run in PARALLEL (they were sequential — the artist page
        // waited for each one in turn); merging stays in the same order.
        var albumsMoreTask = albumsMore != null ? FetchMoreItemsAsync(albumsMore, ct) : null;
        var singlesMoreTask = singlesMore != null ? FetchMoreItemsAsync(singlesMore, ct) : null;
        var appearsMoreTask = appearsMore != null ? FetchMoreItemsAsync(appearsMore, ct) : null;
        var playlistsMoreTask = playlistsMore != null ? FetchMoreItemsAsync(playlistsMore, ct) : null;
        await Task.WhenAll(new[] { albumsMoreTask, singlesMoreTask, appearsMoreTask, playlistsMoreTask }
            .Where(t => t != null).Cast<Task>());

        if (albumsMoreTask != null)
            MergeArtistShelf(albumsMore!, await albumsMoreTask, albums, albumIds, playlists, playlistIds);
        if (singlesMoreTask != null)
            MergeArtistShelf(singlesMore!, await singlesMoreTask, singles, singleIds, playlists, playlistIds);
        if (appearsMoreTask != null)
            MergeArtistShelf(appearsMore!, await appearsMoreTask, appearsOn, appearsIds, playlists, playlistIds);
        if (playlistsMoreTask != null)
            MergeArtistShelf(playlistsMore!, await playlistsMoreTask, null, null, playlists, playlistIds);

        page.Songs = DedupeById(songs);
        page.Albums = albums;
        page.Singles = singles;
        page.AppearsOn = appearsOn;
        page.Playlists = playlists;
        page.RelatedArtists = related;

        System.Diagnostics.Debug.WriteLine(
            $"[Artist] {page.Artist.Name}: {page.Songs.Count} songs, {page.Albums.Count} albums, " +
            $"{page.Singles.Count} singles, {page.AppearsOn.Count} features, " +
            $"{page.Playlists.Count} playlists, {page.RelatedArtists.Count} related");
        return page;
    }

    /// <summary>"23,688,076,706 views" reads better as "23.7B views".</summary>
    private static string ShortViews(string text)
    {
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var views) && views > 0 ? ViewCountFormatting.FormatViews(views) : text;
    }

    private static bool ContainsAny(string text, params string[] needles)
        => needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>"Singles &amp; EPs", but not every title that merely holds the letters "ep".</summary>
    private static readonly System.Text.RegularExpressions.Regex SinglesShelfTitle = new(
        @"\b(?:singles?|eps?)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Merges a shelf's full contents (fetched via its "More" browse endpoint)
    /// into the given bucket. <paramref name="albums"/> may be null (pure
    /// playlists shelf).
    /// </summary>
    private static void MergeArtistShelf(
        BrowseEndpoint more,
        (List<AlbumItem> Albums, List<PlaylistItem> Playlists) fetched,
        List<AlbumItem>? albums, HashSet<string>? albumIds,
        List<PlaylistItem> playlists, HashSet<string> playlistIds)
    {
        var (extraAlbums, extraPlaylists) = fetched;

        if (albums != null && albumIds != null)
            foreach (var a in extraAlbums)
                if (albumIds.Add(a.Id)) albums.Add(a);
        foreach (var p in extraPlaylists)
            if (playlistIds.Add(p.Id)) playlists.Add(p);

        System.Diagnostics.Debug.WriteLine(
            $"[Artist] More {more.BrowseId}: +{extraAlbums.Count} albums, +{extraPlaylists.Count} playlists");
    }

    /// <summary>Browses a "More" endpoint and follows its continuations, parsing every album/playlist card.</summary>
    private async Task<(List<AlbumItem> Albums, List<PlaylistItem> Playlists)> FetchMoreItemsAsync(
        BrowseEndpoint more, CancellationToken ct)
    {
        var albums = new List<AlbumItem>();
        var playlists = new List<PlaylistItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? continuation = null;

        for (var page = 0; page < 20; page++)
        {
            ct.ThrowIfCancellationRequested();
            BrowseResponse response;
            try
            {
                response = await _client.BrowseAsync(
                    YouTubeClient.WEB_REMIX, more.BrowseId, more.Params, continuation, ct: ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Artist] shelf More failed: {ex.Message}");
                break;
            }

            // Later pages arrive as a continuation, not as a new section list.
            var nextPage = response.ContinuationContents;
            if (nextPage?.GridContinuation?.Items is { } moreCards)
            {
                foreach (var gi in moreCards)
                    if (gi.MusicTwoRowItemRenderer is { } tr) AddMoreTwoRow(tr, albums, playlists, seen);
                continuation = nextPage.GridContinuation.Continuations?.FirstOrDefault()?.AnyToken;
                if (string.IsNullOrEmpty(continuation)) break;
                continue;
            }
            if ((nextPage?.MusicShelfContinuation ?? nextPage?.MusicPlaylistShelfContinuation) is { Contents: { } moreRows } moreShelf)
            {
                AddMoreShelf(moreRows, albums, playlists, seen);
                continuation = ShelfToken(moreShelf.Continuations, moreRows);
                if (string.IsNullOrEmpty(continuation)) break;
                continue;
            }

            var contents = GetBrowseContents(response);
            if (contents == null) break;

            foreach (var content in contents)
            {
                if (content.GridRenderer?.Items != null)
                {
                    foreach (var gi in content.GridRenderer.Items)
                        if (gi.MusicTwoRowItemRenderer is { } tr) AddMoreTwoRow(tr, albums, playlists, seen);
                }
                else if (content.MusicCarouselShelfRenderer?.Contents != null)
                {
                    foreach (var ci in content.MusicCarouselShelfRenderer.Contents)
                        if (ci.MusicTwoRowItemRenderer is { } tr2) AddMoreTwoRow(tr2, albums, playlists, seen);
                }
                else if (content.MusicShelfRenderer?.Contents != null)
                {
                    AddMoreShelf(content.MusicShelfRenderer.Contents, albums, playlists, seen);
                }
                else if (content.MusicPlaylistShelfRenderer?.Contents != null)
                {
                    AddMoreShelf(content.MusicPlaylistShelfRenderer.Contents, albums, playlists, seen);
                }
            }

            continuation = NextAnyContinuation(contents, response);
            if (string.IsNullOrEmpty(continuation)) break;
        }

        return (albums, playlists);
    }

    private void AddMoreTwoRow(
        MusicTwoRowItemRenderer renderer, List<AlbumItem> albums, List<PlaylistItem> playlists, HashSet<string> seen)
    {
        var browse = renderer.NavigationEndpoint?.BrowseEndpoint?.BrowseId;
        if (string.IsNullOrEmpty(browse) || !seen.Add(browse)) return;
        var title = renderer.Title?.FirstText() ?? string.Empty;
        var thumb = renderer.ThumbnailRenderer?.GetThumbnailUrl() ?? string.Empty;
        var subtitle = renderer.Subtitle?.FirstText();

        if (browse.StartsWith("VL", StringComparison.Ordinal) ||
            browse.StartsWith("PL", StringComparison.Ordinal) ||
            browse.StartsWith("OLAK", StringComparison.Ordinal))
        {
            playlists.Add(new PlaylistItem { Id = browse, Title = title, ThumbnailUrl = thumb, Author = subtitle });
        }
        else
        {
            albums.Add(new AlbumItem
            {
                Id = browse,
                Title = title,
                ThumbnailUrl = thumb,
                Artists = ExtractAlbumArtists(renderer.Subtitle)
            });
        }
    }

    private void AddMoreShelf(
        List<MusicShelfContent> contents, List<AlbumItem> albums, List<PlaylistItem> playlists, HashSet<string> seen)
    {
        var localAlbums = new List<AlbumItem>();
        var localPlaylists = new List<PlaylistItem>();
        foreach (var item in contents)
            if (item.MusicResponsiveListItemRenderer is { } renderer)
                ParseMusicListItem(renderer, new List<SongItem>(), localAlbums, new List<ArtistItem>(), localPlaylists);
        foreach (var a in localAlbums) if (seen.Add(a.Id)) albums.Add(a);
        foreach (var p in localPlaylists) if (seen.Add(p.Id)) playlists.Add(p);
    }

    private static string? NextAnyContinuation(List<SectionContent> contents, BrowseResponse response)
    {
        foreach (var c in contents)
        {
            var token = GridToken(c.GridRenderer?.Continuations, c.GridRenderer?.Items)
                        ?? ShelfToken(c.MusicShelfRenderer?.Continuations, c.MusicShelfRenderer?.Contents)
                        ?? ShelfToken(c.MusicPlaylistShelfRenderer?.Continuations, c.MusicPlaylistShelfRenderer?.Contents);
            if (!string.IsNullOrEmpty(token)) return token;
        }

        return response.ContinuationContents?.SectionListContinuation?.Continuations?.FirstOrDefault()?.AnyToken;
    }

    /// <summary>The next-page token of a shelf, in both shapes YouTube returns it.</summary>
    private static string? ShelfToken(List<Continuation>? continuations, List<MusicShelfContent>? contents)
        => continuations?.FirstOrDefault()?.AnyToken
           ?? contents?.LastOrDefault()?.ContinuationItemRenderer?.ContinuationEndpoint?.ContinuationCommand?.Token;

    /// <summary>The next-page token of a grid, in both shapes YouTube returns it.</summary>
    private static string? GridToken(List<Continuation>? continuations, List<GridItem>? items)
        => continuations?.FirstOrDefault()?.AnyToken
           ?? items?.LastOrDefault()?.ContinuationItemRenderer?.ContinuationEndpoint?.ContinuationCommand?.Token;

    private static ArtistItem ParseArtistHeader(BrowseResponse response, string browseId)
    {
        var header = response.Header;
        var title = header?.MusicImmersiveHeaderRenderer?.Title?.FirstText()
            ?? header?.MusicResponsiveHeaderRenderer?.Title?.FirstText()
            ?? header?.MusicHeaderRenderer?.Title?.FirstText()
            ?? string.Empty;

        var thumbnail = header?.MusicImmersiveHeaderRenderer?.Thumbnail?.GetThumbnailUrl()
            ?? header?.MusicResponsiveHeaderRenderer?.Thumbnail?.GetThumbnailUrl()
            ?? string.Empty;

        return new ArtistItem { Id = browseId, Name = title, ThumbnailUrl = thumbnail };
    }

    public async Task<PlaylistItem?> GetPlaylistAsync(string playlistId, CancellationToken ct = default)
    {
        var response = await _client.BrowseAsync(
            YouTubeClient.WEB_REMIX, browseId: WithVl(playlistId), setLogin: true, ct: ct);

        var header = GetBrowseHeader(response);
        if (header == null) return null;

        return new PlaylistItem
        {
            Id = playlistId,
            Title = header.Title?.FirstText() ?? string.Empty,
            Author = header.StraplineTextOne?.FirstText(),
            ThumbnailUrl = header.Thumbnail?.GetThumbnailUrl() ?? string.Empty,
            SongCountText = header.SecondSubtitle?.FirstText()
        };
    }

    public async Task<IReadOnlyList<SongItem>> GetPlaylistSongsAsync(string playlistId, CancellationToken ct = default)
    {
        var response = await _client.BrowseAsync(
            YouTubeClient.WEB_REMIX, browseId: WithVl(playlistId), setLogin: true, ct: ct);

        var songs = ParsePlaylistSongs(response);

        // Playlists typically only return the first ~100 tracks with the rest
        // behind a continuation, so follow it to load the whole list.
        var token = FirstShelfContinuation(response);
        if (!string.IsNullOrEmpty(token))
            songs.AddRange(await PaginateShelfAsync(token, ct));

        var deduped = DedupeById(songs);
        System.Diagnostics.Debug.WriteLine($"[Playlist] {playlistId}: {deduped.Count} tracks");
        return deduped;
    }

    // ---- Account library (requires sign-in) ----

    /// <summary>Everything an account library section lists, across all its pages.</summary>
    private sealed class LibrarySectionItems
    {
        public List<MusicTwoRowItemRenderer> Cards { get; } = new();
        public List<MusicResponsiveListItemRenderer> Rows { get; } = new();

        public void Add(IEnumerable<GridItem>? items)
        {
            foreach (var item in items ?? Enumerable.Empty<GridItem>())
            {
                if (item.MusicTwoRowItemRenderer != null) Cards.Add(item.MusicTwoRowItemRenderer);
                else if (item.MusicResponsiveListItemRenderer != null) Rows.Add(item.MusicResponsiveListItemRenderer);
            }
        }

        public void Add(IEnumerable<MusicShelfContent>? rows)
        {
            foreach (var row in rows ?? Enumerable.Empty<MusicShelfContent>())
                if (row.MusicResponsiveListItemRenderer != null) Rows.Add(row.MusicResponsiveListItemRenderer);
        }
    }

    /// <summary>A guard against a server that keeps sending new tokens; far above any real library.</summary>
    private const int MaxLibraryPages = 2000;

    /// <summary>
    /// Reads a whole account library section (port of Velune YouTube.library and
    /// libraryContinuation). The result is the source of truth for the sync:
    /// whatever it does not list is removed from the local mirror. So it either
    /// returns every page or throws; a partial list never leaves this method.
    /// </summary>
    private async Task<LibrarySectionItems> ReadLibrarySectionAsync(string browseId, CancellationToken ct)
    {
        var response = await _client.BrowseAsync(
            YouTubeClient.WEB_REMIX, browseId, setLogin: true, accountOnly: true, ct: ct);
        ThrowIfSignedOut(response, browseId);

        var items = new LibrarySectionItems();
        var token = ReadFirstLibraryPage(response, items, browseId);

        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrEmpty(token))
        {
            ct.ThrowIfCancellationRequested();
            if (!seenTokens.Add(token) || seenTokens.Count > MaxLibraryPages)
                throw new SoundLibert.Domain.Exceptions.AccountLibraryUnrecognizedException(browseId, "the pages never end");

            var page = await _client.BrowseAsync(
                YouTubeClient.WEB_REMIX, continuation: token, setLogin: true, accountOnly: true, ct: ct);
            ThrowIfSignedOut(page, browseId);
            token = ReadNextLibraryPage(page, items, browseId);
        }

        System.Diagnostics.Trace.WriteLine(
            $"[Library] {browseId}: read {items.Cards.Count + items.Rows.Count} items from {seenTokens.Count + 1} pages");
        return items;
    }

    /// <summary>
    /// A silently expired session still gets HTTP 200, with a "sign in" prompt
    /// instead of the library. Reading that as an empty library made the sync
    /// wipe every like, playlist, subscription and saved album.
    /// </summary>
    private static void ThrowIfSignedOut(BrowseResponse response, string browseId)
    {
        if (response.ResponseContext?.LoggedIn != false) return;
        System.Diagnostics.Trace.WriteLine($"[Library] {browseId}: YouTube answered as signed out, session not recognised.");
        throw new SoundLibert.Domain.Exceptions.AccountSessionInvalidException(browseId);
    }

    /// <summary>Collects the first page and returns the next page's token.</summary>
    private static string? ReadFirstLibraryPage(BrowseResponse response, LibrarySectionItems items, string browseId)
    {
        var sections = response.Contents?.SingleColumnBrowseResultsRenderer?.Tabs?.FirstOrDefault()
                ?.TabRenderer?.Content?.SectionListRenderer?.Contents
            ?? response.Contents?.TwoColumnBrowseResultsRenderer?.SecondaryContents?.SectionListRenderer?.Contents
            ?? response.Contents?.SectionListRenderer?.Contents;

        string? token = null;
        var found = false;
        foreach (var section in (sections ?? new List<SectionContent>())
                     .SelectMany(s => s.ItemSectionRenderer?.Contents ?? new List<SectionContent> { s }))
        {
            if (section.GridRenderer is { } grid)
            {
                found = true;
                items.Add(grid.Items);
                token ??= GridToken(grid.Continuations, grid.Items);
            }
            else if (section.MusicShelfRenderer is { } shelf)
            {
                found = true;
                items.Add(shelf.Contents);
                token ??= ShelfToken(shelf.Continuations, shelf.Contents);
            }
            else if (section.MusicPlaylistShelfRenderer is { } playlist)
            {
                found = true;
                items.Add(playlist.Contents);
                token ??= ShelfToken(playlist.Continuations, playlist.Contents);
            }
        }

        if (!found)
            throw new SoundLibert.Domain.Exceptions.AccountLibraryUnrecognizedException(browseId, "no list in the answer");
        return token;
    }

    /// <summary>Collects a continuation page, in every shape YouTube returns it, and returns the next token.</summary>
    private static string? ReadNextLibraryPage(BrowseResponse page, LibrarySectionItems items, string browseId)
    {
        var next = page.ContinuationContents;
        if (next?.GridContinuation is { } grid)
        {
            items.Add(grid.Items);
            return GridToken(grid.Continuations, grid.Items);
        }
        if ((next?.MusicShelfContinuation ?? next?.MusicPlaylistShelfContinuation) is { } shelf)
        {
            items.Add(shelf.Contents);
            return ShelfToken(shelf.Continuations, shelf.Contents);
        }

        var appended = page.OnResponseReceivedActions
            ?.Select(a => a.AppendContinuationItemsAction?.ContinuationItems)
            .Where(entries => entries != null)
            .ToList();
        if (appended is { Count: > 0 })
        {
            string? token = null;
            foreach (var entry in appended.SelectMany(entries => entries!))
            {
                if (entry.MusicTwoRowItemRenderer != null) items.Cards.Add(entry.MusicTwoRowItemRenderer);
                else if (entry.MusicResponsiveListItemRenderer != null) items.Rows.Add(entry.MusicResponsiveListItemRenderer);
                else token ??= entry.ContinuationItemRenderer?.ContinuationEndpoint?.ContinuationCommand?.Token;
            }
            return token;
        }

        throw new SoundLibert.Domain.Exceptions.AccountLibraryUnrecognizedException(browseId, "a later page could not be read");
    }

    /// <summary>
    /// The browsable entries of a library section, whether YouTube sends them as
    /// cards or as list rows (the same section can come either way).
    /// </summary>
    private static IEnumerable<(string Id, string Title, string Thumbnail, RunsText? Subtitle)> LibraryEntries(LibrarySectionItems items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in items.Cards)
        {
            var id = card.NavigationEndpoint?.BrowseEndpoint?.BrowseId;
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
            yield return (id, card.Title?.FirstText() ?? string.Empty,
                card.ThumbnailRenderer?.GetThumbnailUrl() ?? string.Empty, card.Subtitle);
        }
        foreach (var row in items.Rows)
        {
            var id = row.NavigationEndpoint?.BrowseEndpoint?.BrowseId;
            // A song row carries a videoId; a collection row does not.
            if (string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(row.PlaylistItemData?.VideoId)) continue;
            var title = row.FlexColumns?.FirstOrDefault()?.MusicResponsiveListItemFlexColumnRenderer?.Text?.FirstText();
            if (string.IsNullOrWhiteSpace(title) || !seen.Add(id)) continue;
            yield return (id, title, row.Thumbnail?.MusicThumbnailRenderer?.GetThumbnailUrl() ?? string.Empty,
                row.FlexColumns?.ElementAtOrDefault(1)?.MusicResponsiveListItemFlexColumnRenderer?.Text);
        }
    }

    /// <summary>The complete set of liked songs on the account (see <see cref="ReadLibrarySectionAsync"/>).</summary>
    public async Task<IReadOnlyList<SongItem>> GetAccountLikedSongsAsync(CancellationToken ct = default)
    {
        var items = await ReadLibrarySectionAsync("FEmusic_liked_videos", ct);
        var songs = new List<SongItem>();
        foreach (var row in items.Rows)
            ParseMusicListItem(row, songs, new(), new(), new());
        return DedupeById(songs);
    }

    // ---- Pagination helpers ----

    private static string? FirstShelfContinuation(BrowseResponse response)
    {
        var contents = response.Contents?.TwoColumnBrowseResultsRenderer
                ?.SecondaryContents?.SectionListRenderer?.Contents
            ?? response.Contents?.SingleColumnBrowseResultsRenderer?.Tabs?.FirstOrDefault()
                ?.TabRenderer?.Content?.SectionListRenderer?.Contents;

        if (contents == null) return null;
        foreach (var c in contents)
        {
            var token = ShelfToken(c.MusicPlaylistShelfRenderer?.Continuations, c.MusicPlaylistShelfRenderer?.Contents)
                        ?? ShelfToken(c.MusicShelfRenderer?.Continuations, c.MusicShelfRenderer?.Contents);
            if (!string.IsNullOrEmpty(token)) return token;
        }
        return null;
    }

    /// <summary>Follows a music (playlist/shelf) continuation until exhausted.</summary>
    private async Task<List<SongItem>> PaginateShelfAsync(string token, CancellationToken ct)
    {
        var songs = new List<SongItem>();
        var guard = 0;
        string? current = token;

        while (!string.IsNullOrEmpty(current) && guard++ < 60)
        {
            ct.ThrowIfCancellationRequested();

            BrowseResponse response;
            try
            {
                response = await _client.BrowseAsync(
                    YouTubeClient.WEB_REMIX, continuation: current, setLogin: true, ct: ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                break; // network hiccup — return what we already have
            }

            var (page, next) = ReadContinuationPage(response);
            if (page == null) break;
            songs.AddRange(page);
            current = next;
        }

        return songs;
    }

    /// <summary>
    /// Reads one continuation page in BOTH shapes YouTube returns: the classic
    /// <c>continuationContents.musicPlaylistShelfContinuation</c> and the
    /// <c>onResponseReceivedActions[].appendContinuationItemsAction</c> form
    /// (large playlists; ignoring it silently capped lists at ~100 tracks).
    /// Returns (null, _) only when neither shape is present.
    /// </summary>
    private (List<SongItem>? Songs, string? Token) ReadContinuationPage(BrowseResponse response)
    {
        var cc = response.ContinuationContents;
        var shelf = cc?.MusicPlaylistShelfContinuation ?? cc?.MusicShelfContinuation;
        if (shelf?.Contents is { } items)
        {
            var songs = new List<SongItem>();
            foreach (var item in items)
                if (item.MusicResponsiveListItemRenderer != null)
                    ParseMusicListItem(item.MusicResponsiveListItemRenderer, songs, new(), new(), new());

            return (songs, ShelfToken(shelf.Continuations, items));
        }

        var actions = response.OnResponseReceivedActions;
        if (actions != null)
        {
            var songs = new List<SongItem>();
            string? next = null;
            var found = false;
            foreach (var action in actions)
            {
                var entries = action.AppendContinuationItemsAction?.ContinuationItems;
                if (entries == null) continue;
                found = true;
                foreach (var entry in entries)
                {
                    if (entry.MusicResponsiveListItemRenderer != null)
                        ParseMusicListItem(entry.MusicResponsiveListItemRenderer, songs, new(), new(), new());
                    else if (next == null)
                        next = entry.ContinuationItemRenderer?.ContinuationEndpoint?.ContinuationCommand?.Token;
                }
            }
            if (found) return (songs, next);
        }

        return (null, null);
    }

    private static List<SongItem> DedupeById(List<SongItem> songs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SongItem>(songs.Count);
        foreach (var song in songs)
            if (!string.IsNullOrEmpty(song.Id) && seen.Add(song.Id))
                result.Add(song);
        return result;
    }

    public async Task<IReadOnlyList<PlaylistItem>> GetAccountPlaylistsAsync(CancellationToken ct = default)
    {
        var items = await ReadLibrarySectionAsync("FEmusic_liked_playlists", ct);
        var playlists = new List<PlaylistItem>();
        foreach (var (id, title, thumbnail, subtitle) in LibraryEntries(items))
        {
            if (IsAutoGeneratedList(id)) continue;
            playlists.Add(new PlaylistItem
            {
                Id = id,
                Title = title,
                ThumbnailUrl = thumbnail,
                Author = subtitle?.FirstText()
            });
        }
        return playlists;
    }

    /// <summary>
    /// YouTube Music's own lists: "Liked Music" (the app shows it as the built-in
    /// Liked Songs), "Episodes for later" and library shortcuts. Decided by id:
    /// a user's own playlist may well be called "Liked Songs" or "New playlist".
    /// </summary>
    private static bool IsAutoGeneratedList(string browseId)
    {
        var id = browseId.Trim();
        return id.Equals("LM", StringComparison.OrdinalIgnoreCase)
            || id.Equals("VLLM", StringComparison.OrdinalIgnoreCase)
            || id.Equals("SE", StringComparison.OrdinalIgnoreCase)
            || id.Equals("VLSE", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("FE", StringComparison.Ordinal);
    }

    public async Task<IReadOnlyList<AlbumItem>> GetAccountAlbumsAsync(CancellationToken ct = default)
    {
        var items = await ReadLibrarySectionAsync("FEmusic_liked_albums", ct);
        return LibraryEntries(items)
            .Select(e => new AlbumItem
            {
                Id = e.Id,
                Title = e.Title,
                Artists = ExtractAlbumArtists(e.Subtitle),
                ThumbnailUrl = e.Thumbnail
            })
            .ToList();
    }

    public async Task<IReadOnlyList<ArtistItem>> GetAccountArtistsAsync(CancellationToken ct = default)
    {
        var items = await ReadLibrarySectionAsync("FEmusic_library_corpus_artists", ct);
        return LibraryEntries(items)
            .Select(e => new ArtistItem { Id = e.Id, Name = e.Title, ThumbnailUrl = e.Thumbnail })
            .ToList();
    }

    public async Task<IReadOnlyList<SongItem>> GetRelatedTracksAsync(string videoId, CancellationToken ct = default)
    {
        return await GetRadioAsync(videoId, ct);
    }

    /// <summary>
    /// YouTube Music "start radio" / up-next queue for a seed track (port of
    /// Velune YouTube.next). Used by Smart Queue ("Continue the vibe") as the
    /// real source of similar tracks instead of a fabricated similarity score.
    /// </summary>
    public async Task<IReadOnlyList<SongItem>> GetRadioAsync(string videoId, CancellationToken ct = default)
    {
        var songs = new List<SongItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(videoId)) return songs;

        try
        {
            await _client.EnsureVisitorDataAsync(ct);

            // Page 0 asks `next` for the seed's radio; each response may expose
            // an "automix" playlist id that yields the rest of the queue.
            string? nextVideoId = videoId;
            string? playlistId = null;

            for (int page = 0; page < 4; page++)
            {
                var response = await _client.NextAsync(
                    YouTubeClient.WEB_REMIX, nextVideoId, playlistId, ct: ct);

                var panel = response.Contents?.SingleColumn?.TabbedRenderer?.WatchNext?.Tabs
                    ?.Select(t => t.TabRenderer?.Content?.MusicQueueRenderer?.Content?.PlaylistPanelRenderer)
                    .FirstOrDefault(p => p?.Contents is { Count: > 0 });

                if (panel?.Contents == null) break;

                foreach (var item in panel.Contents)
                {
                    var song = ParsePanelItem(item.PlaylistPanelVideoRenderer);
                    if (song != null && seen.Add(song.Id)) songs.Add(song);
                }

                var automix = panel.Contents
                    .Select(c => c.AutomixPreviewVideoRenderer?.Content?
                        .AutomixPlaylistVideoRenderer?.NavigationEndpoint?.WatchPlaylistEndpoint)
                    .LastOrDefault(e => e != null);

                if (string.IsNullOrEmpty(automix?.PlaylistId)) break;

                nextVideoId = null;
                playlistId = automix!.PlaylistId;
                await Task.Delay(120, ct);

                if (songs.Count >= 40) break;
            }

            System.Diagnostics.Debug.WriteLine($"[Radio] {songs.Count} tracks for seed {videoId}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Radio] failed for {videoId}: {ex.Message}");
        }

        return songs;
    }

    private static SongItem? ParsePanelItem(PlaylistPanelVideoRenderer? r)
    {
        if (string.IsNullOrEmpty(r?.VideoId)) return null;

        var title = r.Title?.FirstText() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title)) return null;

        var byline = r.LongBylineText ?? r.ShortBylineText;
        var artists = byline.ExtractArtists();

        string? albumTitle = null;
        var segments = byline?.Runs?.SplitBySeparator();
        if (segments is { Count: >= 2 })
        {
            var album = segments[1].FirstOrDefault()?.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(album) && !LooksLikeTime(album))
                albumTitle = album;
        }

        var duration = ThumbnailExtensions.ParseTime(r.LengthText?.FirstText() ?? "");
        var thumbnail = r.Thumbnail?.Thumbnails?.LastOrDefault()?.Url ?? string.Empty;
        var explicitBadge = r.Badges?.Any(b =>
            b.MusicInlineBadgeRenderer?.Icon?.IconType == "MUSIC_EXPLICIT_BADGE") == true;

        return new SongItem
        {
            Id = r.VideoId!,
            Title = title,
            Artists = artists,
            AlbumTitle = albumTitle,
            DurationSeconds = duration,
            ThumbnailUrl = thumbnail,
            IsExplicit = explicitBadge
        };
    }

    private static bool LooksLikeTime(string text)
        => text.Length is >= 3 and <= 8 && text.Contains(':');

    private static readonly HttpClient _probeHttpClient = new()
    {
        // No Timeout = infinite wait. A stalled googlevideo connection used to
        // hang stream resolution forever ("Отримання стріму…" stuck).
        Timeout = TimeSpan.FromSeconds(15)
    };

    public async Task<StreamInfo?> GetStreamInfoAsync(string videoId, string? playlistId = null, int audioQuality = 0, CancellationToken ct = default)
    {
        // Hard cap: resolution must never hang forever. 14 clients x slow
        // network would otherwise look like a freeze.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(90));
        var limitedCt = cts.Token;

        // Anonymous auth bootstrap (port of Velune App.kt/YouTube init):
        // without real visitorData YouTube withholds stream URLs.
        await _client.EnsureVisitorDataAsync(limitedCt);

        // Client order is evidence-based (verified live 2026-09-13):
        // VISIONOS is currently the only anonymous client returning direct
        // audio URLs. Web clients return signatureCipher URLs (need a JS
        // decipher engine — not implemented yet, so they are fallbacks).
        // ANDROID-family clients either require login or return URL-less
        // metadata-only formats.
        var clients = new[]
        {
            YouTubeClient.VISIONOS,
            YouTubeClient.MWEB,
            YouTubeClient.WEB_REMIX,
            YouTubeClient.ANDROID_VR_NO_AUTH,
            YouTubeClient.IOS_MUSIC,
            YouTubeClient.ANDROID,
            YouTubeClient.ANDROID_MUSIC,
            YouTubeClient.ANDROID_UNPLUGGED,
            YouTubeClient.TVHTML5,
            YouTubeClient.TVHTML5_SIMPLY_EMBEDDED_PLAYER,
            YouTubeClient.ANDROID_CREATOR,
            YouTubeClient.ANDROID_TESTSUITE,
            YouTubeClient.IPADOS,
            YouTubeClient.IOS,
        };

        // Why each client failed: one line in the log, not one line per client.
        var failures = new List<string>();

        foreach (var client in clients)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[StreamResolver] Trying client: {client.ClientName} ({client.ClientVersion}) for videoId={videoId}");

                int? signatureTimestamp = null;
                if (client.UseSignatureTimestamp)
                    signatureTimestamp = await _client.GetSignatureTimestampAsync(videoId, limitedCt);

                var response = await _client.PlayerAsync(
                    client, videoId, playlistId, signatureTimestamp, ct: limitedCt);

                System.Diagnostics.Debug.WriteLine($"[StreamResolver] {client.ClientName} status: {response.PlayabilityStatus?.Status}");
                if (response.PlayabilityStatus?.Status != "OK")
                {
                    System.Diagnostics.Debug.WriteLine($"[StreamResolver] {client.ClientName} reason: {response.PlayabilityStatus?.Reason}");
                    failures.Add($"{client.ClientName}: {response.PlayabilityStatus?.Status ?? "no status"} {Shorten(response.PlayabilityStatus?.Reason)}".TrimEnd());
                    continue;
                }

                var audioFormats = response.StreamingData?.AdaptiveFormats
                    ?.Where(f => f.MimeType?.StartsWith("audio/") == true && f.Bitrate > 0)
                    .ToList();

                System.Diagnostics.Debug.WriteLine($"[StreamResolver] {client.ClientName} audio formats: {audioFormats?.Count ?? 0}");

                // Prefer Opus-in-WebM over AAC-in-MP4 among directly playable
                // formats: this machine's MediaFoundation stack reports full
                // duration but yields zero samples for YouTube's fragmented
                // AAC (proven on captured bytes: valid fMP4, mp4a AAC-LC,
                // decoder present, 0 samples out), so an AAC pick plays as an
                // instant skip. Bitrate still decides within a codec — normal
                // tracks (Opus on top by bitrate anyway) are unaffected.
                // Data saver (audioQuality 1) takes the LOWEST bitrate instead.
                var ordered = audioFormats
                    ?.OrderByDescending(f => f.Url != null ? 1 : 0)
                    .ThenByDescending(f => IsOpusAudio(f.MimeType) ? 1 : 0);
                var format = (audioQuality == 1
                        ? ordered?.ThenBy(f => f.Bitrate)
                        : ordered?.ThenByDescending(f => f.Bitrate))
                    ?.FirstOrDefault();

                if (format != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[StreamResolver] {client.ClientName} best: bitrate={format.Bitrate}, mime={format.MimeType}, hasUrl={format.Url != null}, hasCipher={format.SignatureCipher != null || format.Cipher != null}");
                }

                if (format?.Url == null)
                {
                    if (format?.SignatureCipher != null || format?.Cipher != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[StreamResolver] {client.ClientName} only ciphered formats (JS decipher not implemented), trying next client...");
                    }
                    failures.Add($"{client.ClientName}: {(format == null ? "no audio formats" : "ciphered only")}");
                    continue;
                }

                // Port of Velune YTPlayerUtils: patch cver to the client version
                // we actually used, append GVS poToken for web clients.
                var url = StreamClientUtils.PatchClientVersion(format.Url, client.ClientVersion);
                if (StreamClientUtils.IsWebClient(client.ClientName) && _client.VisitorData != null)
                {
                    var gvsToken = PoTokenGenerator.GenerateContentToken(_client.VisitorData, videoId);
                    url = StreamClientUtils.AppendPoToken(url, gvsToken);
                }
                // The VLC audio slave (Video mode) uses this URL verbatim; NAudio
                // adds ratebypass itself, so this is idempotent there.
                url = WithRateBypass(url);

                // Port of Velune YTPlayerUtils.validateStatus: HTTP Range probe
                // with per-client headers resolved from the URL's `c` param.
                if (!await ValidateStreamUrlAsync(url, client, limitedCt))
                {
                    System.Diagnostics.Debug.WriteLine($"[StreamResolver] {client.ClientName} stream validation failed, trying next client...");
                    failures.Add($"{client.ClientName}: stream rejected");
                    continue;
                }

                System.Diagnostics.Debug.WriteLine($"[StreamResolver] {client.ClientName} stream validated.");
                // The first client normally answers; a fallback working is the
                // early warning that it stopped.
                if (failures.Count > 0)
                    System.Diagnostics.Trace.WriteLine(
                        $"[StreamResolver] {videoId} resolved by {client.ClientName} after: {string.Join("; ", failures)}");
                return new StreamInfo
                {
                    Url = url,
                    MimeType = format.MimeType,
                    Bitrate = format.Bitrate,
                    ExpiresInSeconds = response.StreamingData?.ExpiresInSeconds ?? 0,
                    LoudnessDb = response.PlayerConfig?.AudioConfig?.LoudnessDb
                        ?? format.LoudnessDb ?? 0,
                    AudioQuality = format.AudioQuality,
                    ClientName = client.ClientName
                };
            }
            catch (OperationCanceledException) when (limitedCt.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Our 90s cap fired — stop trying clients, report failure.
                System.Diagnostics.Trace.WriteLine(
                    $"[StreamResolver] {videoId} timed out after: {string.Join("; ", failures)}");
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StreamResolver] {client.ClientName} exception: {ex.Message}");
                if (limitedCt.IsCancellationRequested) return null;
                failures.Add($"{client.ClientName}: {Shorten(ex.Message)}");
                continue;
            }
        }

        System.Diagnostics.Trace.WriteLine(
            $"[StreamResolver] No playable stream for {videoId}: {string.Join("; ", failures)}");
        return null;
    }

    private static string Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var line = text.Trim().ReplaceLineEndings(" ");
        return line.Length <= 80 ? line : line[..80] + "...";
    }

    /// <inheritdoc cref="IMusicProvider.GetStreamHeaders" />
    public Dictionary<string, string> GetStreamHeaders(string streamUrl, string? fallbackClientName = null)
    {
        var urlClient = StreamClientUtils.GetClientParamFromUrl(streamUrl) ?? fallbackClientName;
        return StreamClientUtils.GetStreamHeaders(urlClient);
    }

    /// <summary>
    /// Resolves a direct <b>video</b> stream URL for a track (used by the
    /// fullscreen Canvas background — a short muted loop). VISIONOS returns
    /// direct, un-ciphered video URLs (verified), so no JS decipher is needed.
    /// </summary>
    private readonly Dictionary<string, (StreamInfo Info, DateTime At)> _videoCache = new(StringComparer.Ordinal);
    private readonly object _videoCacheLock = new();
    private static readonly TimeSpan VideoCacheTtl = TimeSpan.FromMinutes(90);

    private readonly Dictionary<string, (TrackVersions? Versions, DateTime At)> _versionsCache = new(StringComparer.Ordinal);
    private readonly object _versionsCacheLock = new();

    private readonly Dictionary<string, long> _viewCountCache = new(StringComparer.Ordinal);
    private readonly object _viewCountCacheLock = new();

    private readonly Dictionary<string, (List<SongItem> Videos, DateTime At)> _artistVideosCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _artistVideosCacheLock = new();
    private static readonly TimeSpan ArtistVideosCacheTtl = TimeSpan.FromHours(12);
    private static readonly TimeSpan VersionsCacheTtl = TimeSpan.FromMinutes(60);

    /// <inheritdoc cref="IMusicProvider.GetViewCountAsync" />
    public async Task<long?> GetViewCountAsync(string videoId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoId)) return null;

        lock (_viewCountCacheLock)
            if (_viewCountCache.TryGetValue(videoId, out var cached)) return cached;

        // A metadata-only player request returns videoDetails.viewCount. Try the
        // clients that reliably answer without a decipher (same order as clip
        // verification); a transport failure moves on, it never fails the row.
        foreach (var client in new[] { YouTubeClient.VISIONOS, YouTubeClient.MWEB, YouTubeClient.WEB_REMIX })
        {
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(8));
                var response = await _client.PlayerAsync(client, videoId, null, null, null, attemptCts.Token);
                var raw = response.VideoDetails?.ViewCount;
                if (!string.IsNullOrWhiteSpace(raw) && long.TryParse(raw, out var views) && views > 0)
                {
                    lock (_viewCountCacheLock) _viewCountCache[videoId] = views;
                    return views;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Views] resolve failed ({client.ClientName}) {videoId}: {ex.Message}");
            }
        }
        return null;
    }

    public async Task<StreamInfo?> GetVideoStreamInfoAsync(string videoId, bool highQuality = false, int targetHeight = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoId)) return null;

        // Quality is part of the key: the Canvas wants ~480p and Video mode the
        // user's chosen height, and both must not reuse each other's URL.
        // "Auto" (targetHeight 0, highQuality) now means ~720p: VLC decodes in
        // software here (frame callbacks), so 1080p stuttered on weaker PCs.
        // The Canvas is a muted, dimmed background: 360p downloads and
        // software-decodes far faster than 480p and looks the same under the scrim.
        var wanted = targetHeight > 0 ? targetHeight : (highQuality ? 720 : 360);
        var cacheKey = $"{videoId}|{wanted}";

        lock (_videoCacheLock)
        {
            // Stream URLs carry an expiry, so old cache entries must not be
            // reused (an expired URL renders as a black Canvas).
            if (_videoCache.TryGetValue(cacheKey, out var cached))
            {
                if (DateTime.UtcNow - cached.At < VideoCacheTtl) return cached.Info;
                _videoCache.Remove(cacheKey);
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        var limitedCt = cts.Token;

        await _client.EnsureVisitorDataAsync(limitedCt);

        var clients = new[]
        {
            YouTubeClient.VISIONOS,
            YouTubeClient.ANDROID_VR_NO_AUTH,
            YouTubeClient.IOS,
            YouTubeClient.IPADOS,
            YouTubeClient.ANDROID,
            YouTubeClient.MWEB,
            YouTubeClient.WEB_REMIX,
        };

        foreach (var client in clients)
        {
            try
            {
                int? signatureTimestamp = null;
                if (client.UseSignatureTimestamp)
                    signatureTimestamp = await _client.GetSignatureTimestampAsync(videoId, limitedCt);

                var response = await _client.PlayerAsync(client, videoId, null, signatureTimestamp, ct: limitedCt);
                if (response.PlayabilityStatus?.Status != "OK") continue;

                var videos = response.StreamingData?.AdaptiveFormats?
                    .Where(f => f.MimeType?.StartsWith("video/") == true && f.Url != null)
                    .ToList();
                if (videos is not { Count: > 0 }) continue;

                // Always prefer VLC-friendly h264/avc1 first (a plain "mp4" filter
                // can pick an AV1 track some software decoders refuse → black
                // picture). Then: the Canvas (muted, dimmed background) wants a
                // SMALL ~480p frame so software decode keeps up at scene cuts,
                // while Video mode wants a full-size frame (the user's choice).
                var format = videos
                    .OrderByDescending(f => f.MimeType!.Contains("avc1", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenBy(f => Math.Abs((f.Height > 0 ? f.Height : wanted) - wanted))
                    .ThenBy(f => f.Bitrate)
                    .First();

                var url = StreamClientUtils.PatchClientVersion(format.Url!, client.ClientVersion);
                // VLC gets this URL verbatim; ratebypass stops the CDN throttle.
                url = WithRateBypass(url);

                // Validate with a Range probe before handing the URL to VLC —
                // an unvalidated 403 otherwise just renders as a black Canvas
                // and the next client is never tried.
                if (!await ValidateStreamUrlAsync(url, client, limitedCt))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[VideoStream] {client.ClientName} validation failed, trying next client...");
                    continue;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[VideoStream] {client.ClientName} itag={format.Itag} {format.MimeType}");

                var info = new StreamInfo
                {
                    Url = url,
                    MimeType = format.MimeType,
                    Bitrate = format.Bitrate,
                    ClientName = client.ClientName
                };

                lock (_videoCacheLock)
                {
                    // Evict the oldest entry instead of clearing the whole cache:
                    // a full Clear() forced re-resolution of the currently playing
                    // fullscreen/Video stream.
                    if (_videoCache.Count >= 64)
                    {
                        var oldest = _videoCache.OrderBy(kv => kv.Value.At).First().Key;
                        _videoCache.Remove(oldest);
                    }
                    _videoCache[cacheKey] = (info, DateTime.UtcNow);
                }
                return info;
            }
            catch (OperationCanceledException) when (limitedCt.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                System.Diagnostics.Trace.WriteLine($"[VideoStream] timed out for videoId={videoId}");
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VideoStream] {client.ClientName} failed: {ex.Message}");
            }
        }

        System.Diagnostics.Trace.WriteLine($"[VideoStream] all clients failed for videoId={videoId}");
        return null;
    }

    /// <summary>
    /// Resolves the two versions of a track: the song (ATV audio track) and its
    /// music video. The video is found by <see cref="FindClipAsync"/>, which accepts
    /// only uploads that pass a check of their own player data: the artist's or a
    /// vouched-for label's channel, never a fan's; a wide frame of a plausible
    /// length; a real clip title. Results are cached per song id.
    /// </summary>
    public async Task<TrackVersions?> GetTrackVersionsAsync(SongItem song, CancellationToken ct = default)
    {
        if (song is null || string.IsNullOrWhiteSpace(song.Id)) return null;
        if (song.IsLocal) return null;

        var videoId = song.Id;

        lock (_versionsCacheLock)
        {
            if (_versionsCache.TryGetValue(videoId, out var cached))
            {
                if (DateTime.UtcNow - cached.At < VersionsCacheTtl) return cached.Versions;
                _versionsCache.Remove(videoId);
            }
        }

        TrackVersions versions;
        try
        {
            await _client.EnsureVisitorDataAsync(ct);

            var (candidates, videoCandidates, anySearchOk) = await GatherSearchCandidatesAsync(song, ct);

            // Every candidate search failed (offline / DNS / server error): this
            // is NOT a "no clip" answer. Throw so the caller keeps the cache
            // clean and re-discovers on the next online play.
            if (!anySearchOk)
                throw new InvalidOperationException("clip discovery: all searches failed");

            // The playing item may itself be the official video — e.g. a lyric
            // video, or a restored queue row whose VideoType was not stored. If
            // YouTube tags the seed id as OMV, treat it as the video version.
            var seedIsVideo = ClipTitleRules.IsRealMusicVideoTitle(song.Title)
                && (IsMusicVideoType(song.VideoType)
                    || videoCandidates.Any(c => c.Id == song.Id && c.VideoType == "MUSIC_VIDEO_TYPE_OMV"));

            if (seedIsVideo)
            {
                // Already the video: find the matching song so the Song/Video
                // toggle works both ways.
                versions = new TrackVersions
                {
                    VideoId = videoId,
                    SongId = await FindOfficialSongIdAsync(candidates, song, ct),
                    CurrentIsVideo = true
                };
            }
            else
            {
                // The clip: from the artist's page and the Videos searches, checked
                // one by one against its own player data (InnerTubeMusicProvider.Clips.cs).
                versions = new TrackVersions
                {
                    SongId = videoId,
                    VideoId = await FindClipAsync(song, videoCandidates, ct),
                    CurrentIsVideo = false
                };
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Versions] failed for {videoId}: {ex.Message}");
            return null;
        }

        lock (_versionsCacheLock)
        {
            if (_versionsCache.Count >= 512) _versionsCache.Clear();
            _versionsCache[videoId] = (versions, DateTime.UtcNow);
        }

        System.Diagnostics.Debug.WriteLine(
            $"[Versions] {videoId} → song={versions.SongId} video={versions.VideoId} currentIsVideo={versions.CurrentIsVideo}");

        return versions;
    }

    public async Task<IReadOnlyList<SongItem>> GetQueueAsync(
        IReadOnlyList<string>? videoIds = null, string? playlistId = null, CancellationToken ct = default)
    {
        var songs = new List<SongItem>();
        try
        {
            var response = await _client.GetQueueAsync(
                YouTubeClient.WEB_REMIX, videoIds, string.IsNullOrWhiteSpace(playlistId) ? null : WithVl(playlistId), ct);

            foreach (var data in response.QueueDatas ?? new List<QueueData>())
            {
                var song = ParsePanelItem(data.Content?.PlaylistPanelVideoRenderer);
                if (song != null) songs.Add(song);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Queue] music/get_queue failed: {ex.Message}");
        }

        return DedupeById(songs);
    }

    private static bool IsMusicVideoType(string? type)
        => type is "MUSIC_VIDEO_TYPE_OMV" or "MUSIC_VIDEO_TYPE_UGC";

    /// <summary>True for Opus-in-WebM adaptive formats (reliably decodable here).</summary>
    private static bool IsOpusAudio(string? mimeType)
        => mimeType?.Contains("opus", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Picks the matching song (ATV audio track, or an untagged row) so the
    /// Song/Video toggle works in both directions. Verified like clips: a
    /// wrong audio twin would make Song mode play a different song.
    /// </summary>
    private async Task<string?> FindOfficialSongIdAsync(
        List<SongItem> candidates, SongItem video, CancellationToken ct)
    {
        var ranked = candidates
            .Where(c => c.Id != video.Id
                        && !IsMusicVideoType(c.VideoType)
                        && ClipTitleRules.IsRealMusicVideoTitle(c.Title, video.Title))
            .Select(c => (Candidate: c, Score: ScoreCandidate(video, c, preferVideo: false)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(3)
            .ToList();

        foreach (var (candidate, _) in ranked)
        {
            if (await VerifyCounterpartAsync(video, candidate, ct, expectSong: true))
                return candidate.Id;
        }
        return null;
    }

    /// <summary>
    /// Deep-verifies that <paramref name="candidate"/> really is the given
    /// song's counterpart. Verification strategy, learned the hard way:
    /// <list type="bullet">
    /// <item>Metadata is fetched from the first client that answers (VISIONOS,
    /// then MWEB, then WEB_REMIX) — a single throttled client must not veto
    /// every clip.</item>
    /// <item>A TRANSPORT failure verifies nothing → fail OPEN (accept + log).
    /// Offline/bot-fight then behaves like the old code instead of empty shelves.</item>
    /// <item>Only POSITIVE mismatches reject: unplayable, wrong title, neither
    /// the channel author nor the attributed row artists match, absurd duration.</item>
    /// <item>The author gate accepts the label channel too (official clips
    /// often live on Fueled By Ramen / Atlantic / …) as long as the search row
    /// still attributes the artist.</item>
    /// </list>
    /// </summary>
    private async Task<bool> VerifyCounterpartAsync(SongItem song, SongItem candidate, CancellationToken ct, bool checkArtist = true, bool strictAuthor = false, bool allowOfficialNonClip = false, bool expectSong = false)
    {
        PlayerResponse? response = null;
        foreach (var client in new[] { YouTubeClient.VISIONOS, YouTubeClient.MWEB, YouTubeClient.WEB_REMIX })
        {
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(10));
                response = await _client.PlayerAsync(client, candidate.Id, null, null, null, attemptCts.Token);
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Versions] verify transport failed ({client.ClientName}): {ex.Message}");
            }
        }
        if (response == null)
        {
            System.Diagnostics.Debug.WriteLine($"[Versions] unverifiable {candidate.Id} — accepting");
            return true;
        }
        if (response.PlayabilityStatus?.Status != "OK")
        {
            System.Diagnostics.Debug.WriteLine($"[Versions] reject {candidate.Id}: not playable");
            return false;
        }

        var details = response.VideoDetails;
        if (details == null || !string.Equals(details.VideoId, candidate.Id, StringComparison.Ordinal))
        {
            System.Diagnostics.Debug.WriteLine($"[Versions] reject {candidate.Id}: no video details");
            return false;
        }

        if (!ClipTitleRules.TitlesMatch(song.Title, details.Title))
        {
            System.Diagnostics.Debug.WriteLine($"[Versions] reject {candidate.Id}: title mismatch (\"{details.Title}\")");
            return false;
        }

        // Auto-generated "Artist - Topic" channels host still-image uploads,
        // never real clips — even with a clean title and matching length.
        // For a SONG lookup (clip → song) the Topic upload IS the official
        // audio, so it must be accepted there.
        // (Matched on the raw "X - Topic" suffix so an artist actually NAMED
        // Topic can never false-fail on their own channel.)
        if (!expectSong && (details.Author ?? string.Empty).Trim().EndsWith("- Topic", StringComparison.OrdinalIgnoreCase))
        {
            System.Diagnostics.Debug.WriteLine($"[Versions] reject {candidate.Id}: topic channel (\"{details.Author}\")");
            return false;
        }

        // The search-row title passed the markers, but the player title can
        // still reveal a live cut ("Unplugged", "Live at …") — re-check here.
        // An empty player title verifies nothing, so it rejects too. When the
        // caller allows an official non-clip (last-resort tier), only hard
        // junk (karaoke/cover/remix/…) is rejected — an "Official Lyric Video"
        // or "Official Audio" from the artist's own channel is accepted.
        if (string.IsNullOrWhiteSpace(details.Title) ||
            (allowOfficialNonClip
                ? ClipTitleRules.IsRejectedUploadTitle(details.Title, song.Title)
                : !ClipTitleRules.IsRealMusicVideoTitle(details.Title, song.Title)))
        {
            System.Diagnostics.Debug.WriteLine($"[Versions] reject {candidate.Id}: non-clip title (\"{details.Title}\")");
            return false;
        }

        var primary = song.Artists.FirstOrDefault()?.Name ?? song.ArtistDisplay.Split(',')[0].Trim();
        var wantArtist = NormName(primary);
        // Skipped for cached pins (see VerifyVideoPinAsync): without the
        // original search row, label-hosted official clips would false-fail.
        if (checkArtist)
        {
            var authorOk = wantArtist.Length >= 2 && NormName(details.Author).Contains(wantArtist);
            var rowArtistOk = wantArtist.Length >= 2 && candidate.Artists.Any(a =>
            {
                var n = NormName(a.Name ?? string.Empty);
                return n.Contains(wantArtist) || wantArtist.Contains(n);
            });
            // UGC uploads attributed to the artist are usually fan covers with
            // a clean title — row attribution alone is not enough for them,
            // the channel itself must be the artist's.
            if ((!authorOk && !rowArtistOk) || (strictAuthor && !authorOk))
            {
                System.Diagnostics.Debug.WriteLine($"[Versions] reject {candidate.Id}: artist mismatch (author \"{details.Author}\")");
                return false;
            }
        }

        if (int.TryParse(details.LengthSeconds, out var length) && length > 0)
        {
            // No official single clip runs 15+ minutes — full concerts and
            // long mixes are rejected even when the duration is unknown.
            if (length > 900)
            {
                System.Diagnostics.Debug.WriteLine($"[Versions] reject {candidate.Id}: too long ({length}s) for a clip");
                return false;
            }
            // Relative window, not absolute seconds: MV intros/outros shift
            // the length, but a real counterpart stays within 0.7–2.2x.
            if (song.DurationSeconds > 0)
            {
                var ratio = (double)length / song.DurationSeconds;
                if (ratio < 0.7 || ratio > 2.2)
                {
                    System.Diagnostics.Debug.WriteLine($"[Versions] reject {candidate.Id}: duration ratio {ratio:F2} ({length}s)");
                    return false;
                }
            }
        }

        return true;
    }

    /// <inheritdoc cref="IMusicProvider.VerifyVideoPinAsync" />
    public async Task<bool> VerifyVideoPinAsync(SongItem song, string videoId, CancellationToken ct = default)
    {
        if (song is null || string.IsNullOrWhiteSpace(videoId)) return false;
        // A clip played as its own video is its own pin.
        if (string.Equals(song.Id, videoId, StringComparison.Ordinal)) return true;
        var known = await KnowSongAsync(song, ct);
        var response = await PlayerForCheckAsync(videoId, ct);
        if (response == null) return true; // could not check: keep the pin
        var check = JudgeClip(known, ArtistNamesOf(known), ArtistChannelsOf(known), videoId, rowType: null, pinned: true, response);
        if (check.Verdict != ClipVerdict.Accept)
            System.Diagnostics.Trace.WriteLine($"[Clip] saved clip {videoId} for \"{song.Title}\" fails: {check.Note}");
        return check.Verdict == ClipVerdict.Accept;
    }

    /// <inheritdoc cref="ClipTitleRules.Norm"/>
    private static string NormName(string? s) => ClipTitleRules.Norm(s);

    /// <summary>
    /// Gathers search candidates for version discovery. All three searches run
    /// <b>concurrently</b> and always. <c>Videos</c> is the union of the two
    /// Videos-filter passes: those rows are guaranteed VIDEO uploads, so only
    /// they may ever be pinned as a clip — the Songs pass is used solely to
    /// find the audio twin (pinning an audio row would make Video mode play
    /// audio). Returns both the full pool and the video-only subset.
    /// </summary>
    private async Task<(List<SongItem> All, List<SongItem> Videos, bool AnySearchOk)> GatherSearchCandidatesAsync(
        SongItem song, CancellationToken ct)
    {
        var clean = CleanSearchTitle(song.Title);
        var artist = song.ArtistDisplay;

        var videosTask = SearchSongsSafeAsync($"{song.Title} {artist}".Trim(), SearchFilter.Videos, ct);
        var songsTask = SearchSongsSafeAsync($"{clean} {artist}".Trim(), SearchFilter.Songs, ct);
        var officialTask = SearchSongsSafeAsync($"{clean} {artist} official video".Trim(), SearchFilter.Videos, ct);
        var lyricTask = SearchSongsSafeAsync($"{clean} {artist} official audio".Trim(), SearchFilter.Videos, ct);
        await Task.WhenAll(videosTask, songsTask, officialTask, lyricTask);

        static List<SongItem> Dedupe(IEnumerable<SongItem> src) => src
            .Where(c => !string.IsNullOrEmpty(c.Id))
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        var v = await videosTask;
        var s = await songsTask;
        var o = await officialTask;
        var l = await lyricTask;
        var anyOk = v.Ok || s.Ok || o.Ok || l.Ok;

        var videos = Dedupe(v.Songs.Concat(o.Songs).Concat(l.Songs));
        var all = Dedupe(videos.Concat(s.Songs));
        return (all, videos, anyOk);
    }

    /// <summary>
    /// Reads video rows from the artist page's Videos shelves (carousels and
    /// flat shelves). Browse id may be a channel (UC…) or artist page (MPRE…)
    /// id; when absent or unusable, an exact-name Artists search resolves it
    /// first. Junk (karaoke/cover/remix/…) is dropped; lyric/audio/live rows
    /// are KEPT (they are the official fallback tier). Cached per artist.
    /// </summary>
    private async Task<List<SongItem>> GetArtistChannelVideosAsync(
        string? artistBrowseId, string artistName, CancellationToken ct)
    {
        var key = artistName.Trim();
        lock (_artistVideosCacheLock)
        {
            if (_artistVideosCache.TryGetValue(key, out var cached))
            {
                if (DateTime.UtcNow - cached.At < ArtistVideosCacheTtl) return cached.Videos;
                _artistVideosCache.Remove(key);
            }
        }

        var videos = new List<SongItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var readOk = true;

        void Add(string? id, string? title, string? thumb)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) return;
            if (ClipTitleRules.IsRejectedUploadTitle(title)) return;
            if (!seen.Add(id)) return;
            videos.Add(new SongItem
            {
                Id = id,
                Title = title,
                Artists = new[] { new Artist { Name = artistName } },
                // The channel shelf often sends no picture; the video id always has one.
                ThumbnailUrl = string.IsNullOrWhiteSpace(thumb) ? $"https://i.ytimg.com/vi/{id}/hqdefault.jpg" : thumb
            });
        }

        try
        {
            var browseId = artistBrowseId;
            if (string.IsNullOrWhiteSpace(browseId))
            {
                var found = await SearchAsync(key, SearchFilter.Artists, ct);
                browseId = found.Artists
                    .FirstOrDefault(a => string.Equals(a.Name, key, StringComparison.OrdinalIgnoreCase)
                                         && !string.IsNullOrWhiteSpace(a.Id))
                    ?.Id;
            }
            if (!string.IsNullOrWhiteSpace(browseId))
            {
                var response = await _client.BrowseAsync(YouTubeClient.WEB_REMIX, browseId, ct: ct);
                var contents = GetBrowseContents(response);
                if (contents != null)
                {
                    foreach (var content in contents)
                    {
                        var carousel = content.MusicCarouselShelfRenderer;
                        if (carousel != null)
                        {
                            var shelfTitle = carousel.Header?.MusicCarouselShelfBasicHeaderRenderer?.Title?.FirstText() ?? "";
                            if (!shelfTitle.Contains("video", StringComparison.OrdinalIgnoreCase)) continue;
                            foreach (var entry in carousel.Contents ?? new List<CarouselContent>())
                            {
                                var r = entry.MusicTwoRowItemRenderer;
                                Add(r?.NavigationEndpoint?.WatchEndpoint?.VideoId,
                                    r?.Title?.FirstText(),
                                    r?.ThumbnailRenderer?.GetThumbnailUrl());
                            }
                        }
                        else if (content.MusicShelfRenderer?.Contents is { } shelf)
                        {
                            var parsed = new List<SongItem>();
                            foreach (var item in shelf)
                                if (item.MusicResponsiveListItemRenderer != null)
                                    ParseMusicListItem(item.MusicResponsiveListItemRenderer, parsed, new(), new(), new());
                            foreach (var p in parsed)
                                Add(p.Id, p.Title, p.ThumbnailUrl);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Versions] artist videos browse failed for \"{key}\": {ex.Message}");
            readOk = false;
        }

        if (readOk)
        {
            lock (_artistVideosCacheLock)
            {
                if (_artistVideosCache.Count >= 128) _artistVideosCache.Clear();
                _artistVideosCache[key] = (videos, DateTime.UtcNow);
            }
        }
        return videos;
    }

    private async Task<(List<SongItem> Songs, bool Ok)> SearchSongsSafeAsync(
        string query, SearchFilter filter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return (new List<SongItem>(), false);
        try
        {
            var result = await SearchAsync(query, filter, ct);
            return (result.Songs.ToList(), true);
        }
        catch (Exception ex)
        {
            // Ok=false marks a transport failure (offline / DNS / 5xx). Clip
            // discovery must tell this apart from a genuine "no clip" so a
            // failed lookup never poisons the negative cache.
            System.Diagnostics.Debug.WriteLine($"[Versions] search failed for \"{query}\": {ex.Message}");
            return (new List<SongItem>(), false);
        }
    }

    /// <summary>Heuristic match score (0 = reject) for a search candidate.</summary>
    private static int ScoreCandidate(SongItem song, SongItem candidate, bool preferVideo)
    {
        if (string.IsNullOrWhiteSpace(candidate.Title)) return 0;

        var cand = candidate.Title.ToLowerInvariant();
        var candNormal = CleanSearchTitle(candidate.Title).ToLowerInvariant();
        var titleClean = CleanSearchTitle(song.Title).ToLowerInvariant();
        var titleRaw = song.Title.ToLowerInvariant();
        var artist = song.ArtistDisplay.ToLowerInvariant();

        int score = 0;
        if (titleClean.Length > 0 && cand.Contains(titleClean)) score += 24;
        else if (cand.Contains(titleRaw)) score += 18;
        else return 0; // the title must be present, otherwise it is a different track

        // The official music video is normally titled exactly the song title;
        // lyric/visualiser/cover uploads append qualifiers ("Artist - Song (Lyrics)").
        if (candNormal == titleClean || candNormal == titleRaw) score += 22;

        if (!string.IsNullOrWhiteSpace(artist))
        {
            var primary = artist.Split(',')[0].Trim();
            var inTitle = cand.Contains(primary);
            var inArtists = candidate.Artists.Any(a => a.Name.ToLowerInvariant().Contains(primary));
            if (inTitle || inArtists) score += 12;
            else score -= 4;
        }

        if (preferVideo && candidate.VideoType == "MUSIC_VIDEO_TYPE_OMV") score += 12;

        // Popularity: the canonical music video massively outviews its variants.
        score += candidate.ViewCount switch
        {
            >= 1_000_000_000 => 28,
            >= 100_000_000 => 24,
            >= 10_000_000 => 20,
            >= 1_000_000 => 15,
            >= 100_000 => 9,
            _ => 0
        };

        // Reject uploads that are not the music video itself.
        if (!ClipTitleRules.IsRealMusicVideoTitle(candidate.Title, song.Title)) score -= 30;

        // Duration is only a tiebreaker: a music video is often longer than
        // the album track (intro/outro), so a mismatch must not outweigh
        // relevance or popularity. A near-exact match is a small bonus.
        if (song.DurationSeconds > 0 && candidate.DurationSeconds > 0)
        {
            var diff = Math.Abs(song.DurationSeconds - candidate.DurationSeconds);
            if (diff <= 15) score += 3;
            else if (diff <= 25) score += 1;
            else if (diff > 180) score -= 8;
        }

        return score;
    }

    /// <summary>Parses "1.1B views" / "723M views" from a Video row subtitle.</summary>
    private static long ParseViewCount(RunsText? subtitle)
    {
        if (subtitle?.Runs == null) return 0;

        var text = string.Concat(subtitle.Runs.Select(r => r.Text));
        var match = System.Text.RegularExpressions.Regex.Match(
            text, @"([\d.,]+)\s*([KMB])?\s*(?:views|plays)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return 0;

        if (!double.TryParse(
                match.Groups[1].Value.Replace(",", string.Empty),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            return 0;
        }

        var multiplier = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "B" => 1_000_000_000d,
            "M" => 1_000_000d,
            "K" => 1_000d,
            _ => 1d
        };
        return (long)(value * multiplier);
    }

    /// <inheritdoc cref="ClipTitleRules.Clean"/>
    private static string CleanSearchTitle(string title) => ClipTitleRules.Clean(title);

    /// <summary>
    /// Port of Velune YTPlayerUtils.validateStatus: probes the stream URL with
    /// HTTP Range requests. A 403 (or other failure) means the URL will not
    /// play and the next fallback client must be tried.
    /// </summary>
    private static async Task<bool> ValidateStreamUrlAsync(string url, YouTubeClient client, CancellationToken ct)
    {
        try
        {
            var clientParam = StreamClientUtils.GetClientParamFromUrl(url) ?? client.ClientName;
            var userAgent = StreamClientUtils.ResolveUserAgent(clientParam);
            var (origin, referer) = StreamClientUtils.ResolveOriginReferer(clientParam);

            var ranges = StreamClientUtils.IsWebClient(clientParam)
                ? new[] { "bytes=0-0", "bytes=262144-262145", "bytes=1048576-1048577" }
                : new[] { "bytes=0-0" };

            foreach (var range in ranges)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                request.Headers.TryAddWithoutValidation("Range", range);
                if (origin != null) request.Headers.TryAddWithoutValidation("Origin", origin);
                if (referer != null) request.Headers.TryAddWithoutValidation("Referer", referer);

                using var response = await _probeHttpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);
                var code = (int)response.StatusCode;
                if (code == 403) return false;
                if ((code < 200 || code > 399) && code != 416) return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StreamResolver] validation exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Playlist ids arrive either bare ("PL…"/"OLAK5uy_…") or already prefixed
    /// with "VL" (as YouTube Music returns in browse results). Prepending "VL"
    /// unconditionally produced "VLVL…" and an empty playlist.
    /// </summary>
    private static string WithVl(string playlistId)
        => playlistId.StartsWith("VL", StringComparison.Ordinal) ? playlistId : "VL" + playlistId;

    /// <summary>
    /// Appends <c>ratebypass=yes</c> so the CDN does not throttle bulk transfers.
    /// VLC streams the URL directly and never sets this, so without it the video
    /// stalls/rebuffers mid-play (the NAudio path already adds it itself — this is
    /// idempotent there). Also applied to the DASH audio slave in Video mode.
    /// </summary>
    private static string WithRateBypass(string url)
        => url.Contains("ratebypass=", StringComparison.OrdinalIgnoreCase)
            ? url
            : url + (url.Contains('?') ? "&" : "?") + "ratebypass=yes";

    /// <summary>Album and playlist pages now carry their header in the first tab.</summary>
    private MusicResponsiveHeaderRenderer? GetBrowseHeader(BrowseResponse response)
    {
        return response.Header?.MusicResponsiveHeaderRenderer
            ?? response.Contents?.TwoColumnBrowseResultsRenderer?.Tabs
                ?.SelectMany(t => t.TabRenderer?.Content?.SectionListRenderer?.Contents ?? new List<SectionContent>())
                .Select(c => c.MusicResponsiveHeaderRenderer)
                .FirstOrDefault(h => h != null);
    }

    private List<SectionContent>? GetBrowseContents(BrowseResponse response)
    {
        return response.Contents?.SingleColumnBrowseResultsRenderer?.Tabs?.FirstOrDefault()
            ?.TabRenderer?.Content?.SectionListRenderer?.Contents
            ?? response.Contents?.SectionListRenderer?.Contents;
    }

    private List<SongItem> ParsePlaylistSongs(BrowseResponse response)
    {
        var songs = new List<SongItem>();
        var contents = response.Contents?.TwoColumnBrowseResultsRenderer
                ?.SecondaryContents?.SectionListRenderer?.Contents
            ?? response.Contents?.SingleColumnBrowseResultsRenderer?.Tabs?.FirstOrDefault()
                ?.TabRenderer?.Content?.SectionListRenderer?.Contents;

        if (contents == null) return songs;

        foreach (var content in contents)
        {
            var rows = content.MusicPlaylistShelfRenderer?.Contents ?? content.MusicShelfRenderer?.Contents;
            if (rows == null) continue;
            foreach (var item in rows)
            {
                if (item.MusicResponsiveListItemRenderer != null)
                    ParseMusicListItem(item.MusicResponsiveListItemRenderer, songs, new(), new(), new());
            }
        }

        return songs;
    }

    private static readonly System.Text.RegularExpressions.Regex PlayCountText = new(
        @"^[\d.,]+\s*[KMB]?\s*(?:views?|plays?)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private void ParseMusicListItem(
        MusicResponsiveListItemRenderer renderer,
        List<SongItem> songs,
        List<AlbumItem> albums,
        List<ArtistItem> artists,
        List<PlaylistItem> playlists)
    {
        var videoId = renderer.PlaylistItemData?.VideoId
            ?? renderer.NavigationEndpoint?.WatchEndpoint?.VideoId;
        var browseId = renderer.NavigationEndpoint?.BrowseEndpoint?.BrowseId;

        var columns = renderer.FlexColumns;
        if (columns == null || columns.Count < 2) return;

        var title = columns[0].MusicResponsiveListItemFlexColumnRenderer?.Text?.FirstText() ?? "";
        var subtitleRuns = columns[1].MusicResponsiveListItemFlexColumnRenderer?.Text;
        var thumbnail = renderer.Thumbnail?.GetThumbnailUrl() ?? string.Empty;
        var isExplicit = renderer.Badges?.Any(b =>
            b.MusicInlineBadgeRenderer?.Icon?.IconType == "MUSIC_EXPLICIT_BADGE") == true;

        var artistsList = subtitleRuns.ExtractArtists();

        // Search rows: [artists] • [album?] • [duration]. The middle segment
        // (when present) is the album name — needed for Music DNA top albums.
        string? albumTitle = null;
        var segments = subtitleRuns?.Runs?.SplitBySeparator();
        if (segments is { Count: >= 3 })
        {
            // A Videos row has the view count there ("209M views"), not an album.
            var middle = segments[1].FirstOrDefault()?.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(middle) && !PlayCountText.IsMatch(middle)) albumTitle = middle;
        }

        // Artist pages and playlists keep the album in a column of its own, after the plays.
        if (albumTitle == null && columns.Count >= 3)
        {
            var last = columns[^1].MusicResponsiveListItemFlexColumnRenderer?.Text?.FullText()?.Trim();
            if (!string.IsNullOrWhiteSpace(last) && !PlayCountText.IsMatch(last) && !LooksLikeTime(last))
                albumTitle = last;
        }

        if (!string.IsNullOrEmpty(videoId))
        {
            // Search rows put duration at the end of the subtitle (" • 3:43").
            // Album rows put it in a separate fixed column instead.
            var subtitle = columns[1].MusicResponsiveListItemFlexColumnRenderer?.Text;
            var duration = subtitle.ExtractDurationSeconds();
            if (duration == 0)
            {
                var fixedText = renderer.FixedColumns?.FirstOrDefault()
                    ?.MusicResponsiveListItemFixedColumnRenderer?.Text?.FirstText();
                if (!string.IsNullOrWhiteSpace(fixedText))
                    duration = ThumbnailExtensions.ParseTime(fixedText);
            }

            songs.Add(new SongItem
            {
                Id = videoId,
                Title = title,
                Artists = artistsList,
                AlbumTitle = albumTitle,
                DurationSeconds = duration,
                ThumbnailUrl = thumbnail,
                IsExplicit = isExplicit,
                VideoType = ExtractVideoType(renderer, columns),
                // Artist pages keep the plays in a column of their own ("7B plays").
                ViewCount = columns.Skip(1)
                    .Select(c => ParseViewCount(c.MusicResponsiveListItemFlexColumnRenderer?.Text))
                    .FirstOrDefault(v => v > 0)
            });
        }
        else if (!string.IsNullOrEmpty(browseId))
        {
            var subtitleText = subtitleRuns?.FullText();
            if (browseId.StartsWith("UC"))
            {
                artists.Add(new ArtistItem { Id = browseId, Name = title, ThumbnailUrl = thumbnail, Subtitle = WithoutType(subtitleText) });
            }
            else if (browseId.StartsWith("MPREb"))
            {
                albums.Add(new AlbumItem
                {
                    Id = browseId,
                    Title = title,
                    // "Album • Artist • 2017": the first segment is the type, not an artist.
                    Artists = ExtractAlbumArtists(subtitleRuns),
                    ThumbnailUrl = thumbnail,
                    IsExplicit = isExplicit,
                    Subtitle = subtitleText,
                    Year = YearIn(subtitleRuns)
                });
            }
            else
            {
                playlists.Add(new PlaylistItem { Id = browseId, Title = title, ThumbnailUrl = thumbnail, Subtitle = subtitleText });
            }
        }
    }

    /// <summary>
    /// Reads YouTube Music's own video classification
    /// (<c>MUSIC_VIDEO_TYPE_ATV/OMV/UGC</c>) for a search/browse row. It lives on
    /// the title run's <c>navigationEndpoint</c> (and, for videos, on the item's
    /// own endpoint / play-button overlay).
    /// </summary>
    private static string? ExtractVideoType(MusicResponsiveListItemRenderer renderer, List<FlexColumn>? columns)
    {
        var direct = renderer.NavigationEndpoint?.WatchEndpoint?
            .WatchEndpointMusicSupportedConfigs?.WatchEndpointMusicConfig?.MusicVideoType;
        if (!string.IsNullOrEmpty(direct)) return direct;

        if (columns != null)
        {
            foreach (var column in columns)
            {
                var runs = column.MusicResponsiveListItemFlexColumnRenderer?.Text?.Runs;
                if (runs == null) continue;
                foreach (var run in runs)
                {
                    var type = run.NavigationEndpoint?.WatchEndpoint?
                        .WatchEndpointMusicSupportedConfigs?.WatchEndpointMusicConfig?.MusicVideoType;
                    if (!string.IsNullOrEmpty(type)) return type;
                }
            }
        }

        return null;
    }

    private YTItem? ParseTwoRowItem(MusicTwoRowItemRenderer renderer)
    {
        var videoId = renderer.NavigationEndpoint?.WatchEndpoint?.VideoId;
        var browseId = renderer.NavigationEndpoint?.BrowseEndpoint?.BrowseId;
        var title = renderer.Title?.FirstText() ?? "";
        var thumbnail = renderer.ThumbnailRenderer?.GetThumbnailUrl() ?? string.Empty;
        var artistsList = renderer.Subtitle.ExtractArtists();

        if (!string.IsNullOrEmpty(videoId))
        {
            return new SongItem { Id = videoId, Title = title, Artists = artistsList, ThumbnailUrl = thumbnail };
        }

        if (!string.IsNullOrEmpty(browseId))
        {
            return new AlbumItem
            {
                Id = browseId,
                Title = title,
                Artists = ExtractAlbumArtists(renderer.Subtitle),
                ThumbnailUrl = thumbnail,
                Subtitle = renderer.Subtitle?.FullText(),
                Year = YearIn(renderer.Subtitle)
            };
        }

        return null;
    }

    /// <summary>"Single • 2025": the year of a release, when its subtitle has one.</summary>
    private static string? YearIn(RunsText? subtitle) => subtitle?.Runs?
        .Select(r => r.Text.Trim())
        .LastOrDefault(t => t.Length == 4 && t.All(char.IsDigit) && t[0] is '1' or '2');

    /// <summary>"Artist • 535M monthly audience" without the first word.</summary>
    private static string? WithoutType(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var parts = text.Split(" • ", 2);
        return parts.Length == 2 && parts[0] is "Artist" or "Profile" ? parts[1] : text;
    }

    private static readonly HashSet<string> ReleaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Album", "Single", "EP", "Compilation", "Playlist", "Music video"
    };

    /// <summary>
    /// Album/playlist cards render their subtitle as "Album • Artist" (release
    /// type first), unlike song rows where the artist comes first. Pick the
    /// segment that carries an artist browse endpoint, else the second segment,
    /// and never treat the release-type token as an artist.
    /// </summary>
    private static List<Artist> ExtractAlbumArtists(RunsText? subtitle)
    {
        if (subtitle?.Runs == null) return new List<Artist>();

        var segments = subtitle.Runs.SplitBySeparator();
        var segment = segments.FirstOrDefault(s =>
                s.Any(r => r.NavigationEndpoint?.BrowseEndpoint?.BrowseId?.StartsWith("UC", StringComparison.Ordinal) == true))
            ?? segments.Skip(1).FirstOrDefault()
            ?? segments.FirstOrDefault();

        var artists = new List<Artist>();
        if (segment == null) return artists;

        for (int i = 0; i < segment.Count; i += 2)
        {
            var run = segment[i];
            var text = run.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (ReleaseTypes.Contains(text)) continue;
            if (LooksLikeTime(text)) continue;

            artists.Add(new Artist
            {
                Name = text,
                Id = run.NavigationEndpoint?.BrowseEndpoint?.BrowseId
            });
        }
        return artists;
    }

    private AlbumItem? ParseAlbumFromBrowse(BrowseResponse response, string browseId)
    {
        var header = GetBrowseHeader(response);
        if (header == null) return null;

        var title = header.Title?.FirstText() ?? "";
        var thumbnail = header.Thumbnail?.GetThumbnailUrl() ?? string.Empty;
        var year = header.Subtitle?.Runs?.LastOrDefault()?.Text;
        var playlistId = response.Microformat?.MicroformatDataRenderer?.UrlCanonical?.Split('=').LastOrDefault();
        var artistsList = header.StraplineTextOne.ExtractArtists();

        return new AlbumItem
        {
            Id = browseId,
            Title = title,
            PlaylistId = playlistId,
            Artists = artistsList,
            ThumbnailUrl = thumbnail,
            Year = year
        };
    }
}
