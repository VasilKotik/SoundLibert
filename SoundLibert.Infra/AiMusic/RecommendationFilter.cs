using SoundLibert.Domain.Enums;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;
using SoundLibert.Domain.Video;

namespace SoundLibert.Infra.AiMusic;

/// <summary>
/// The catalogue as the app sees it, with AI-generated acts kept out of what is
/// recommended: Home, charts, mood pages, radio and autoplay, what sounds like a song,
/// fans-also-like. What the listener asks for by name stays whole: search, an album, a
/// playlist, an artist's own page, their library.
/// </summary>
public sealed class RecommendationFilter : IMusicProvider
{
    private readonly IMusicProvider _inner;
    private readonly IAiMusicFilter _ai;

    public RecommendationFilter(IMusicProvider inner, IAiMusicFilter ai)
    {
        _inner = inner;
        _ai = ai;
    }

    // ---- Recommendations: filtered ----

    public async Task<HomePage> GetHomePageAsync(string? chipParams = null, CancellationToken ct = default)
    {
        var page = await _inner.GetHomePageAsync(chipParams, ct).ConfigureAwait(false);
        if (!await ReadyAsync(ct).ConfigureAwait(false)) return page;
        page.Shelves = Keep(page.Shelves);
        return page;
    }

    public async Task<HomePage> GetHomeContinuationAsync(string token, CancellationToken ct = default)
    {
        var page = await _inner.GetHomeContinuationAsync(token, ct).ConfigureAwait(false);
        if (!await ReadyAsync(ct).ConfigureAwait(false)) return page;
        page.Shelves = Keep(page.Shelves);
        return page;
    }

    public async Task<ExplorePage> GetExplorePageAsync(CancellationToken ct = default)
    {
        var page = await _inner.GetExplorePageAsync(ct).ConfigureAwait(false);
        if (!await ReadyAsync(ct).ConfigureAwait(false)) return page;
        page.NewReleases = KeepOrNull(page.NewReleases);
        page.Trending = KeepOrNull(page.Trending);
        page.NewVideos = KeepOrNull(page.NewVideos);
        return page;
    }

    public async Task<ShelfPage> GetShelfPageAsync(string browseId, string? @params = null, CancellationToken ct = default)
    {
        var page = await _inner.GetShelfPageAsync(browseId, @params, ct).ConfigureAwait(false);
        if (!await ReadyAsync(ct).ConfigureAwait(false)) return page;
        return new ShelfPage { Title = page.Title, Shelves = Keep(page.Shelves) };
    }

    public async Task<IReadOnlyList<Shelf>> GetChartsAsync(string country, CancellationToken ct = default)
    {
        var shelves = await _inner.GetChartsAsync(country, ct).ConfigureAwait(false);
        return await ReadyAsync(ct).ConfigureAwait(false) ? Keep(shelves) : shelves;
    }

    public async Task<SongRelated> GetSongRelatedAsync(string videoId, CancellationToken ct = default)
    {
        var related = await _inner.GetSongRelatedAsync(videoId, ct).ConfigureAwait(false);
        if (!await ReadyAsync(ct).ConfigureAwait(false)) return related;
        return new SongRelated { Shelves = Keep(related.Shelves), About = related.About };
    }

    public async Task<CollectionExtras> GetCollectionExtrasAsync(string browseId, CancellationToken ct = default)
    {
        var extras = await _inner.GetCollectionExtrasAsync(browseId, ct).ConfigureAwait(false);
        if (!await ReadyAsync(ct).ConfigureAwait(false)) return extras;
        return new CollectionExtras { Shelves = Keep(extras.Shelves), Artist = extras.Artist };
    }

    /// <summary>An artist's own music stays; only who "fans also like" is a recommendation.</summary>
    public async Task<ArtistPage> GetArtistPageAsync(string browseId, CancellationToken ct = default)
    {
        var page = await _inner.GetArtistPageAsync(browseId, ct).ConfigureAwait(false);
        if (await ReadyAsync(ct).ConfigureAwait(false))
            page.RelatedArtists = page.RelatedArtists.Where(a => !_ai.IsAi(a)).ToList();
        return page;
    }

