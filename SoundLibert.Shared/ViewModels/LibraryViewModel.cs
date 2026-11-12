using SoundLibert.Domain.Exceptions;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;

using SoundLibert.UI.Controls;

namespace SoundLibert.UI.ViewModels;

/// <summary>
/// Library collections, with YouTube-imported likes and SoundLibert-local likes
/// kept separate. Data loads automatically when the view is entered.
/// </summary>
public partial class LibraryViewModel : ViewModelBase
{
    private readonly IMusicDatabase _database;
    private readonly IQueueService _queueService;
    private readonly ILocalLibrary _localLibrary;
    private readonly IMusicProvider _musicProvider;
    private readonly IAccountService _account;
    private readonly ILikeService _likeService;
    private readonly IDownloadService _downloads;
    private readonly IGenreService _genres;
    private readonly IViewCountService _viewCounts;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasLikedSongs;
    [ObservableProperty] private bool _hasRecentlyPlayed;
    [ObservableProperty] private bool _hasLocalSongs;
    [ObservableProperty] private bool _hasDownloads;
    [ObservableProperty] private bool _hasHistory;
    [ObservableProperty] private bool _hasArtists;
    [ObservableProperty] private bool _hasAlbums;
    [ObservableProperty] private bool _hasPlaylists;
    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private string _localStatus = string.Empty;
    [ObservableProperty] private string _accountNote = string.Empty;

    // ---- Drill-in state (a special collection or a playlist opened from a card) ----
    [ObservableProperty] private bool _isCollectionOpen;
    [ObservableProperty] private bool _collectionHasTracks;
    [ObservableProperty] private bool _isLocalSection;
    /// <summary>Songs / Videos filter tab in a collection drill-in.</summary>
    [ObservableProperty] private bool _isVideosTab;
    [ObservableProperty] private string _collectionTitle = string.Empty;
    [ObservableProperty] private string _collectionSubtitle = string.Empty;
    [ObservableProperty] private string _collectionEmptyMessage = "Nothing here yet.";
    private LibrarySection _openSection = LibrarySection.Liked;
    private PlaylistEntity? _openPlaylist;

    public ObservableCollection<SongItem> LikedSongs { get; } = new();
    public ObservableCollection<SongItem> RecentlyPlayed { get; } = new();
    public ObservableCollection<SongItem> LocalSongs { get; } = new();
    public ObservableCollection<SongItem> DownloadedSongs { get; } = new();
    public ObservableCollection<PlayHistoryItem> History { get; } = new();
    public ObservableCollection<ArtistItem> Artists { get; } = new();
    public ObservableCollection<AlbumItem> Albums { get; } = new();
    public ObservableCollection<PlaylistEntity> Playlists { get; } = new();

    /// <summary>The Library grid: the four built-in tiles followed by the playlists.</summary>
    public ObservableCollection<LibraryCard> Sections { get; } = new();

    /// <summary>The built-in collections (Liked, Recent, ...): the compact tiles.</summary>
    public ObservableCollection<LibraryCard> Collections { get; } = new();

    /// <summary>The playlists: the cover cards.</summary>
    public ObservableCollection<LibraryCard> PlaylistCards { get; } = new();

    /// <summary>Show the Songs/Videos filter chips in the Liked and playlist drill-ins.</summary>
    public bool ShowCollectionTabs => IsCollectionOpen
        && (_openSection == LibrarySection.Liked || _openSection == LibrarySection.Playlist);

    /// <summary>True while showing a videos list (rows open as clips).</summary>
    public bool IsVideoSection => IsCollectionOpen
        && (_openSection == LibrarySection.Liked || _openSection == LibrarySection.Playlist)
        && IsVideosTab;
    public bool IsSongsTab => !IsVideosTab;

