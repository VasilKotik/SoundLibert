using SoundLibert.Domain.Enums;
using SoundLibert.Domain.Models;
using SoundLibert.Domain.Video;
using SoundLibert.Infra.InnerTube;
using SoundLibert.Infra.InnerTube.Models;

namespace SoundLibert.Infra;

public partial class InnerTubeMusicProvider
{
    /// <inheritdoc cref="Domain.Interfaces.IMusicProvider.OpenArtistVideoFeed" />
    public IVideoFeed OpenArtistVideoFeed(string? artistBrowseId, string artistName)
        => new ArtistVideoFeed(this, artistBrowseId, artistName);

    /// <inheritdoc cref="Domain.Interfaces.IMusicProvider.OpenVideoSearchFeed" />
    public IVideoFeed OpenVideoSearchFeed(string query) => new VideoSearchFeed(this, query);

    /// <inheritdoc cref="Domain.Interfaces.IMusicProvider.GetNewVideosAsync" />
    public async Task<IReadOnlyList<SongItem>> GetNewVideosAsync(CancellationToken ct = default)
    {
        var response = await _client.BrowseAsync(
            YouTubeClient.WEB_REMIX, browseId: "FEmusic_new_releases_videos", setLogin: true, ct: ct);

        var videos = new List<SongItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var content in GetBrowseContents(response) ?? new List<SectionContent>())
        {
            foreach (var item in content.GridRenderer?.Items ?? new List<GridItem>())
                AddVideo(ReadVideoCard(item.MusicTwoRowItemRenderer), seen, videos);
            foreach (var item in content.MusicCarouselShelfRenderer?.Contents ?? new List<CarouselContent>())
                AddVideo(ReadVideoCard(item.MusicTwoRowItemRenderer), seen, videos);
        }
        return videos;
    }

    /// <summary>The artist's own upload, or a clip that names them ("Song (feat. Artist)").</summary>
    private static bool CreditsArtist(SongItem row, string? channelId, string wantedName) =>
        row.Artists.Any(a =>
            (!string.IsNullOrWhiteSpace(channelId) && string.Equals(a.Id, channelId, StringComparison.Ordinal)) ||
            (wantedName.Length > 0 && string.Equals(NormName(a.Name), wantedName, StringComparison.Ordinal))) ||
        (wantedName.Length >= 3 && NormName(row.Title).Contains(wantedName, StringComparison.Ordinal));

    /// <summary>A card of a video shelf or grid; null when it does not open a video.</summary>
    private static SongItem? ReadVideoCard(MusicTwoRowItemRenderer? card, string? artistName = null)
    {
        var watch = card?.NavigationEndpoint?.WatchEndpoint;
        if (card == null || string.IsNullOrWhiteSpace(watch?.VideoId)) return null;

        var artists = card.Subtitle.ExtractArtists();
        if (artists.Count == 0 && !string.IsNullOrWhiteSpace(artistName))
            artists.Add(new Artist { Name = artistName });

        return new SongItem
        {
            Id = watch.VideoId!,
            Title = card.Title?.FirstText() ?? string.Empty,
            Artists = artists,
            ThumbnailUrl = card.ThumbnailRenderer?.GetThumbnailUrl() ?? string.Empty,
            ViewCount = ParseViewCount(card.Subtitle),
            VideoType = watch.WatchEndpointMusicSupportedConfigs?.WatchEndpointMusicConfig?.MusicVideoType
        };
    }

    private static readonly System.Text.RegularExpressions.Regex ShortsTitle = new(
        @"#shorts?\b|[\[\(]\s*shorts?\s*[\]\)]",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Anything shorter is a Short or a teaser, not a music video.</summary>
    private const int ShortestVideoSeconds = 61;

    /// <summary>
    /// Adds a row to a page of videos unless it is junk (karaoke, cover, a Short) or already
    /// there: the same id, or the same title by the same artists (channels upload a clip more
    /// than once). Pinned to its own id, it plays as a video from every path and never
    /// resolves to something else.
    /// </summary>
    private static bool AddVideo(SongItem? video, HashSet<string> seen, List<SongItem> page)
    {
        if (video == null || string.IsNullOrWhiteSpace(video.Id) || string.IsNullOrWhiteSpace(video.Title)) return false;
        if (ClipTitleRules.IsRejectedUploadTitle(video.Title) || ShortsTitle.IsMatch(video.Title)) return false;
        if (video.DurationSeconds is > 0 and < ShortestVideoSeconds) return false;
        if (seen.Contains(video.Id)) return false;

        var title = NormName(video.Title);
        if (title.Length > 0 && !seen.Add($"title:{title}|{NormName(video.ArtistDisplay)}")) return false;
        seen.Add(video.Id);

        video.VideoVersionId = video.Id;
        video.SongVersionId = video.Id;
        // The channel shelf often sends no picture; the video id always has one.
        if (string.IsNullOrWhiteSpace(video.ThumbnailUrl))
            video.ThumbnailUrl = $"https://i.ytimg.com/vi/{video.Id}/hqdefault.jpg";
        page.Add(video);
        return true;
    }

    /// <summary>One Videos search, a page at a time. A failed request leaves it where it was.</summary>
    private sealed class VideoSearchPager(InnerTubeMusicProvider owner, string query)
    {
        private string? _token;

        public int Pages { get; private set; }

        public bool IsDone { get; private set; }

        public async Task<List<SongItem>> NextPageAsync(CancellationToken ct)
        {
            var rows = new List<SongItem>();
            string? next = null;

            if (Pages == 0)
            {
                var response = await owner._client.SearchAsync(YouTubeClient.WEB_REMIX, query, VideosFilterParams, ct: ct);
                var sections = response.Contents?.TabbedSearchResultsRenderer?.Tabs
                    ?.SelectMany(t => t.TabRendererInner?.Content?.SectionListRenderer?.Contents
                                      ?? Enumerable.Empty<SectionContent>())
                    ?? Enumerable.Empty<SectionContent>();
                foreach (var section in sections)
                {
                    if (section.MusicShelfRenderer is not { Contents: not null } shelf) continue;
                    ReadRows(shelf.Contents, rows);
                    next ??= ShelfToken(shelf.Continuations, shelf.Contents);
                }
            }
            else
            {
                var response = await owner._client.SearchAsync(YouTubeClient.WEB_REMIX, continuation: _token, ct: ct);
                var shelf = response.ContinuationContents?.MusicPlaylistShelfContinuation
                            ?? response.ContinuationContents?.MusicShelfContinuation;
                if (shelf?.Contents != null)
                {
                    ReadRows(shelf.Contents, rows);
                    next = ShelfToken(shelf.Continuations, shelf.Contents);
                }
            }

            Pages++;
            _token = next;
            IsDone = string.IsNullOrEmpty(next);
            return rows;
        }

        private void ReadRows(List<MusicShelfContent> contents, List<SongItem> rows)
        {
            foreach (var item in contents)
                if (item.MusicResponsiveListItemRenderer != null)
                    owner.ParseMusicListItem(item.MusicResponsiveListItemRenderer, rows, new(), new(), new());
        }
    }

    /// <summary>A Videos search as a feed (concerts, sessions: whatever a query asks for).</summary>
    private sealed class VideoSearchFeed(InnerTubeMusicProvider owner, string query) : IVideoFeed
    {
        private const int MaxPages = 10;

        private readonly VideoSearchPager _pager = new(owner, query);
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public bool HasMore => !_pager.IsDone && _pager.Pages < MaxPages;

        public async Task<IReadOnlyList<SongItem>> NextPageAsync(CancellationToken ct = default)
        {
            var page = new List<SongItem>();
            if (!HasMore) return page;
            foreach (var row in await _pager.NextPageAsync(ct)) AddVideo(row, _seen, page);
            return page;
        }
    }

    /// <summary>
    /// Everything from an artist's own channel, nearest first: the Videos and Live shelves
    /// of the artist page (about ten each), the full Videos list behind the shelf's "More"
    /// (up to 150), then Videos searches kept to rows of this channel. Where it stands
    /// moves only after a request succeeded, so a failed page is simply asked again.
    /// </summary>
    private sealed class ArtistVideoFeed : IVideoFeed
    {
        private const int RequestsPerPage = 4;
        private const int SearchPagesPerQuery = 8;
        private const int FruitlessPagesToStop = 3;
        private const int LongestVideoSeconds = 3 * 60 * 60;

        private enum Stage { ArtistPage, VideoList, Search, Done }

        private readonly InnerTubeMusicProvider _owner;
        private readonly string _artistName;
        private readonly string _wantedName;
        private readonly string[] _queries;
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SongItem> _read = new(StringComparer.Ordinal);

        private string? _channelId;
        private Stage _stage = Stage.ArtistPage;
        private BrowseEndpoint? _list;
        private string? _listToken;
        private int _query;
        private VideoSearchPager? _search;
        private int _fruitlessPages;

        public ArtistVideoFeed(InnerTubeMusicProvider owner, string? channelId, string artistName)
        {
            _owner = owner;
            _channelId = channelId;
            _artistName = artistName.Trim();
            _wantedName = NormName(_artistName);
            _queries = new[] { _artistName, $"{_artistName} official video", $"{_artistName} live" };
            if (_artistName.Length == 0) _stage = Stage.Done;
        }

        public bool HasMore => _stage != Stage.Done;

        /// <summary>
        /// Shelf cards carry no length. When the same video comes again in a list that has
        /// it, the card already on the page gets it.
        /// </summary>
        private bool Add(SongItem? row, List<SongItem> page)
        {
            if (row == null) return false;
            if (_read.TryGetValue(row.Id, out var earlier))
            {
                if (earlier.DurationSeconds == 0) earlier.DurationSeconds = row.DurationSeconds;
                return false;
            }
            if (!AddVideo(row, _seen, page)) return false;
            _read[row.Id] = row;
            return true;
        }

        public async Task<IReadOnlyList<SongItem>> NextPageAsync(CancellationToken ct = default)
        {
            var page = new List<SongItem>();
            for (var request = 0; request < RequestsPerPage && page.Count == 0 && HasMore; request++)
            {
                ct.ThrowIfCancellationRequested();
                switch (_stage)
                {
                    case Stage.ArtistPage: await ReadArtistPageAsync(page, ct); break;
                    case Stage.VideoList: await ReadVideoListAsync(page, ct); break;
                    case Stage.Search: await ReadSearchPageAsync(page, ct); break;
                }
            }
            return page;
        }

        private async Task ReadArtistPageAsync(List<SongItem> page, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_channelId))
            {
                var found = await _owner.SearchAsync(_artistName, SearchFilter.Artists, ct);
                _channelId = found.Artists
                    .FirstOrDefault(a => string.Equals(a.Name, _artistName, StringComparison.OrdinalIgnoreCase)
                                         && !string.IsNullOrWhiteSpace(a.Id))
                    ?.Id;
                if (string.IsNullOrWhiteSpace(_channelId))
                {
                    _stage = Stage.Search;
                    return;
                }
            }

            var response = await _owner._client.BrowseAsync(YouTubeClient.WEB_REMIX, _channelId, ct: ct);
            foreach (var content in _owner.GetBrowseContents(response) ?? new List<SectionContent>())
            {
                if (content.MusicCarouselShelfRenderer is not { } carousel) continue;

                var header = carousel.Header?.MusicCarouselShelfBasicHeaderRenderer;
                var title = header?.Title?.FirstText() ?? string.Empty;
                var isVideos = title.Contains("video", StringComparison.OrdinalIgnoreCase);
                if (!isVideos && !title.Contains("live", StringComparison.OrdinalIgnoreCase)
                              && !title.Contains("performance", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var entry in carousel.Contents ?? new List<CarouselContent>())
                    if (ReadVideoCard(entry.MusicTwoRowItemRenderer, _artistName) is { } card && BelongsToArtist(card))
                        Add(card, page);

                // The Live shelf's "More" answers with nothing readable; the Videos one
                // is a real list.
                if (isVideos) _list ??= header?.MoreContentButton?.ButtonRenderer?.NavigationEndpoint?.BrowseEndpoint;
            }

            _stage = _list != null ? Stage.VideoList : Stage.Search;
        }

        private async Task ReadVideoListAsync(List<SongItem> page, CancellationToken ct)
        {
            List<SongItem>? rows;
            string? next;
            if (_listToken == null)
            {
                var response = await _owner._client.BrowseAsync(
                    YouTubeClient.WEB_REMIX, _list!.BrowseId, _list.Params, ct: ct);
                rows = _owner.ParsePlaylistSongs(response);
                next = FirstShelfContinuation(response);
            }
            else
            {
                var response = await _owner._client.BrowseAsync(
                    YouTubeClient.WEB_REMIX, continuation: _listToken, ct: ct);
                (rows, next) = _owner.ReadContinuationPage(response);
            }

            foreach (var row in rows ?? new List<SongItem>())
                if (BelongsToArtist(row))
                    Add(row, page);

            _listToken = next;
            if (string.IsNullOrEmpty(next)) _stage = Stage.Search;
        }

        private async Task ReadSearchPageAsync(List<SongItem> page, CancellationToken ct)
        {
            _search ??= new VideoSearchPager(_owner, _queries[_query]);
            var rows = await _search.NextPageAsync(ct);

            var added = 0;
            foreach (var row in rows)
                if (IsOwnUpload(row) && row.DurationSeconds <= LongestVideoSeconds && Add(row, page))
                    added++;
            _fruitlessPages = added > 0 ? 0 : _fruitlessPages + 1;

            if (_search.IsDone || _search.Pages >= SearchPagesPerQuery || _fruitlessPages >= FruitlessPagesToStop)
            {
                _search = null;
                _fruitlessPages = 0;
                if (++_query >= _queries.Length) _stage = Stage.Done;
            }
        }

        /// <summary>
        /// For what YouTube itself lists on the artist's page. A real artist's lists hold
        /// their channel and clips that credit them ("Song (feat. Artist)"), but a made-up
        /// "artist" ("Relaxing Spa Music") gets other people's uploads there.
        /// </summary>
        private bool BelongsToArtist(SongItem row) => CreditsArtist(row, _channelId, _wantedName);

        /// <summary>
        /// For search rows: by channel id; by exact channel name when the row carries none.
        /// There, a title that merely mentions the artist is a fan upload.
        /// </summary>
        private bool IsOwnUpload(SongItem row) => row.Artists.Any(a =>
            (!string.IsNullOrWhiteSpace(_channelId) && string.Equals(a.Id, _channelId, StringComparison.Ordinal)) ||
            (_wantedName.Length > 0 && string.Equals(NormName(a.Name), _wantedName, StringComparison.Ordinal)));
    }
}
