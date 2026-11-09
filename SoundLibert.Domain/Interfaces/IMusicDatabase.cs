using SoundLibert.Domain.Models;

namespace SoundLibert.Domain.Interfaces;

public interface IMusicDatabase
{
    Task InitializeAsync(CancellationToken ct = default);

    Task<SongEntity?> GetSongAsync(string id, CancellationToken ct = default);
    Task UpsertSongAsync(SongEntity song, CancellationToken ct = default);

    /// <summary>
    /// Inserts/updates many songs inside ONE transaction (used by local-folder
    /// import, where per-row transactions would be extremely slow).
    /// </summary>
    Task UpsertSongsAsync(IEnumerable<SongEntity> songs, CancellationToken ct = default);
    Task<IReadOnlyList<SongEntity>> GetLikedSongsAsync(CancellationToken ct = default);
    Task<IReadOnlySet<string>> GetYouTubeLikedSongIdsAsync(CancellationToken ct = default);

    /// <summary>Ids of every liked song (used to hydrate list rows' heart state).</summary>
    Task<IReadOnlySet<string>> GetLikedSongIdsAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes everything that came from the signed-in account (all songs that
    /// are not local files, plus account playlists), leaving the local library
    /// intact. Called on sign-out so account-synced data cannot linger.
    /// </summary>
    Task ClearAccountDataAsync(CancellationToken ct = default);

    /// <summary>Unlikes non-local songs that are liked locally but no longer in the account's likes.</summary>
    Task ReconcileLikesAsync(IReadOnlyCollection<string> accountLikedIds, CancellationToken ct = default);

    /// <summary>
    /// Drops every explicit in-app unlike (<c>IsLocalUnliked</c>) so the next
    /// account sync re-imports the YouTube state verbatim. Used only by the
    /// explicit "Re-import songs" action — automatic reloads keep the vetoes.
    /// </summary>
    Task ClearLocalUnlikesAsync(CancellationToken ct = default);

    /// <summary>
    /// Drops a cached song/video version pin for one song so the next play
    /// re-discovers it. User-confirmed pins (video pinned to its own id) are
    /// never touched — only guessed cross-id pins. Used by background
    /// self-healing when a cached pin fails verification.
    /// </summary>
    Task ClearVersionPinsAsync(string songId, CancellationToken ct = default);

    /// <summary>
    /// Records when clip discovery found no clip for a song (null clears the
    /// record, e.g. when a clip gets pinned). Missing rows are ignored.
    /// </summary>
    Task SetClipMissAtAsync(string songId, DateTime? value, CancellationToken ct = default);

    /// <summary>True when a recorded clip-miss is still fresh (skip discovery).</summary>
    Task<bool> HasFreshClipMissAsync(string songId, CancellationToken ct = default);

    /// <summary>
    /// A stored clip row already paired with this song (e.g. an album/artist
    /// row whose id is the clip and whose SongVersionId is this song), or null.
    /// </summary>
    Task<string?> FindKnownClipForSongAsync(string songId, CancellationToken ct = default);

    /// <summary>Records the offline copy path for a downloaded catalogue song.</summary>
    Task SetDownloadAsync(string songId, string path, DateTime downloadedAt, CancellationToken ct = default);

    /// <summary>Clears the offline copy record (does not touch the file).</summary>
    Task ClearDownloadAsync(string songId, CancellationToken ct = default);

    /// <summary>Ids of every song with a recorded offline copy.</summary>
    Task<IReadOnlySet<string>> GetDownloadedSongIdsAsync(CancellationToken ct = default);

    /// <summary>Every downloaded song, newest first.</summary>
    Task<IReadOnlyList<SongEntity>> GetDownloadedSongsAsync(CancellationToken ct = default);

    /// <summary>
    /// Deletes cached catalogue rows that are not part of the account, not liked,
    /// not in a local playlist and never played — so songs removed from YouTube
    /// stop lingering in the database. Local files and history are never touched.
    /// </summary>
    Task<int> PruneOrphanCatalogueAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SongEntity>> GetLibrarySongsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SongEntity>> GetRecentlyPlayedAsync(int limit = 50, string? accountKey = null, CancellationToken ct = default);
    Task<IReadOnlyList<SongEntity>> GetLocalSongsAsync(string? accountKey = null, CancellationToken ct = default);
    Task<bool> ToggleLikeAsync(string songId, string? accountKey, CancellationToken ct = default);

    Task<ArtistEntity?> GetArtistAsync(string id, CancellationToken ct = default);
    Task UpsertArtistAsync(ArtistEntity artist, CancellationToken ct = default);
    Task<IReadOnlyList<ArtistEntity>> GetLibraryArtistsAsync(string? accountKey = null, CancellationToken ct = default);
    Task<bool> IsArtistSubscribedAsync(string artistId, string? accountKey = null, CancellationToken ct = default);
    Task<bool> ToggleArtistSubscribeAsync(ArtistEntity artist, string? accountKey, CancellationToken ct = default);
    Task ImportAccountArtistsAsync(IEnumerable<ArtistEntity> artists, CancellationToken ct = default);
    Task ReconcileArtistsAsync(IReadOnlyCollection<string> accountArtistIds, CancellationToken ct = default);

    Task<AlbumEntity?> GetAlbumAsync(string id, CancellationToken ct = default);
    Task UpsertAlbumAsync(AlbumEntity album, CancellationToken ct = default);
    Task<IReadOnlyList<AlbumEntity>> GetLibraryAlbumsAsync(string? accountKey = null, CancellationToken ct = default);
    Task<bool> IsAlbumLikedAsync(string albumId, string? accountKey = null, CancellationToken ct = default);
    Task<bool> ToggleAlbumLikeAsync(AlbumEntity album, string? accountKey, CancellationToken ct = default);
    Task ImportAccountAlbumsAsync(IEnumerable<AlbumEntity> albums, CancellationToken ct = default);
    Task ReconcileAlbumLikesAsync(IReadOnlyCollection<string> accountAlbumIds, CancellationToken ct = default);

    Task<PlaylistEntity?> GetPlaylistAsync(string id, CancellationToken ct = default);
    Task UpsertPlaylistAsync(PlaylistEntity playlist, CancellationToken ct = default);
    Task<IReadOnlyList<PlaylistEntity>> GetLibraryPlaylistsAsync(CancellationToken ct = default);

    /// <summary>
    /// Up to <paramref name="perPlaylist"/> distinct track covers of every local
    /// (editable) playlist, in playlist order — for the 2×2 cover mosaic.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetLocalPlaylistCoversAsync(
        int perPlaylist = 4, CancellationToken ct = default);
    Task<bool> IsPlaylistSavedAsync(string playlistId, CancellationToken ct = default);
    Task<bool> TogglePlaylistSaveAsync(PlaylistEntity playlist, string? accountKey, CancellationToken ct = default);

