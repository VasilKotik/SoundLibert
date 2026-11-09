using SoundLibert.Domain.Enums;
using SoundLibert.Domain.Models;
using SoundLibert.Domain.Video;

namespace SoundLibert.Domain.Interfaces;

public interface IMusicProvider
{
    Task<SearchResult> SearchAsync(string query, SearchFilter filter = SearchFilter.Songs, CancellationToken ct = default);

    /// <summary>Searches to run and things to open while the user types.</summary>
    Task<SearchSuggestions> GetSuggestionsAsync(string query, CancellationToken ct = default);

    /// <summary>The "All" results: the best match and a shelf of every kind. Throws only if nothing answered.</summary>
    Task<SearchOverview> SearchOverviewAsync(string query, CancellationToken ct = default);

    Task<AlbumItem?> GetAlbumAsync(string browseId, CancellationToken ct = default);

    /// <summary>What an album or playlist page offers after its tracks: other versions, related playlists.</summary>
    Task<CollectionExtras> GetCollectionExtrasAsync(string browseId, CancellationToken ct = default);
    Task<IReadOnlyList<SongItem>> GetAlbumSongsAsync(string playlistId, CancellationToken ct = default);
    Task<IReadOnlyList<SongItem>> GetAlbumTracksAsync(string albumBrowseId, CancellationToken ct = default);
    Task<ArtistItem?> GetArtistAsync(string browseId, CancellationToken ct = default);
    Task<ArtistPage> GetArtistPageAsync(string browseId, CancellationToken ct = default);

    /// <summary>The artist page as YouTube answers it, in one request: shelves stay at their first ~10 items.</summary>
    Task<ArtistPage> GetArtistPreviewAsync(string browseId, CancellationToken ct = default);

    Task<PlaylistItem?> GetPlaylistAsync(string playlistId, CancellationToken ct = default);
    Task<IReadOnlyList<SongItem>> GetPlaylistSongsAsync(string playlistId, CancellationToken ct = default);

    /// <summary>The home feed, or the feed narrowed to one of its mood chips.</summary>
    Task<HomePage> GetHomePageAsync(string? chipParams = null, CancellationToken ct = default);

    /// <summary>The next portion of the home feed.</summary>
    Task<HomePage> GetHomeContinuationAsync(string token, CancellationToken ct = default);

    Task<ExplorePage> GetExplorePageAsync(CancellationToken ct = default);

    /// <summary>Every mood and genre there is to explore.</summary>
    Task<IReadOnlyList<MoodCategory>> GetMoodsAndGenresAsync(CancellationToken ct = default);

    /// <summary>A page of shelves: a mood, a genre, or the whole of a shelf behind its "Show all".</summary>
    Task<ShelfPage> GetShelfPageAsync(string browseId, string? @params = null, CancellationToken ct = default);

    /// <summary>A country's charts (ISO code, "ZZ" for the world): video charts and top artists, ranked.</summary>
    Task<IReadOnlyList<Shelf>> GetChartsAsync(string country, CancellationToken ct = default);

    /// <summary>What YouTube Music relates to a song: similar songs and artists, more by the artist.</summary>
    Task<SongRelated> GetSongRelatedAsync(string videoId, CancellationToken ct = default);


    /// <summary>Account library (requires sign-in): liked songs, playlists, albums and subscribed artists.</summary>
    Task<IReadOnlyList<SongItem>> GetAccountLikedSongsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PlaylistItem>> GetAccountPlaylistsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AlbumItem>> GetAccountAlbumsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ArtistItem>> GetAccountArtistsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SongItem>> GetRelatedTracksAsync(string videoId, CancellationToken ct = default);
    Task<IReadOnlyList<SongItem>> GetRadioAsync(string videoId, CancellationToken ct = default);

    /// <summary>
    /// EVERYTHING from the artist's own channel, read a page at a time (clips, live,
    /// sessions; no canvas-eligibility filtering): channel-authoritative by construction,
    /// so no verification. Taps play as video.
    /// </summary>
    IVideoFeed OpenArtistVideoFeed(string? artistBrowseId, string artistName);

    /// <summary>A Videos search read a page at a time. Rows are not checked against any channel.</summary>
    IVideoFeed OpenVideoSearchFeed(string query);

    /// <summary>YouTube Music's "New music videos" list.</summary>
    Task<IReadOnlyList<SongItem>> GetNewVideosAsync(CancellationToken ct = default);

    Task<StreamInfo?> GetStreamInfoAsync(string videoId, string? playlistId = null, int audioQuality = 0, CancellationToken ct = default);

    /// <summary>
    /// HTTP headers required to download a resolved stream URL (Velune
    /// resolves them from the URL's embedded client param, falling back to
    /// the requesting client — not from the requesting client alone).
    /// Kept on the provider so UI code never touches InnerTube internals.
    /// </summary>
    Dictionary<string, string> GetStreamHeaders(string streamUrl, string? fallbackClientName = null);

    /// <summary>
    /// Resolves a direct video stream URL. <paramref name="highQuality"/> picks a
    /// full-size frame (Video mode, watched fullscreen); otherwise a small ~480p
    /// frame is preferred (the muted Canvas background).
    /// <paramref name="targetHeight"/> overrides the resolution (Video mode
    /// quality setting); 0 = derive from <paramref name="highQuality"/>.
    /// </summary>
    Task<StreamInfo?> GetVideoStreamInfoAsync(string videoId, bool highQuality = false, int targetHeight = 0, CancellationToken ct = default);

    /// <summary>
    /// Resolves the global YouTube view count for a video id (Spotify-style play
    /// count) through the metadata player endpoint. Null when it cannot be
    /// determined. Results are cached briefly in memory.
    /// </summary>
    Task<long?> GetViewCountAsync(string videoId, CancellationToken ct = default);

    /// <summary>
    /// Resolves the two official YouTube Music versions of a track — the song
    /// (ATV audio track) and the music video (OMV) — like YouTube Music's own
    /// Song / Video switch. The counterpart is found through YouTube Music's own
    /// catalogue search and accepted only when it is tagged as an official music
    /// video (<c>MUSIC_VIDEO_TYPE_OMV</c>), never a random upload. Null when no
    /// versions can be determined.
    /// </summary>
    Task<TrackVersions?> GetTrackVersionsAsync(SongItem song, CancellationToken ct = default);

    /// <summary>
    /// Re-checks a CACHED video pin (playable + title + duration — never the
    /// artist gate, so label-hosted official clips cannot false-fail for lack
    /// of the original search row). Transport failure keeps the pin (fail
    /// open); only a positive mismatch returns false. Used for background
    /// self-healing of stale pins.
    /// </summary>
    Task<bool> VerifyVideoPinAsync(SongItem song, string videoId, CancellationToken ct = default);

    /// <summary>
    /// Resolves a queue of songs for the given video ids or playlist id
    /// (<c>music/get_queue</c>), used to materialise a playlist into a local queue.
    /// </summary>
    Task<IReadOnlyList<SongItem>> GetQueueAsync(
        IReadOnlyList<string>? videoIds = null, string? playlistId = null, CancellationToken ct = default);
}