    public async Task<IReadOnlyList<SongItem>> GetRelatedTracksAsync(string videoId, CancellationToken ct = default)
        => await KeepAsync(_inner.GetRelatedTracksAsync(videoId, ct), ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<SongItem>> GetRadioAsync(string videoId, CancellationToken ct = default)
        => await KeepAsync(_inner.GetRadioAsync(videoId, ct), ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<SongItem>> GetNewVideosAsync(CancellationToken ct = default)
        => await KeepAsync(_inner.GetNewVideosAsync(ct), ct).ConfigureAwait(false);

    // ---- What the listener asked for: whole ----

    public Task<SearchResult> SearchAsync(string query, SearchFilter filter = SearchFilter.Songs, CancellationToken ct = default)
        => _inner.SearchAsync(query, filter, ct);

    public Task<SearchSuggestions> GetSuggestionsAsync(string query, CancellationToken ct = default)
        => _inner.GetSuggestionsAsync(query, ct);

    public Task<SearchOverview> SearchOverviewAsync(string query, CancellationToken ct = default)
        => _inner.SearchOverviewAsync(query, ct);

    public Task<AlbumItem?> GetAlbumAsync(string browseId, CancellationToken ct = default)
        => _inner.GetAlbumAsync(browseId, ct);

    public Task<IReadOnlyList<SongItem>> GetAlbumSongsAsync(string playlistId, CancellationToken ct = default)
        => _inner.GetAlbumSongsAsync(playlistId, ct);

    public Task<IReadOnlyList<SongItem>> GetAlbumTracksAsync(string albumBrowseId, CancellationToken ct = default)
        => _inner.GetAlbumTracksAsync(albumBrowseId, ct);

    public Task<ArtistItem?> GetArtistAsync(string browseId, CancellationToken ct = default)
        => _inner.GetArtistAsync(browseId, ct);

    public Task<ArtistPage> GetArtistPreviewAsync(string browseId, CancellationToken ct = default)
        => _inner.GetArtistPreviewAsync(browseId, ct);

    public Task<PlaylistItem?> GetPlaylistAsync(string playlistId, CancellationToken ct = default)
        => _inner.GetPlaylistAsync(playlistId, ct);

    public Task<IReadOnlyList<SongItem>> GetPlaylistSongsAsync(string playlistId, CancellationToken ct = default)
        => _inner.GetPlaylistSongsAsync(playlistId, ct);

    public Task<IReadOnlyList<MoodCategory>> GetMoodsAndGenresAsync(CancellationToken ct = default)
        => _inner.GetMoodsAndGenresAsync(ct);

    public Task<IReadOnlyList<SongItem>> GetAccountLikedSongsAsync(CancellationToken ct = default)
        => _inner.GetAccountLikedSongsAsync(ct);

    public Task<IReadOnlyList<PlaylistItem>> GetAccountPlaylistsAsync(CancellationToken ct = default)
        => _inner.GetAccountPlaylistsAsync(ct);

    public Task<IReadOnlyList<AlbumItem>> GetAccountAlbumsAsync(CancellationToken ct = default)
        => _inner.GetAccountAlbumsAsync(ct);

    public Task<IReadOnlyList<ArtistItem>> GetAccountArtistsAsync(CancellationToken ct = default)
        => _inner.GetAccountArtistsAsync(ct);

    public IVideoFeed OpenArtistVideoFeed(string? artistBrowseId, string artistName)
        => _inner.OpenArtistVideoFeed(artistBrowseId, artistName);

    public IVideoFeed OpenVideoSearchFeed(string query) => _inner.OpenVideoSearchFeed(query);

    public Task<StreamInfo?> GetStreamInfoAsync(string videoId, string? playlistId = null, int audioQuality = 0, CancellationToken ct = default)
        => _inner.GetStreamInfoAsync(videoId, playlistId, audioQuality, ct);

    public Dictionary<string, string> GetStreamHeaders(string streamUrl, string? fallbackClientName = null)
        => _inner.GetStreamHeaders(streamUrl, fallbackClientName);

    public Task<StreamInfo?> GetVideoStreamInfoAsync(string videoId, bool highQuality = false, int targetHeight = 0, CancellationToken ct = default)
        => _inner.GetVideoStreamInfoAsync(videoId, highQuality, targetHeight, ct);

    public Task<long?> GetViewCountAsync(string videoId, CancellationToken ct = default)
        => _inner.GetViewCountAsync(videoId, ct);

    public Task<TrackVersions?> GetTrackVersionsAsync(SongItem song, CancellationToken ct = default)
        => _inner.GetTrackVersionsAsync(song, ct);

    public Task<bool> VerifyVideoPinAsync(SongItem song, string videoId, CancellationToken ct = default)
        => _inner.VerifyVideoPinAsync(song, videoId, ct);

    public Task<bool> VerifyKnownClipAsync(SongItem song, string videoId, string? videoType, CancellationToken ct = default)
        => _inner.VerifyKnownClipAsync(song, videoId, videoType, ct);

    public Task<IReadOnlyList<SongItem>> GetQueueAsync(IReadOnlyList<string>? videoIds = null, string? playlistId = null, CancellationToken ct = default)
        => _inner.GetQueueAsync(videoIds, playlistId, ct);

    // ---- Sorting ----

    /// <summary>Filtering is on and the list is there (or was waited for as long as is fair).</summary>
    private async Task<bool> ReadyAsync(CancellationToken ct)
    {
        if (!_ai.Enabled) return false;
        await _ai.EnsureReadyAsync(ct).ConfigureAwait(false);
        return true;
    }

    private async Task<IReadOnlyList<SongItem>> KeepAsync(Task<IReadOnlyList<SongItem>> songs, CancellationToken ct)
    {
        var list = await songs.ConfigureAwait(false);
        if (!await ReadyAsync(ct).ConfigureAwait(false)) return list;
        var kept = list.Where(s => !_ai.IsAi(s)).ToList();
        if (kept.Count == list.Count) return list;
        System.Diagnostics.Trace.WriteLine("[AiMusic] left out " + string.Join(", ",
            list.Except(kept).Take(6).Select(s => $"{s.Title} ({s.ArtistDisplay})")));
        return kept;
    }

    /// <summary>Every shelf without its AI entries; a shelf left empty goes.</summary>
    private IReadOnlyList<Shelf> Keep(IReadOnlyList<Shelf> shelves)
    {
        var kept = new List<Shelf>(shelves.Count);
        foreach (var shelf in shelves)
            if (KeepOrNull(shelf) is { } left) kept.Add(left);
        return kept;
    }

    private Shelf? KeepOrNull(Shelf? shelf)
    {
        if (shelf == null) return null;
        var items = shelf.Items.Where(i => !_ai.IsAi(i)).ToList();
        if (items.Count == shelf.Items.Count) return shelf;
        System.Diagnostics.Trace.WriteLine($"[AiMusic] \"{shelf.Title}\": left out " + string.Join(", ",
            shelf.Items.Except(items).Take(6).Select(i => $"{i.Title} ({i.Subtitle})")));
        if (items.Count == 0) return null;
        return new Shelf
        {
            Title = shelf.Title,
            Layout = shelf.Layout,
            Items = items,
            MoreBrowseId = shelf.MoreBrowseId,
            MoreParams = shelf.MoreParams
        };
    }
}