    /// <summary>
    /// Removes account (non-editable) playlists that are no longer in the
    /// YouTube Music account. Local user playlists are never touched.
    /// </summary>
    Task ReconcileAccountPlaylistsAsync(IReadOnlyCollection<string> accountPlaylistIds, CancellationToken ct = default);

    /// <summary>Creates a user-owned (editable) playlist and returns it.</summary>
    Task<PlaylistEntity> CreatePlaylistAsync(string title, CancellationToken ct = default);

    /// <summary>Creates a user-owned playlist scoped to an account (null = device-local).</summary>
    Task<PlaylistEntity> CreatePlaylistAsync(string title, string? accountKey, CancellationToken ct = default);

    /// <summary>Renames a user-owned (editable) playlist.</summary>
    Task RenamePlaylistAsync(string playlistId, string newTitle, CancellationToken ct = default);

    /// <summary>Copies an editable playlist (with its tracks) under a new name.</summary>
    Task<PlaylistEntity> DuplicatePlaylistAsync(string playlistId, string newTitle, CancellationToken ct = default);

    /// <summary>Persists a new manual card order for the library playlists.</summary>
    Task ReorderPlaylistsAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default);

    Task DeletePlaylistAsync(string playlistId, CancellationToken ct = default);

    /// <summary>
    /// Assigns unscoped local rows to an account scope (per-account data: each
    /// sign-in sees its own; idempotent). Also merges the session handle scope
    /// into the email scope — both come from one live session, hence one human
    /// (email alone flickers when enrichment lags, splitting history in two).
    /// </summary>
    Task AdoptLocalDataAsync(string accountKey, string? handleKey, CancellationToken ct = default);

    /// <summary>
    /// Recomputes the materialized like columns for a scope from the
    /// per-account <c>local_likes</c> table (plus untouched YouTube flags).
    /// Keeps every existing like query working unchanged across sign-in/out.
    /// </summary>
    Task ApplyLikeScopeAsync(string? accountKey, CancellationToken ct = default);

    Task AddSongToPlaylistAsync(string playlistId, SongEntity song, CancellationToken ct = default);
    Task RemoveSongFromPlaylistAsync(string playlistId, string songId, CancellationToken ct = default);
    Task<IReadOnlyList<SongEntity>> GetPlaylistSongsAsync(string playlistId, CancellationToken ct = default);

    /// <summary>Moves a track within a playlist to a new position (reorder).</summary>
    Task MovePlaylistSongAsync(string playlistId, string songId, int newPosition, CancellationToken ct = default);

    Task AddPlayEventAsync(PlayEvent playEvent, CancellationToken ct = default);
    Task RecordPlayAsync(string songId, int playedMs, string? playlistId = null, string? accountKey = null, CancellationToken ct = default);
    Task<IReadOnlyList<PlayEvent>> GetPlayHistoryAsync(int limit = 200, CancellationToken ct = default);
    Task<IReadOnlyList<PlayHistoryItem>> GetHistoryAsync(int limit = 200, string? accountKey = null, CancellationToken ct = default);
    Task<PlayStats> GetPlayStatsAsync(DateTime? since = null, string? accountKey = null, CancellationToken ct = default);

    Task AddSearchHistoryAsync(string query, string? accountKey = null, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetSearchHistoryAsync(int limit = 20, string? accountKey = null, CancellationToken ct = default);

    Task UpsertLyricsAsync(string songId, string lyrics, CancellationToken ct = default);
    Task<string?> GetLyricsAsync(string songId, CancellationToken ct = default);

    /// <summary>Deletes every cached lyrics entry (Settings → Clear cache).</summary>
    Task ClearLyricsCacheAsync(CancellationToken ct = default);

    /// <summary>Cached artist genre (from the external genre lookup), or null if unknown.</summary>
    Task<string?> GetArtistGenreAsync(string artist, CancellationToken ct = default);

    /// <summary>Stores an artist's resolved genre (null = known-unknown, still cached).</summary>
    Task SetArtistGenreAsync(string artist, string? genre, CancellationToken ct = default);

    /// <summary>Cached per-song genre (from the external genre lookup), or null if unknown.</summary>
    Task<string?> GetSongGenreAsync(string songId, CancellationToken ct = default);

    /// <summary>Stores a song's resolved genre (null = known-unknown, still cached).</summary>
    Task SetSongGenreAsync(string songId, string? genre, CancellationToken ct = default);

    /// <summary>Cached global view counts for the given song ids (missing = unresolved).</summary>
    Task<IReadOnlyDictionary<string, long>> GetViewCountsAsync(IEnumerable<string> songIds, CancellationToken ct = default);

    /// <summary>Persists a resolved global view count for a song.</summary>
    Task SetViewCountAsync(string songId, long viewCount, DateTime resolvedAt, CancellationToken ct = default);
}