    partial void OnIsCollectionOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCollectionTabs));
        OnPropertyChanged(nameof(IsVideoSection));
        UpdateDrillFlags();
    }

    partial void OnIsVideosTabChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSongsTab));
        OnPropertyChanged(nameof(IsVideoSection));
        if (!IsCollectionOpen) return;
        if (_openSection == LibrarySection.Liked) RefreshOpenCollection();
        else if (_openSection == LibrarySection.Playlist) RefreshPlaylistTab();
    }

    [RelayCommand]
    private void ShowSongsTab() => IsVideosTab = false;

    [RelayCommand]
    private void ShowVideosTab() => IsVideosTab = true;

    /// <summary>
    /// Splits rows into songs and clips (clips never count as songs); a song
    /// twin of a listed clip is hidden from the songs side. Shared by the
    /// Liked tile subtitle and the Liked drill-in tabs so both agree.
    /// </summary>
    private static (List<SongItem> Songs, List<SongItem> Videos) SplitSongClips(IEnumerable<SongItem> rows)
    {
        var videos = rows.Where(IsVideoTrack).ToList();
        var songs = rows.Where(s => !IsVideoTrack(s)).ToList();
        if (videos.Count > 0 && songs.Count > 0)
        {
            var clips = new SongIdentity.TrackSet(videos);
            songs = songs.Where(s => !clips.Contains(s)).ToList();
        }
        return (songs, videos);
    }

    /// <summary>True when the row is a video clip (single rule lives on the row).</summary>
    private static bool IsVideoTrack(SongItem song) => song.IsVideoClip;

    /// <summary>Unified header counts: clips never inflate the songs number.</summary>
    private static string CountBoth(int songs, int videos)
        => videos == 0
            ? songs == 1 ? "1 song" : $"{songs} songs"
            : songs == 0
                ? videos == 1 ? "1 video" : $"{videos} videos"
                : $"{(songs == 1 ? "1 song" : $"{songs} songs")} · {(videos == 1 ? "1 video" : $"{videos} videos")}";

    /// <summary>All rows of the open playlist (unfiltered); the tab filters CollectionTracks.</summary>
    private List<SongItem> _openPlaylistTracks = new();
    private string? _openPlaylistLoadedFor;

    /// <summary>Genre chips shown inside a playlist (to build a genre mix).</summary>
    public ObservableCollection<GenreChip> Genres { get; } = new();

    private string[] _activeGenreKeywords = Array.Empty<string>();
    private readonly Dictionary<string, string?> _genreBySong = new(StringComparer.Ordinal);
    private int _genreGeneration;

    /// <summary>Full (unfiltered) rows of the open non-playlist section.</summary>
    private List<SongItem> _sectionTracks = new();

    /// <summary>The list the genre chips are resolved from and filter against.</summary>
    private List<SongItem> _genreSource = new();

    /// <summary>True when the drill-in can show genre chips (any track section with songs).</summary>
    [ObservableProperty] private bool _hasGenreChips;

    /// <summary>Guards overlapping playlist loads: only the latest may fill the list.</summary>
    private int _playlistGeneration;

    /// <summary>Set by the shell so Play-all/Shuffle in a videos tab play clips, not songs.</summary>
    public Action<IReadOnlyList<SongItem>, SongItem?>? PlayVideosRequest { get; set; }

    /// <summary>Set by the shell: reports a friendly, user-facing error.</summary>
    public Action<string>? ReportError { get; set; }

    /// <summary>True when the drill-in shows an editable (local) playlist.</summary>
    public bool IsOpenPlaylistEditable => _openPlaylist?.IsEditable == true;

    /// <summary>Tracks shown in the opened collection.</summary>
    public ObservableCollection<SongItem> CollectionTracks { get; } = new();

    /// <summary>Artists shown in the opened Artists collection.</summary>
    public ObservableCollection<ArtistItem> DrillArtists { get; } = new();

    /// <summary>Albums shown in the opened Saved albums collection.</summary>
    public ObservableCollection<AlbumItem> DrillAlbums { get; } = new();

    public LibraryViewModel(IMusicDatabase database, IQueueService queueService, ILocalLibrary localLibrary,
        IMusicProvider musicProvider, IAccountService account, ILikeService likeService,
        IDownloadService downloads, IGenreService genres, IViewCountService viewCounts)
    {
        _database = database;
        _queueService = queueService;
        _localLibrary = localLibrary;
        _musicProvider = musicProvider;
        _account = account;
        _likeService = likeService;
        _downloads = downloads;
        _genres = genres;
        _viewCounts = viewCounts;

        // A download finishing/failing refreshes the Downloads tile live
        // (progress ticks are ignored — they fire far too often for a rebuild).
        _downloads.Changed += (_, e) =>
        {
            if (e.State == DownloadState.Downloading) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _ = RefreshDownloadsAsync();
                if (IsCollectionOpen && _openSection == LibrarySection.Downloads) RefreshOpenCollection();
            });
        };

        // App-lifetime collections: refresh the empty-state flag on changes.
        DrillArtists.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowDrillEmpty));
        DrillAlbums.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowDrillEmpty));

        _account.SignedIn += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = ReloadAsync(force: true));

        // Signing out drops everything that came from the account (likes, cached
        // catalogue, account playlists) and reloads — the library then shows only
        // local content until the next sign-in.
        _account.SignedOut += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await _database.ClearAccountDataAsync();
                IsSignedIn = false;
                AccountNote = string.Empty;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Library] Clear on sign-out failed: {ex.Message}");
            }
            _ = ReloadAsync();
        });
    }

    public override void Initialize() => _ = ReloadAsync();

    /// <summary>Set while a reload runs and another one was asked for; completes when that one has run.</summary>
    private TaskCompletionSource? _queuedReload;

    /// <summary>The queued reload must reach YouTube if any of the callers asked for that.</summary>
    private bool _queuedReloadForce;

    /// <summary>Last account sync; automatic reloads skip the network while fresh.</summary>
    private DateTime _lastAccountSync = DateTime.MinValue;


    private static readonly TimeSpan AccountSyncTtl = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Explicit re-import (Settings → Account): discards the local unlike
    /// vetoes first, so songs deleted while signed in come back from YouTube.
    /// Automatic reloads keep the vetoes — only this button clears them.
    /// Re-imports the whole account library (likes, albums, artists, playlists).
    /// </summary>
    public async Task ReimportAsync()
    {
        try
        {
            await _database.ClearLocalUnlikesAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Library] Clear vetoes failed: {ex.Message}");
        }
        await ReloadAsync(force: true);
    }

    [RelayCommand]
    private Task Reload() => ReloadAsync(force: true);

    /// <summary>
    /// Reloads the library. Local lists always refresh from SQLite (cheap);
    /// the account sync (full like pagination + per-song upserts + reconcile +
    /// playlists) runs only when forced (Refresh button, sign-in, re-import)
    /// or stale — it used to hammer YouTube on every single Library visit.
    /// </summary>
    private Task ReloadAsync(bool force = false)
    {
        if (!IsLoading) return RunReloadAsync(force);

        // A reload already running: another pass runs after it, and the caller
        // waits for that pass (Re-import reported success before it had started).
        _queuedReloadForce |= force;
        _queuedReload ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _queuedReload.Task;
    }

    private async Task RunQueuedReloadAsync(bool force, TaskCompletionSource done)
    {
        try { await RunReloadAsync(force); }
        finally { done.TrySetResult(); }
    }

    private async Task RunReloadAsync(bool force)
    {
        IsLoading = true;
        try
        {
            IsSignedIn = _account.IsSignedIn;
            var scope = IsSignedIn ? _account.AccountScopeKey : null;

            // Per-account data: adopt unscoped local rows into this sign-in
            // (idempotent) and materialize this scope's hearts (side table →
            // columns) BEFORE anything reads below. Signed-out sees only the
            // device-local scope.
            if (IsSignedIn && scope is not null)
            {
                try { await _database.AdoptLocalDataAsync(scope, _account.AccountHandle); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[Library] Local data adoption failed: {ex.Message}"); }
            }
            try { await _database.ApplyLikeScopeAsync(scope); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[Library] Like scope apply failed: {ex.Message}"); }

            var liked = (await _database.GetLikedSongsAsync()).Select(s => s.ToSongItem()).ToList();
            var recent = await _database.GetRecentlyPlayedAsync(100, scope);
            var local = await _database.GetLocalSongsAsync(scope);
            var history = await _database.GetHistoryAsync(150, scope);
            var playlists = await _database.GetLibraryPlaylistsAsync();

            // When signed in, re-import the account source and reconcile only its
            // flags; SoundLibert-local favorites remain independent. The network
            // sync runs only when forced or stale — local lists above already
            // refreshed from SQLite, so a fresh visit costs no requests.
            if (IsSignedIn && _account.NeedsReauthentication)
            {
                // YouTube no longer recognises the session: keep the mirrored
                // library as it is (an empty "signed out" answer must never be
                // reconciled) and ask for a fresh sign-in.
                AccountNote = string.Empty; // the app-wide banner already says it
                playlists = await _database.GetLibraryPlaylistsAsync();
            }
            else if (IsSignedIn && (force || DateTime.UtcNow - _lastAccountSync >= AccountSyncTtl))
            {
                try
                {
                    try
                    {
                        // The account is the source of truth on every load: upsert the
                        // current likes and reconcile BOTH ways (add new, drop removed),
                        // even when the account likes list is empty.
                        var accountLiked = await _musicProvider.GetAccountLikedSongsAsync();
                        var accountIds = accountLiked.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
                        var localBefore = await _database.GetYouTubeLikedSongIdsAsync();
                        var removed = localBefore.Count(id => !accountIds.Contains(id));
                        System.Diagnostics.Trace.WriteLine(
                            $"[Library] Account likes loaded: {accountLiked.Count}; " +
                            $"local liked: {localBefore.Count}; will unlike (removed on YouTube): {removed}");

                        var entities = new List<SongEntity>(accountLiked.Count);
                        foreach (var song in accountLiked)
                        {
                            var entity = song.ToEntity();
                            entity.IsLiked = true;
                            entity.IsYouTubeLiked = true;
                            // BookmarkedAt is intentionally left null: the upsert's
                            // COALESCE keeps the existing stamp so the "recently
                            // liked" order survives every sync. A brand-new like is
                            // stamped when it is first upserted to the row.
                            if (!localBefore.Contains(song.Id)) entity.BookmarkedAt = DateTime.UtcNow;
                            entities.Add(entity);
                        }

                        // ONE transaction for the whole import (per-row commits made
                        // large libraries take minutes); same upsert semantics.
                        if (entities.Count > 0)
                            await _database.UpsertSongsAsync(entities);

                        await _database.ReconcileLikesAsync(accountIds.ToList());

                        // A successful sync is the moment to drop stale cache rows
                        // (songs no longer on YouTube, not liked, never played).
                        await _database.PruneOrphanCatalogueAsync();

                        AccountNote = string.Empty;
                    }
                    catch (AccountLibraryUnrecognizedException ex)
                    {
                        // Nothing reconciled, nothing lost. An empty library on a new
                        // account looks like this too, so it is no error for the user.
                        System.Diagnostics.Trace.WriteLine($"[Library] Account likes skipped: {ex.Message}");
                        AccountNote = string.Empty;
                    }
                    catch (Exception ex) when (ex is not AccountSessionInvalidException)
                    {
                        System.Diagnostics.Trace.WriteLine($"[Library] Account likes failed: {ex.Message}");
                        AccountNote = "Couldn't sync your YouTube Music likes right now. Your library is kept; try Refresh.";
                    }

                    // Sync account playlists into the library (read-only copies).
                    // Deleting a playlist on YouTube removes it here on the next reload.
                    try
                    {
                        var accountPlaylists = await _musicProvider.GetAccountPlaylistsAsync();
                        System.Diagnostics.Trace.WriteLine($"[Library] Account playlists loaded: {accountPlaylists.Count}");
                        if (accountPlaylists.Count > 0)
                        {
                            var now = DateTime.UtcNow;
                            foreach (var p in accountPlaylists)
                            {
                                await _database.UpsertPlaylistAsync(new PlaylistEntity
                                {
                                    Id = p.Id,
                                    Title = p.Title,
                                    BrowseId = p.Id,
                                    ThumbnailUrl = p.ThumbnailUrl,
                                    IsEditable = false,
                                    BookmarkedAt = now,
                                    LastUpdated = now
                                });
                            }

                        }

                        // An empty result is also authoritative: playlists removed
                        // from YouTube must disappear from the imported tab.
                        await _database.ReconcileAccountPlaylistsAsync(
                            accountPlaylists.Select(p => p.Id).ToList());
                        playlists = await _database.GetLibraryPlaylistsAsync();
                    }
                    catch (Exception ex) when (ex is not AccountSessionInvalidException)
                    {
                        System.Diagnostics.Trace.WriteLine($"[Library] Account playlists failed: {ex.Message}");
                    }

                    // Subscribed artists mirror (read-only import; local follows kept).
                    try
                    {
                        var accountArtists = await _musicProvider.GetAccountArtistsAsync();
                        await _database.ImportAccountArtistsAsync(accountArtists.Select(a => new ArtistEntity
                        {
                            Id = a.Id,
                            Name = a.Name,
                            ThumbnailUrl = a.ThumbnailUrl,
                            ChannelId = a.ChannelId
                        }));
                        await _database.ReconcileArtistsAsync(accountArtists.Select(a => a.Id).ToList());
                        System.Diagnostics.Trace.WriteLine($"[Library] Account artists synced: {accountArtists.Count}");
                    }
                    catch (Exception ex) when (ex is not AccountSessionInvalidException)
                    {
                        System.Diagnostics.Trace.WriteLine($"[Library] Account artists failed: {ex.Message}");
                    }

                    // Saved albums import (read-only side; local hearts untouched).
                    try
                    {
                        var accountAlbums = await _musicProvider.GetAccountAlbumsAsync();
                        await _database.ImportAccountAlbumsAsync(accountAlbums.Select(a => new AlbumEntity
                        {
                            Id = a.Id,
                            Title = a.Title,
                            ArtistsJson = System.Text.Json.JsonSerializer.Serialize(
                                a.Artists.Select(x => x.Name).ToArray()),
                            ThumbnailUrl = a.ThumbnailUrl
                        }));
                        await _database.ReconcileAlbumLikesAsync(accountAlbums.Select(a => a.Id).ToList());
                        System.Diagnostics.Trace.WriteLine($"[Library] Account albums synced: {accountAlbums.Count}");
                    }
                    catch (Exception ex) when (ex is not AccountSessionInvalidException)
                    {
                        System.Diagnostics.Trace.WriteLine($"[Library] Account albums failed: {ex.Message}");
                    }
                }
                catch (AccountSessionInvalidException ex)
                {
                    // Nothing was reconciled: the first section that saw the
                    // signed-out answer aborted the whole sync.
                    System.Diagnostics.Trace.WriteLine($"[Library] Sync stopped, library kept: {ex.Message}");
                    _account.MarkNeedsReauthentication();
                    AccountNote = string.Empty; // the app-wide banner already says it
                    playlists = await _database.GetLibraryPlaylistsAsync();
                }
                _lastAccountSync = DateTime.UtcNow;
            }
            else if (IsSignedIn)
            {
                // A fresh sync is already in place — just re-read the grid rows.
                playlists = await _database.GetLibraryPlaylistsAsync();
            }

            // Refresh the unified liked collection after account reconciliation.
            liked = (await _database.GetLikedSongsAsync()).Select(s => s.ToSongItem()).ToList();
            // Every heart in the app follows the reconciled likes (rows already on
            // screen were drawn from the likes as they were before the sync).
            try { await _likeService.ReloadAsync(); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[Library] Like reload failed: {ex.Message}"); }

            LikedSongs.Clear();
            foreach (var s in liked) LikedSongs.Add(s);

            RecentlyPlayed.Clear();
            foreach (var s in recent) RecentlyPlayed.Add(s.ToSongItem());

            LocalSongs.Clear();
            foreach (var s in local) LocalSongs.Add(s.ToSongItem());

            DownloadedSongs.Clear();
            foreach (var s in await _downloads.GetDownloadedAsync()) DownloadedSongs.Add(s);

            History.Clear();
            foreach (var h in history) History.Add(h);

            Playlists.Clear();
            foreach (var p in playlists) Playlists.Add(p);

            Artists.Clear();
            var scopeArtists = IsSignedIn ? _account.AccountScopeKey : null;
            foreach (var a in await _database.GetLibraryArtistsAsync(scopeArtists))
                Artists.Add(a.ToArtistItem());

            Albums.Clear();
            var scopeAlbums = IsSignedIn ? _account.AccountScopeKey : null;
            foreach (var a in await _database.GetLibraryAlbumsAsync(scopeAlbums))
                Albums.Add(a.ToAlbumItem());

            HasLikedSongs = LikedSongs.Count > 0;
            HasRecentlyPlayed = RecentlyPlayed.Count > 0;
            HasLocalSongs = LocalSongs.Count > 0;
            HasDownloads = DownloadedSongs.Count > 0;
            HasHistory = History.Count > 0;
            HasPlaylists = Playlists.Count > 0;
            HasArtists = Artists.Count > 0;
            HasAlbums = Albums.Count > 0;

            try { _playlistCovers = await _database.GetLocalPlaylistCoversAsync(); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[Library] playlist covers failed: {ex.Message}"); }
            BuildSections();

            // If a collection is open, refresh its tracks against the new data.
            if (IsCollectionOpen) RefreshOpenCollection();
            LibraryReloaded?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Library] Reload failed: {ex.Message}");
            ReportError?.Invoke("Couldn't load your library. Check your connection and try Refresh.");
        }
        finally
        {
            IsLoading = false;

            // A call arrived while we were loading: run once more so the newest
            // account state wins.
            if (_queuedReload is { } queued)
            {
                var queuedForce = _queuedReloadForce;
                _queuedReload = null;
                _queuedReloadForce = false;
                _ = RunQueuedReloadAsync(queuedForce, queued);
            }
        }
    }

    /// <summary>A reload finished: the playlists in the database may differ from before.</summary>
    public event Action? LibraryReloaded;

    /// <summary>
    /// Playlists changed elsewhere (track menu, another page): repaints the grid.
    /// An open list stays as it is, so the row being looked at does not jump.
    /// </summary>
    public async Task RefreshPlaylistTilesAsync()
    {
        try
        {
            var playlists = await _database.GetLibraryPlaylistsAsync();
            Playlists.Clear();
            foreach (var p in playlists) Playlists.Add(p);
            HasPlaylists = Playlists.Count > 0;
            // Counts on the open playlist's tile are kept on this entity.
            if (_openPlaylist is { } open)
                _openPlaylist = Playlists.FirstOrDefault(p => string.Equals(p.Id, open.Id, StringComparison.Ordinal)) ?? open;
            _playlistCovers = await _database.GetLocalPlaylistCoversAsync();
            BuildSections();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Library] playlist tiles refresh failed: {ex.Message}");
        }
    }

    /// <summary>Reloads the offline-copy list and rebuilds the tile counts.</summary>
    private async Task RefreshDownloadsAsync()
    {
        try
        {
            var list = await _downloads.GetDownloadedAsync();
            DownloadedSongs.Clear();
            foreach (var s in list) DownloadedSongs.Add(s);
            HasDownloads = DownloadedSongs.Count > 0;
            BuildSections();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Library] Downloads refresh failed: {ex.Message}");
        }
    }

    /// <summary>Rebuilds the grid: four built-in tiles + one card per playlist.</summary>
    private void BuildSections()
    {
        static string Count(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

        Sections.Clear();
        Collections.Clear();
        PlaylistCards.Clear();

        void AddCollection(LibrarySection section, string title, string subtitle, IconKind icon)
        {
            var card = new LibraryCard { Section = section, Title = title, Subtitle = subtitle, Icon = icon };
            Sections.Add(card);
            Collections.Add(card);
        }

        // Clips never count as songs: the tile shows both numbers separately.
        var (tileSongs, tileVideos) = SplitSongClips(LikedSongs);
        AddCollection(LibrarySection.Liked, "Liked Songs",
            tileVideos.Count == 0
                ? Count(tileSongs.Count, "song")
                : tileSongs.Count == 0
                    ? Count(tileVideos.Count, "video")
                    : $"{Count(tileSongs.Count, "song")} · {Count(tileVideos.Count, "video")}",
            IconKind.Heart);
        AddCollection(LibrarySection.Recent, "Recently played", Count(RecentlyPlayed.Count, "song"), IconKind.Clock);
        AddCollection(LibrarySection.Downloads, "Downloads", Count(DownloadedSongs.Count, "song"), IconKind.Download);
        AddCollection(LibrarySection.Local, "Local files", Count(LocalSongs.Count, "song"), IconKind.Folder);
        AddCollection(LibrarySection.Albums, "Saved albums", Count(Albums.Count, "album"), IconKind.Album);
        AddCollection(LibrarySection.Artists, "Artists", Count(Artists.Count, "artist"), IconKind.User);
        AddCollection(LibrarySection.History, "History", Count(History.Count, "play"), IconKind.History);

        var scope = _account.AccountScopeKey;
        foreach (var p in Playlists.Where(p => PlaylistEntity.InScope(p, scope)))
        {
            var card = new LibraryCard
            {
                Section = LibrarySection.Playlist,
                Title = p.Title,
                Subtitle = p.ContentDisplay,
                Playlist = p,
                CoverUrl = p.ThumbnailUrl,
                CoverUrls = p.IsEditable && _playlistCovers.TryGetValue(p.Id, out var covers) ? covers : null
            };
            Sections.Add(card);
            PlaylistCards.Add(card);
        }
    }

    /// <summary>The tile that was opened (its art/glyph heads the drill-in hero).</summary>
    // Never null (bindings read through it before any tile was opened).
    [ObservableProperty] private LibraryCard? _openedCard = new()
    {
        Section = LibrarySection.Liked, Title = string.Empty, Subtitle = string.Empty
    };

    /// <summary>Hero eyebrow: what kind of collection is open.</summary>
    public string OpenCardKindLabel => OpenedCard switch
    {
        { Section: LibrarySection.Playlist, Playlist.IsEditable: true } => "YOUR PLAYLIST",
        { Section: LibrarySection.Playlist } => "PLAYLIST",
        { Section: LibrarySection.Artists } => "FOLLOWED ARTISTS",
        { Section: LibrarySection.Albums } => "SAVED ALBUMS",
        _ => "COLLECTION"
    };

    partial void OnOpenedCardChanged(LibraryCard? value) => OnPropertyChanged(nameof(OpenCardKindLabel));

    /// <summary>Local playlist id → its first distinct track covers (mosaic tiles).</summary>
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _playlistCovers =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Opens a collection when its tile is tapped.</summary>
    public void OpenCard(LibraryCard card)
    {
        if (card is null) return;
        OpenedCard = card;

        _openSection = card.Section;
        _openPlaylist = card.Playlist;
        ResetGenreFilter();
        // A new collection starts unfiltered in its own order.
        _resettingCollectionView = true;
        CollectionFilter = string.Empty;
        CollectionSortIndex = 0;
        _resettingCollectionView = false;

        // Note: the Songs/Videos tab is deliberately NOT reset here — it is
        // remembered across opens so saved clips don't "turn into songs" on
        // re-entry.
        IsLocalSection = card.Section == LibrarySection.Local;
        CollectionTitle = card.Title;
        CollectionSubtitle = card.Subtitle;
        IsCollectionOpen = true;
        OnPropertyChanged(nameof(ShowCollectionTabs));
        UpdateDrillFlags();
        RefreshOpenCollection();
    }

    /// <summary>Opens one of the built-in collections by section.</summary>
    public void OpenSection(LibrarySection section)
    {
        var card = Sections.FirstOrDefault(c => c.Section == section);
        if (card != null) OpenCard(card);
    }

    [RelayCommand]
    private void CloseCollection()
    {
        IsCollectionOpen = false;
        _openPlaylist = null;
        // Nobody looks at the chips any more: stop asking the network for genres.
        _genreGeneration++;
        CollectionTracks.Clear();
        DrillArtists.Clear();
        DrillAlbums.Clear();
        UpdateDrillFlags();
    }

    private void RefreshOpenCollection()
    {
        // This pass rebuilds the chips, and any pass still running is out of date.
        _genreGeneration++;
        CollectionTracks.Clear();
        _sectionTracks = new List<SongItem>();
        switch (_openSection)
        {
            case LibrarySection.Liked:
                // One list: clips inline with a badge, song twins of listed
                // clips hidden (otherwise every liked video appears twice).
                var (likedSongs, likedVideos) = SplitSongClips(LikedSongs);
                var shownSongs = new HashSet<SongItem>(likedSongs);
                foreach (var s in LikedSongs)
                {
                    if (!s.IsVideoClip && !shownSongs.Contains(s)) continue;
                    _sectionTracks.Add(s);
                }
                CollectionSubtitle = CountBoth(likedSongs.Count, likedVideos.Count);
                CollectionEmptyMessage = "No liked songs yet. Like any track to save it here.";
                break;
            case LibrarySection.Recent:
                _sectionTracks.AddRange(RecentlyPlayed);
                CollectionEmptyMessage = "Nothing played yet.";
                break;
            case LibrarySection.Local:
                _sectionTracks.AddRange(LocalSongs);
                CollectionEmptyMessage = "No local music yet. Use “Import folder…”.";
                break;
            case LibrarySection.Downloads:
                _sectionTracks.AddRange(DownloadedSongs);
                CollectionSubtitle = DownloadedSongs.Count == 1 ? "1 song" : $"{DownloadedSongs.Count} songs";
                CollectionEmptyMessage = "Nothing downloaded yet. Use ⋯ → Download on any track.";
                break;
            case LibrarySection.History:
                // History carries its own metadata; map to playable items.
                foreach (var h in History) _sectionTracks.Add(h.ToSongItem());
                CollectionEmptyMessage = "Nothing played yet.";
                break;
            case LibrarySection.Playlist when _openPlaylist is { } p:
                CollectionSubtitle = p.TrackCountDisplay;
                _ = LoadPlaylistIntoTracksAsync(p);
                return;
            case LibrarySection.Artists:
                DrillArtists.Clear();
                foreach (var a in Artists.Where(a => MatchesFilter(a.Name))
                             .OrderBy(a => CollectionSortIndex == 0 ? string.Empty : a.Name, StringComparer.CurrentCultureIgnoreCase))
                    DrillArtists.Add(a);
                CollectionSubtitle = DrillArtists.Count == 1 ? "1 artist" : $"{DrillArtists.Count} artists";
                CollectionEmptyMessage = "No followed artists yet. Subscribe on any artist page.";
                UpdateDrillFlags();
                CollectionHasTracks = DrillArtists.Count > 0;
                return;
            case LibrarySection.Albums:
                DrillAlbums.Clear();
                foreach (var a in Albums.Where(a => MatchesFilter(a.Title) || MatchesFilter(a.ArtistDisplay))
                             .OrderBy(a => CollectionSortIndex switch
                             {
                                 0 => string.Empty,
                                 2 => a.ArtistDisplay,
                                 _ => a.Title
                             }, StringComparer.CurrentCultureIgnoreCase))
                    DrillAlbums.Add(a);
                CollectionSubtitle = DrillAlbums.Count == 1 ? "1 album" : $"{DrillAlbums.Count} albums";
                CollectionEmptyMessage = "No saved albums yet. Like any album to keep it here.";
                UpdateDrillFlags();
                CollectionHasTracks = DrillAlbums.Count > 0;
                return;
        }

        // Every track section (Liked / Recent / Local / Downloads / History)
        // gets the same genre chips built from ITS OWN songs.
        _sectionEmptyMessage = CollectionEmptyMessage;
        _genreSource = _sectionTracks;
        ApplySectionGenreFilter();
        if (_genreSource.Count > 0) _ = RefreshGenresAsync(_genreGeneration);
    }

    /// <summary>The open section's own empty text (restored when a filter is cleared).</summary>
    private string? _sectionEmptyMessage;

    /// <summary>Fills the drill-in list from the section source, genre-filtered.</summary>
    private void ApplySectionGenreFilter()
    {
        CollectionTracks.Clear();
        var src = _activeGenreKeywords.Length == 0
            ? _genreSource
            : _genreSource.Where(SongMatchesGenre).ToList();
        foreach (var s in FilterAndSort(src)) CollectionTracks.Add(s);
        if (CollectionTracks.Count == 0)
        {
            if (FilteredEmptyMessage() is { } filtered) CollectionEmptyMessage = filtered;
            else if (_activeGenreKeywords.Length > 0) CollectionEmptyMessage = "No songs of this genre here.";
            else if (_sectionEmptyMessage != null) CollectionEmptyMessage = _sectionEmptyMessage;
        }
        CollectionHasTracks = CollectionTracks.Count > 0;
        PopulateCollectionViews();
    }

    /// <summary>
    /// Fills Spotify-style global view counts on the drill-in rows: cached from
    /// SQLite now, unknown ones resolved in the background (capped).
    /// </summary>
    private void PopulateCollectionViews()
    {
        if (CollectionTracks.Count == 0) return;
        var rows = CollectionTracks.ToList();
        _ = HydrateAndResolveViewsAsync(rows);
    }

    private async Task HydrateAndResolveViewsAsync(IReadOnlyList<SongItem> rows)
    {
        await _viewCounts.HydrateAsync(rows);
        await _viewCounts.ResolveMissingAsync(rows);
    }

    /// <summary>Which drill-in content block the view shows (tracks default).</summary>
    public bool IsArtistsSection => IsCollectionOpen && _openSection == LibrarySection.Artists;
    public bool IsAlbumsSection => IsCollectionOpen && _openSection == LibrarySection.Albums;
    public bool IsPlaylistSection => IsCollectionOpen && _openSection == LibrarySection.Playlist;
    public bool IsTracksSection => IsCollectionOpen && !IsArtistsSection && !IsAlbumsSection;

    /// <summary>Tracks list visible only for track sections with rows.</summary>
    public bool ShowTracksList => CollectionHasTracks && IsTracksSection;

    /// <summary>Empty-state text visible when the open section has no rows.</summary>
    public bool ShowDrillEmpty =>
        IsCollectionOpen &&
        ((IsTracksSection && !CollectionHasTracks) ||
         (IsArtistsSection && DrillArtists.Count == 0) ||
         (IsAlbumsSection && DrillAlbums.Count == 0));

    partial void OnCollectionHasTracksChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTracksList));
        OnPropertyChanged(nameof(ShowDrillEmpty));
    }

    private void UpdateDrillFlags()
    {
        OnPropertyChanged(nameof(IsArtistsSection));
        OnPropertyChanged(nameof(IsAlbumsSection));
        OnPropertyChanged(nameof(IsPlaylistSection));
        OnPropertyChanged(nameof(IsTracksSection));
        OnPropertyChanged(nameof(ShowTracksList));
        OnPropertyChanged(nameof(ShowDrillEmpty));
    }

    private async Task LoadPlaylistIntoTracksAsync(PlaylistEntity playlist)
    {
        // Fast tab flips / re-entries start overlapping loads; a slower older
        // one must not overwrite the fresh list when it finishes later.
        var generation = ++_playlistGeneration;
        try
        {
            IReadOnlyList<SongEntity> rows;
            if (playlist.IsEditable)
            {
                rows = await _database.GetPlaylistSongsAsync(playlist.Id);
            }
            else
            {
                // Account (YouTube) playlist: load live from the provider.
                var songs = await _musicProvider.GetPlaylistSongsAsync(playlist.BrowseId ?? playlist.Id);
                rows = songs.Select(s => s.ToEntity()).ToList();
            }

            // Hearts reflect the library like state, not the (possibly stale)
            // stored row flag — the same hydration every other list uses.
            await _likeService.EnsureLoadedAsync();
            if (generation != _playlistGeneration) return;
            _openPlaylistTracks = rows.Select(row =>
            {
                var song = row.ToSongItem();
                if (string.IsNullOrWhiteSpace(song.ThumbnailUrl) &&
                    !string.IsNullOrWhiteSpace(playlist.ThumbnailUrl))
                {
                    song.ThumbnailUrl = playlist.ThumbnailUrl!;
                }
                song.IsLiked = _likeService.IsLiked(song);
                // Same flag CollectionView rows use: shows the × button.
                song.IsLocalPlaylist = playlist.IsEditable;
                // Rows read live from YouTube know nothing about local files.
                song.IsDownloaded = _downloads.HasCopy(song);
                return song;
            }).ToList();
            _openPlaylistLoadedFor = playlist.Id;
            _genreSource = _openPlaylistTracks;
            ApplyPlaylistTabFilter();
            if (_openPlaylistTracks.Count > 0)
                _ = RefreshGenresAsync(_genreGeneration);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Library] Playlist load failed: {ex.Message}");
            // A newer load owns the list now; this failure is no news to it.
            if (generation != _playlistGeneration) return;
            CollectionHasTracks = false;
            CollectionEmptyMessage = "Could not load this playlist.";
        }
    }

    /// <summary>
    /// Shows the open playlist as one list: clips inline with a badge, song
    /// twins of listed clips hidden (otherwise every saved video appears twice).
    /// </summary>
    private void ApplyPlaylistTabFilter()
    {
        if (_openPlaylist == null) return;
        CollectionTracks.Clear();
        // Genre mix: when a genre chip is active, only its songs are shown.
        var source = _activeGenreKeywords.Length == 0
            ? _openPlaylistTracks
            : _openPlaylistTracks.Where(SongMatchesGenre).ToList();
        var videos = source.Where(IsVideoTrack).ToList();
        var songs = source.Where(s => !IsVideoTrack(s)).ToList();
        if (videos.Count > 0 && songs.Count > 0)
        {
            var clips = new SongIdentity.TrackSet(videos);
            songs = songs.Where(s => !clips.Contains(s)).ToList();
        }
        var shownSongs = new HashSet<SongItem>(songs);
        foreach (var song in FilterAndSort(source))
        {
            if (!song.IsVideoClip && !shownSongs.Contains(song)) continue;
            CollectionTracks.Add(song);
        }
        CollectionSubtitle = CountBoth(songs.Count, videos.Count);
        if (CollectionTracks.Count == 0)
            CollectionEmptyMessage = FilteredEmptyMessage()
                ?? (_activeGenreKeywords.Length > 0
                    ? "No songs of this genre in the playlist."
                    : "This playlist is empty. Add songs from a track's ⋯ menu.");
        CollectionHasTracks = CollectionTracks.Count > 0;
        PopulateCollectionViews();
    }

    /// <summary>Re-applies the Songs/Videos tab without refetching the playlist.</summary>
    private void RefreshPlaylistTab()
    {
        if (_openPlaylist == null || _openPlaylistLoadedFor != _openPlaylist.Id)
        {
            RefreshOpenCollection();
            return;
        }
        ApplyPlaylistTabFilter();
    }

    [RelayCommand]
    private void PlayCollection()
    {
        if (CollectionTracks.Count == 0) return;
        // One mixed queue plays as songs (pinned clips carry the same audio).
        // Single clip rows open as video through PlayCollectionTrack instead.
        _queueService.SetQueue(CollectionTracks.ToList(), 0);
    }

    [RelayCommand]
    private void ShuffleCollection()
    {
        if (CollectionTracks.Count == 0) return;
        var shuffled = CollectionTracks.OrderBy(_ => Guid.NewGuid()).ToList();
        _queueService.SetQueue(shuffled, 0);
    }

    /// <summary>Queues every catalogue track in the open collection for offline download.</summary>
    [RelayCommand]
    private async Task DownloadCollection()
    {
        foreach (var s in CollectionTracks.Where(s => !s.IsLocal).ToList())
            await _downloads.DownloadAsync(s);
    }

    /// <summary>
    /// Toggles a genre mix inside the open playlist. Chips come from the genres
    /// actually present (see RefreshGenresAsync); tapping the active chip clears.
    /// </summary>
    [RelayCommand]
    private void FilterGenre(GenreChip? chip)
    {
        if (chip == null) return;

        if (chip.IsActive)
        {
            chip.IsActive = false;
            _activeGenreKeywords = Array.Empty<string>();
            ApplyCurrentGenreFilter();
            return;
        }

        foreach (var g in Genres) g.IsActive = false;
        chip.IsActive = true;
        _activeGenreKeywords = chip.Keywords;
        ApplyCurrentGenreFilter();
    }

    private void ApplyCurrentGenreFilter()
    {
        if (IsPlaylistSection) ApplyPlaylistTabFilter();
        else ApplySectionGenreFilter();
    }

    // ---- Filter + sort inside an open collection (big libraries) ----

    /// <summary>Text filter for the open collection: title, artist or album.</summary>
    [ObservableProperty] private string _collectionFilter = string.Empty;

    /// <summary>Index into <see cref="SortOptions"/> (0 = the collection's own order).</summary>
    [ObservableProperty] private int _collectionSortIndex;

    public IReadOnlyList<string> SortOptions { get; } =
        new[] { "Default order", "Title", "Artist", "Album", "Longest first", "Shortest first" };

    partial void OnCollectionFilterChanged(string value) => ReapplyCollectionView();
    partial void OnCollectionSortIndexChanged(int value) => ReapplyCollectionView();

    private bool _resettingCollectionView;

    private void ReapplyCollectionView()
    {
        if (_resettingCollectionView || !IsCollectionOpen) return;
        if (IsArtistsSection || IsAlbumsSection) RefreshOpenCollection();
        else ApplyCurrentGenreFilter();
    }

    private bool IsFiltering => !string.IsNullOrWhiteSpace(CollectionFilter);

    private bool MatchesFilter(string? text)
    {
        if (!IsFiltering) return true;
        return !string.IsNullOrEmpty(text)
            && text.Contains(CollectionFilter.Trim(), StringComparison.CurrentCultureIgnoreCase);
    }

    private bool SongMatchesFilter(SongItem song)
        => MatchesFilter(song.Title) || MatchesFilter(song.ArtistDisplay) || MatchesFilter(song.AlbumTitle);

    /// <summary>Text filter + chosen order for a track list.</summary>
    private List<SongItem> FilterAndSort(IEnumerable<SongItem> source)
    {
        var list = IsFiltering ? source.Where(SongMatchesFilter) : source;
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        return (CollectionSortIndex switch
        {
            1 => list.OrderBy(s => s.Title, comparer),
            2 => list.OrderBy(s => s.ArtistDisplay, comparer).ThenBy(s => s.Title, comparer),
            3 => list.OrderBy(s => string.IsNullOrWhiteSpace(s.AlbumTitle) ? "\uffff" : s.AlbumTitle, comparer)
                     .ThenBy(s => s.Title, comparer),
            4 => list.OrderByDescending(s => s.DurationSeconds),
            5 => list.OrderBy(s => s.DurationSeconds <= 0 ? int.MaxValue : s.DurationSeconds),
            _ => list
        }).ToList();
    }

    /// <summary>Empty text for a filtered-out list (null = use the section's own).</summary>
    private string? FilteredEmptyMessage()
        => IsFiltering ? $"Nothing here matches “{CollectionFilter.Trim()}”." : null;

    /// <summary>
    /// Rebuilds the chips from the genres ACTUALLY present in the open
    /// collection (resolved per track, cached) — most common first. Runs in the
    /// background; a newer section/playlist supersedes it.
    /// </summary>
    private async Task RefreshGenresAsync(int generation)
    {
        // A copy: rows can be removed from the live list during the awaits below.
        var source = _genreSource.ToList();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rendered = 0;
        foreach (var song in source)
        {
            if (generation != _genreGeneration) return;
            var genre = await ResolveSongGenreAsync(song);
            if (string.IsNullOrWhiteSpace(genre)) continue;
            var key = genre.Trim();
            counts[key] = counts.GetValueOrDefault(key) + 1;
            if (counts.Count != rendered)
            {
                RenderGenres(generation, counts);
                rendered = counts.Count;
            }
        }
        if (generation != _genreGeneration) return;
        RenderGenres(generation, counts);
    }

    private void RenderGenres(int generation, Dictionary<string, int> counts)
    {
        // A superseded run must never repaint the chips of the collection the
        // user is actually looking at (A→B→A leaked B's genres onto A).
        if (generation != _genreGeneration) return;
        var active = _activeGenreKeywords;
        Genres.Clear();
        foreach (var genre in counts
                     .OrderByDescending(kv => kv.Value)
                     .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                     .Select(kv => kv.Key))
        {
            var chip = GenreChip.FromGenre(genre);
            if (active.Length > 0 && chip.Keywords.Any(k => active.Contains(k, StringComparer.Ordinal)))
                chip.IsActive = true;
            Genres.Add(chip);
        }
        HasGenreChips = Genres.Count > 0;
    }

    private async Task<string?> ResolveSongGenreAsync(SongItem song)
    {
        if (string.IsNullOrWhiteSpace(song.Id)) return null;
        if (_genreBySong.TryGetValue(song.Id, out var cached)) return cached;
        var artist = song.Artists.FirstOrDefault()?.Name ?? song.ArtistDisplay;
        try
        {
            var genre = await _genres.GetSongGenreAsync(song.Id, song.Title, artist);
            _genreBySong[song.Id] = genre;
            return genre;
        }
        catch
        {
            _genreBySong[song.Id] = null;
            return null;
        }
    }

    private bool SongMatchesGenre(SongItem song)
    {
        var genre = _genreBySong.TryGetValue(song.Id, out var g) ? g : null;
        if (string.IsNullOrWhiteSpace(genre)) return false;
        var lower = genre!.ToLowerInvariant();
        return _activeGenreKeywords.Any(k => lower.Contains(k, StringComparison.Ordinal));
    }

    private void ResetGenreFilter()
    {
        _genreGeneration++;
        foreach (var g in Genres) g.IsActive = false;
        Genres.Clear();
        HasGenreChips = false;
        _activeGenreKeywords = Array.Empty<string>();
        _genreBySong.Clear();
        _sectionTracks = new List<SongItem>();
        _genreSource = _sectionTracks;
    }

    [RelayCommand]
    private async Task ImportLocalAsync(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        LocalStatus = "Importing…";
        try
        {
            var count = await _localLibrary.ImportFolderAsync(folder);
            await ReloadAsync();
            LocalStatus = count == 0 ? "No supported audio files found." : $"Imported {count}.";
        }
        catch (Exception ex)
        {
            LocalStatus = $"Import failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CreatePlaylistAsync(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        try
        {
            // Playlists are local-only: created in SQLite under the current
            // account scope (each sign-in sees its own lists).
            await _database.CreatePlaylistAsync(title, _account.AccountScopeKey);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Library] CreatePlaylist failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RenamePlaylistAsync((PlaylistEntity Playlist, string Name)? request)
    {
        if (request is not { } req) return;
        try
        {
            await _database.RenamePlaylistAsync(req.Playlist.Id, req.Name);
            req.Playlist.Title = req.Name;
            // Force the bound item to refresh its title.
            var index = Playlists.IndexOf(req.Playlist);
            if (index >= 0) { Playlists[index] = req.Playlist; }
            // The grid shows Sections, not Playlists.
            BuildSections();
            if (IsCollectionOpen && string.Equals(_openPlaylist?.Id, req.Playlist.Id, StringComparison.Ordinal))
                CollectionTitle = req.Name;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Library] RenamePlaylist failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DuplicatePlaylistAsync(PlaylistEntity? playlist)
    {
        if (playlist == null) return;
        try
        {
            await _database.DuplicatePlaylistAsync(playlist.Id, $"{playlist.Title} (copy)");
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Library] DuplicatePlaylist failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task MovePlaylistAsync((PlaylistEntity Playlist, int Delta)? request)
    {
        if (request is not { } req) return;
        var index = Playlists.IndexOf(req.Playlist);
        var target = index + req.Delta;
        if (index < 0 || target < 0 || target >= Playlists.Count) return;

        Playlists.Move(index, target);
        try
        {
            await _database.ReorderPlaylistsAsync(Playlists.Select(p => p.Id).ToList());
            BuildSections();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Library] MovePlaylist failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeletePlaylistAsync(PlaylistEntity? playlist)
    {
        if (playlist == null) return;
        try
        {
            await _database.DeletePlaylistAsync(playlist.Id);
            Playlists.Remove(playlist);
            // The grid shows Sections, not Playlists — without this the card
            // lingers until the next reload instead of deleting instantly.
            var card = Sections.FirstOrDefault(c => c.Section == LibrarySection.Playlist
                && string.Equals(c.Playlist?.Id, playlist.Id, StringComparison.Ordinal));
            if (card != null) Sections.Remove(card);
            HasPlaylists = Playlists.Count > 0;
            if (IsCollectionOpen && string.Equals(_openPlaylist?.Id, playlist.Id, StringComparison.Ordinal))
                CloseCollection();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Library] DeletePlaylist failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes a track from the open editable playlist (drill-in × button).
    /// Drops every SameTrack duplicate so no twin survives, then rebuilds the
    /// unified list (a hidden song twin of a removed clip reappears).
    /// </summary>
    [RelayCommand]
    private async Task RemovePlaylistTrack(SongItem? song)
    {
        if (song == null || _openPlaylist == null || !_openPlaylist.IsEditable) return;
        try
        {
            var duplicates = _openPlaylistTracks.Where(s => SongIdentity.SameTrack(s, song)).ToList();
            if (duplicates.Count == 0) duplicates.Add(song);
            foreach (var duplicate in duplicates)
                await _database.RemoveSongFromPlaylistAsync(_openPlaylist.Id, duplicate.Id);
            foreach (var duplicate in duplicates)
                _openPlaylistTracks.Remove(duplicate);
            ApplyPlaylistTabFilter();
            // Repaint the grid card counts (entity is shared with the card).
            _openPlaylist.TrackCount = Math.Max(0, _openPlaylist.TrackCount - duplicates.Count);
            if (song.IsVideoClip)
                _openPlaylist.VideoCount = Math.Max(0, _openPlaylist.VideoCount - duplicates.Count(d => d.IsVideoClip));
            // The removed track's cover leaves the tile's mosaic too.
            _playlistCovers = await _database.GetLocalPlaylistCoversAsync();
            BuildSections();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Library] RemovePlaylistTrack failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Plays a track from the opened collection (used for every row). The
    /// CONTEXT decides: only a Videos tab plays the clip; every song list plays
    /// the song (a clip-tagged row must not hijack audio playback).
    /// </summary>
    public void PlayCollectionTrack(SongItem song)
    {
        // A real video row plays as the clip (Videos tab, or a clip row inline
        // in a playlist's songs); a song row joins the song queue.
        if ((IsVideoSection || song.IsVideoClip) && PlayVideosRequest != null)
        {
            PlayVideosRequest(new[] { song }, song);
            return;
        }
        var index = CollectionTracks.IndexOf(song);
        _queueService.SetQueue(CollectionTracks.ToList(), index < 0 ? 0 : index);
    }

    [RelayCommand]
    private async Task ToggleLikeAsync(SongItem? song)
    {
        if (song == null) return;
        try
        {
            await _likeService.ToggleAsync(song);
            if (song.IsLiked && LikedSongs.All(s => s.Id != song.Id)) LikedSongs.Add(song);
            // Unliking clears every version of the song: their rows go too.
            foreach (var gone in LikedSongs.Where(s => !_likeService.IsLiked(s)).ToList())
                LikedSongs.Remove(gone);

            if (IsCollectionOpen && _openSection == LibrarySection.Liked)
            {
                // The section's own list too: a row removed only from the view
                // came back as soon as a filter or sort was applied.
                _sectionTracks.RemoveAll(s => string.Equals(s.Id, song.Id, StringComparison.Ordinal)
                                              || !_likeService.IsLiked(s));
                if (song.IsLiked) _sectionTracks.Add(song);
                foreach (var gone in CollectionTracks.Where(s => !_likeService.IsLiked(s)).ToList())
                    CollectionTracks.Remove(gone);
                if (song.IsLiked && CollectionTracks.All(s => s.Id != song.Id)) CollectionTracks.Add(song);
                CollectionHasTracks = CollectionTracks.Count > 0;
                var (likedSongs, likedVideos) = SplitSongClips(LikedSongs);
                CollectionSubtitle = CountBoth(likedSongs.Count, likedVideos.Count);
            }
            HasLikedSongs = LikedSongs.Count > 0;
            BuildSections();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Library] ToggleLike failed: {ex.Message}");
        }
    }

    private static void RemoveById(ObservableCollection<SongItem> songs, string id)
    {
        var item = songs.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        if (item != null) songs.Remove(item);
    }
}
