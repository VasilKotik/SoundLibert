using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;

namespace SoundLibert.UI.ViewModels;

/// <summary>
/// A browsable collection (album, playlist or artist) with a header and a
/// track list. Routed by browse-id prefix: "MPREb…" = album, "UC…" = artist,
/// anything else = playlist.
/// </summary>
public partial class CollectionViewModel : ViewModelBase
{
    private readonly IMusicProvider _provider;
    private readonly IQueueService _queue;
    private readonly IMusicDatabase _database;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string? _subtitle;
    [ObservableProperty] private string? _thumbnailUrl;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasSongs;
    [ObservableProperty] private string _emptyMessage = "Nothing here yet.";

    /// <summary>True when this page is a user-created playlist (tracks removable).</summary>
    [ObservableProperty] private bool _isLocalPlaylist;

    /// <summary>The collection id currently shown (used to detect deletions).</summary>
    public string CollectionId { get; private set; } = string.Empty;

    /// <summary>Featured artist of the collection (for the "Go to artist" link).</summary>
    [ObservableProperty] private string? _artistId;
    [ObservableProperty] private string? _artistName;

    public bool CanGoToArtist => !string.IsNullOrWhiteSpace(ArtistId);

    partial void OnArtistIdChanged(string? value) => OnPropertyChanged(nameof(CanGoToArtist));

    public ObservableCollection<SongItem> Songs { get; } = new();

    /// <summary>What to go on to after the last track.</summary>
    public ObservableCollection<ShelfVm> MoreShelves { get; } = new();

    [ObservableProperty] private bool _hasMoreShelves;
    private int _moreShelvesGeneration;

    private readonly ILikeService _likeService;
    private readonly IAccountService? _account;
    private readonly IDownloadService _downloads;
    private readonly IGenreService _genres;
    private readonly IViewCountService _viewCounts;

    /// <summary>Genre chips shown on a playlist page (build a genre mix).</summary>
    public ObservableCollection<GenreChip> Genres { get; } = new();

    /// <summary>Full, unfiltered track list (the genre filter is applied on top).</summary>
    private List<SongItem> _allSongs = new();
    private string[] _activeGenreKeywords = Array.Empty<string>();
    private readonly Dictionary<string, string?> _genreBySong = new(StringComparer.Ordinal);
    private int _genreGeneration;

    /// <summary>True on a playlist page (chips live there, not on albums).</summary>
    public bool IsPlaylistPage => !IsAlbumPage;

    /// <summary>
    /// Set by the shell so tapping a clip row replays it as a clip.
    /// Song rows keep the plain queue path below.
    /// </summary>
    public Action<IReadOnlyList<SongItem>, SongItem?>? PlayVideoRequest { get; set; }

    /// <summary>True for a real (YouTube) playlist while signed in — saved to the account.</summary>
    [ObservableProperty] private bool _canSavePlaylist;
    [ObservableProperty] private bool _isPlaylistSaved;

    public string SavePlaylistText => IsPlaylistSaved ? "Saved" : "Save";

    partial void OnIsPlaylistSavedChanged(bool value) => OnPropertyChanged(nameof(SavePlaylistText));

    /// <summary>Empty state only once loading finished (it flashed under the skeleton).</summary>
    public bool ShowEmpty => !HasSongs && !IsLoading && !string.IsNullOrWhiteSpace(EmptyMessage);

    partial void OnEmptyMessageChanged(string value) => OnPropertyChanged(nameof(ShowEmpty));

    partial void OnHasSongsChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    /// <summary>Local heart for the open album (MPREb pages).</summary>
    [ObservableProperty] private bool _isAlbumLiked;

    /// <summary>Likes/unlikes the open album (local only, works signed out).</summary>
    [RelayCommand]
    private async Task ToggleAlbumLikeAsync()
    {
        if (!IsAlbumPage || string.IsNullOrWhiteSpace(CollectionId)) return;
        try
        {
            var liked = await _database.ToggleAlbumLikeAsync(new AlbumEntity
            {
                Id = CollectionId,
                Title = Title,
                ArtistsJson = System.Text.Json.JsonSerializer.Serialize(
                    string.IsNullOrWhiteSpace(ArtistName) ? Array.Empty<string>() : new[] { ArtistName }),
                ThumbnailUrl = ThumbnailUrl ?? string.Empty
            }, _account?.AccountScopeKey);
            IsAlbumLiked = liked;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Collection] Toggle album like failed: {ex.Message}");
        }
    }

    /// <summary>True when this page shows an album (heart visible).</summary>
    public bool IsAlbumPage => CollectionId.StartsWith("MPREb", StringComparison.Ordinal);

    /// <summary>Display-only hero eyebrow: Album / Playlist / Local playlist.</summary>
    public string KindLabel => IsLocalPlaylist ? "YOUR PLAYLIST" : IsAlbumPage ? "ALBUM" : "PLAYLIST";

    /// <summary>Display-only hero meta ("12 songs · 48 min").</summary>
    public string MetaLine =>
        Songs.Count == 0
            ? string.Empty
            : $"{Songs.Count} song{(Songs.Count == 1 ? string.Empty : "s")}" +
              (TotalMinutes > 0 ? $" · {TotalMinutes} min" : string.Empty);

    private int TotalMinutes => (int)Math.Round(Songs.Sum(s => s.DurationSeconds) / 60.0);

    private void RefreshHeaderMeta()
    {
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(MetaLine));
        OnPropertyChanged(nameof(IsPlaylistPage));
    }

    /// <summary>Saves/removes this playlist in the local library (never writes to YouTube).</summary>
    [RelayCommand]
    private async Task ToggleSavePlaylistAsync()
    {
        if (!CanSavePlaylist || string.IsNullOrWhiteSpace(CollectionId)) return;
        try
        {
            IsPlaylistSaved = await _database.TogglePlaylistSaveAsync(new PlaylistEntity
            {
                Id = CollectionId,
                Title = Title,
                BrowseId = CollectionId,
                ThumbnailUrl = ThumbnailUrl
            }, _account?.AccountScopeKey);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Collection] Save playlist failed: {ex.Message}");
        }
    }

    public CollectionViewModel(IMusicProvider provider, IQueueService queue, IMusicDatabase database,
        ILikeService likeService, IDownloadService downloads, IGenreService genres,
        IViewCountService viewCounts, IAccountService? account = null)
    {
        _provider = provider;
        _queue = queue;
        _database = database;
        _likeService = likeService;
        _downloads = downloads;
        _genres = genres;
        _viewCounts = viewCounts;
        _account = account;
    }

    /// <summary>Toggles a genre mix on the open playlist (own songs only).</summary>
    [RelayCommand]
    private void FilterGenre(GenreChip? chip)
    {
        if (chip == null) return;
        if (chip.IsActive)
        {
            chip.IsActive = false;
            _activeGenreKeywords = Array.Empty<string>();
            ApplyGenreFilter();
            return;
        }

        foreach (var g in Genres) g.IsActive = false;
        chip.IsActive = true;
        _activeGenreKeywords = chip.Keywords;
        ApplyGenreFilter();
    }

    /// <summary>
    /// Rebuilds the chips from the genres ACTUALLY present in the playlist:
    /// resolves each track's genre (cached after the first time) and keeps only
    /// the distinct styles, most common first. Runs in the background after the
    /// list is shown; a newer playlist supersedes it.
    /// </summary>
    private async Task RefreshGenresAsync(int generation)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rendered = 0;
        // A copy: a row can be removed from the live list during the awaits below.
        foreach (var song in _allSongs.ToList())
        {
            if (generation != _genreGeneration) return;
            var genre = await ResolveSongGenreAsync(song);
            if (string.IsNullOrWhiteSpace(genre)) continue;
            var key = genre.Trim();
            counts[key] = counts.GetValueOrDefault(key) + 1;
            // Show each new style as soon as it is found (don't wait for the
            // whole playlist to resolve).
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

    private void ApplyGenreFilter()
    {
        Songs.Clear();
        var src = _activeGenreKeywords.Length == 0
            ? _allSongs
            : _allSongs.Where(SongMatchesGenre).ToList();
        foreach (var s in src) Songs.Add(s);
        HasSongs = Songs.Count > 0;
        RefreshHeaderMeta();
    }

    private bool SongMatchesGenre(SongItem song)
    {
        var genre = _genreBySong.TryGetValue(song.Id, out var g) ? g : null;
        if (string.IsNullOrWhiteSpace(genre)) return false;
        var lower = genre!.ToLowerInvariant();
        return _activeGenreKeywords.Any(k => lower.Contains(k, StringComparison.Ordinal));
    }

    private string? FirstCover()
        => _allSongs.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.ThumbnailUrl))?.ThumbnailUrl;

    /// <summary>Local playlists: first distinct track covers for the 2×2 header mosaic.</summary>
    [ObservableProperty] private IReadOnlyList<string>? _headerCovers;

    private void RefreshHeaderCovers()
    {
        HeaderCovers = !IsLocalPlaylist
            ? null
            : _allSongs.Select(s => s.ThumbnailUrl)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .DistinctBy(u => u.Split('=')[0])
                .Take(4)
                .ToList();
    }

    private void ResetGenreFilter()
    {
        _genreGeneration++;
        foreach (var g in Genres) g.IsActive = false;
        Genres.Clear();
        _activeGenreKeywords = Array.Empty<string>();
        _genreBySong.Clear();
    }

    /// <summary>Queues every track in this collection for offline download.</summary>
    [RelayCommand]
    private async Task DownloadAll()
    {
        foreach (var s in Songs.Where(s => !s.IsLocal).ToList())
            await _downloads.DownloadAsync(s);
    }

    /// <summary>Other versions, more by the artist, what YouTube relates to it. A page without them still works.</summary>
    private async Task LoadMoreShelvesAsync(string id, int generation)
    {
        try
        {
            var extras = await _provider.GetCollectionExtrasAsync(id);
            if (generation != _moreShelvesGeneration) return;

            var shelves = extras.Shelves;
            ArtistPage? artist = null;
            if (IsAlbumPage)
            {
                // An album's rows do not name its artist: its page does.
                if (string.IsNullOrWhiteSpace(ArtistId) && !string.IsNullOrWhiteSpace(extras.Artist?.Id))
                {
                    ArtistId = extras.Artist.Id;
                    ArtistName = extras.Artist.Name;
                }
                if (string.IsNullOrWhiteSpace(Subtitle)) Subtitle = extras.Artist?.Name;

                if (!string.IsNullOrWhiteSpace(ArtistId))
                {
                    try { artist = await _provider.GetArtistPreviewAsync(ArtistId); }
                    catch (Exception ex) { Trace.WriteLine($"[Collection] Artist of {id} failed: {ex.Message}"); }
                    if (generation != _moreShelvesGeneration) return;
                }
            }

            var versions = shelves.FirstOrDefault(s => s.Title.Contains("version", StringComparison.OrdinalIgnoreCase));
            if (versions != null) MoreShelves.Add(ShelfVm.From(versions));

            if (artist != null)
            {
                var more = artist.Albums.Concat(artist.Singles)
                    .Where(a => !string.Equals(a.Id, id, StringComparison.Ordinal))
                    .DistinctBy(a => a.Id)
                    .Take(ShelfVm.RailLimit)
                    .Select(ShelfItems.Album)
                    .ToList();
                if (more.Count > 0)
                    MoreShelves.Add(ShelfVm.From(new Shelf { Title = $"More by {artist.Artist.Name}", Items = more }));
                if (artist.RelatedArtists.Count > 0)
                    MoreShelves.Add(ShelfVm.From(new Shelf
                    {
                        Title = "Fans might also like",
                        Items = artist.RelatedArtists.Take(ShelfVm.RailLimit).Select(ShelfItems.Artist).ToList()
                    }));
            }

            foreach (var shelf in shelves.Where(s => !ReferenceEquals(s, versions)))
                MoreShelves.Add(ShelfVm.From(shelf));

            HasMoreShelves = MoreShelves.Count > 0;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Collection] More shelves failed for {id}: {ex.Message}");
        }
    }

    public async Task LoadBrowseAsync(string id, string title, string? thumbnail, string? subtitle)
    {
        // Per-scope hearts first (idempotent) so hydration below reads this scope.
        try { await _database.ApplyLikeScopeAsync(_account?.AccountScopeKey); } catch { }
        CollectionId = id;
        OnPropertyChanged(nameof(IsAlbumPage));
        RefreshHeaderMeta();
        IsLocalPlaylist = false;
        // Reset per-collection state: otherwise a "Saved" label from the previous
        // playlist leaks onto the next one.
        IsPlaylistSaved = false;
        IsAlbumLiked = false;
        // Saving (local-only, never written to YouTube) is offered for real playlists.
        CanSavePlaylist = !id.StartsWith("MPREb", StringComparison.Ordinal) &&
                          !id.StartsWith(PlaylistEntity.LocalIdPrefix, StringComparison.Ordinal);
        Title = title;
        ThumbnailUrl = thumbnail;
        Subtitle = subtitle;
        IsLoading = true;
        var moreShelves = ++_moreShelvesGeneration;
        MoreShelves.Clear();
        HasMoreShelves = false;

        try
        {
            IReadOnlyList<SongItem> songs;

            if (id.StartsWith("MPREb", StringComparison.Ordinal))
            {
                // Read the album's own track shelf; fall back to its playlist id.
                songs = await _provider.GetAlbumTracksAsync(id);
                if (songs.Count == 0)
                {
                    var album = await _provider.GetAlbumAsync(id);
                    if (!string.IsNullOrWhiteSpace(album?.PlaylistId))
                        songs = await _provider.GetAlbumSongsAsync(album.PlaylistId);
                    if (string.IsNullOrWhiteSpace(Subtitle)) Subtitle = album?.ArtistDisplay;
                }
                EmptyMessage = "This album has no playable tracks.";
            }
            else
            {
                songs = await _provider.GetPlaylistSongsAsync(id);
                EmptyMessage = "This playlist has no playable tracks.";
            }

            await _likeService.EnsureLoadedAsync();
            Songs.Clear();
            foreach (var song in songs)
            {
                // Album tracks carry no thumbnail of their own — inherit the
                // album/playlist cover so every row shows artwork.
                if (string.IsNullOrWhiteSpace(song.ThumbnailUrl) &&
                    !string.IsNullOrWhiteSpace(ThumbnailUrl))
                {
                    song.ThumbnailUrl = ThumbnailUrl!;
                }
                song.IsLiked = _likeService.IsLiked(song);
                song.IsDownloaded = _downloads.HasCopy(song);
                song.IsLocalPlaylist = false;
                Songs.Add(song);
            }

            _allSongs = Songs.ToList();
            ResetGenreFilter();

            // An album header said "Album" under the ALBUM eyebrow when the
            // caller had no artist to pass: show the album's artist instead.
            if (IsAlbumPage && (string.IsNullOrWhiteSpace(Subtitle)
                    || Subtitle is "Album" or "Single" or "EP" or "Playlist"))
            {
                var artists = Songs.SelectMany(s => s.Artists).Select(a => a.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key)
                    .Take(2)
                    .ToList();
                Subtitle = artists.Count > 0 ? string.Join(", ", artists) : null;
            }

            var featuredArtist = Songs
                .SelectMany(s => s.Artists)
                .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Id));
            ArtistId = featuredArtist?.Id;
            ArtistName = featuredArtist?.Name;

            if (IsAlbumPage)
            {
                try { IsAlbumLiked = await _database.IsAlbumLikedAsync(id, _account?.AccountScopeKey); }
                catch (Exception ex) { Trace.WriteLine($"[Collection] Album like state failed: {ex.Message}"); }
            }
            else if (CanSavePlaylist)
            {
                try { IsPlaylistSaved = await _database.IsPlaylistSavedAsync(id); }
                catch (Exception ex) { Trace.WriteLine($"[Collection] Playlist saved state failed: {ex.Message}"); }
            }

            ApplyGenreFilter();

            if (_allSongs.Count > 0) _ = LoadMoreShelvesAsync(id, moreShelves);

            // Only playlists show genre chips, so only they pay for resolution.
            if (IsPlaylistPage && _allSongs.Count > 0)
                _ = RefreshGenresAsync(_genreGeneration);

            // Spotify-style global view counts (cached first, resolved after).
            if (_allSongs.Count > 0)
            {
                await _viewCounts.HydrateAsync(_allSongs);
                _ = _viewCounts.ResolveMissingAsync(_allSongs);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Collection] Load failed for {id}: {ex.Message}");
            EmptyMessage = "Could not load this collection.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Loads a user-created (local) playlist from SQLite.</summary>
    public async Task LoadLocalPlaylistAsync(string id, string title, string? thumbnail)
    {
        try { await _database.ApplyLikeScopeAsync(_account?.AccountScopeKey); } catch { }
        CollectionId = id;
        IsLocalPlaylist = true;
        RefreshHeaderMeta();
        IsPlaylistSaved = false;
        CanSavePlaylist = false;
        Title = title;
        ThumbnailUrl = thumbnail;
        Subtitle = "Your playlist";
        IsLoading = true;

        try
        {
            var rows = await _database.GetPlaylistSongsAsync(id);
            await _likeService.EnsureLoadedAsync();
            Songs.Clear();
            foreach (var row in rows)
            {
                var song = row.ToSongItem();
                if (string.IsNullOrWhiteSpace(song.ThumbnailUrl) &&
                    !string.IsNullOrWhiteSpace(ThumbnailUrl))
                {
                    song.ThumbnailUrl = ThumbnailUrl!;
                }
                song.IsLiked = _likeService.IsLiked(song);
                song.IsDownloaded = _downloads.HasCopy(song);
                song.IsLocalPlaylist = true;
                Songs.Add(song);
            }
            _allSongs = Songs.ToList();
            // The header showed an empty frame while the Library tile already
            // used the first track's cover: use the same cover here.
            if (string.IsNullOrWhiteSpace(ThumbnailUrl))
                ThumbnailUrl = FirstCover();
            RefreshHeaderCovers();
            ResetGenreFilter();
            ApplyGenreFilter();
            EmptyMessage = "This playlist is empty. Add songs from a track's ⋯ menu.";
            if (_allSongs.Count > 0)
            {
                _ = RefreshGenresAsync(_genreGeneration);
                await _viewCounts.HydrateAsync(_allSongs);
                _ = _viewCounts.ResolveMissingAsync(_allSongs);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Collection] Local playlist load failed for {id}: {ex.Message}");
            EmptyMessage = "Could not load this playlist.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Moves a track up or down inside a local playlist (reorder).</summary>
    [RelayCommand]
    private async Task MoveSong((SongItem Song, int Delta)? request)
    {
        if (request is not { } req || !IsLocalPlaylist) return;

        var index = Songs.IndexOf(req.Song);
        var target = index + req.Delta;
        if (index < 0 || target < 0 || target >= Songs.Count) return;

        try
        {
            // The view may be genre-filtered: its index is not the playlist
            // position. Move next to the neighbour in the full list instead.
            var fullIndex = _allSongs.IndexOf(req.Song);
            var fullTarget = _allSongs.IndexOf(Songs[target]);
            if (fullIndex < 0 || fullTarget < 0) return;

            await _database.MovePlaylistSongAsync(CollectionId, req.Song.Id, fullTarget);
            _allSongs.RemoveAt(fullIndex);
            _allSongs.Insert(fullTarget, req.Song);
            Songs.Move(index, target);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Collection] MoveSong failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RemoveFromPlaylist(SongItem? song)
    {
        if (song == null || !IsLocalPlaylist) return;
        try
        {
            // Remove every row for the same underlying track (ATV/OMV copies),
            // otherwise a duplicate entry survives and looks like the deleted
            // song came back.
            // Match against the FULL list: Songs may be a genre-filtered view,
            // and a row left in _allSongs came back when the filter changed.
            var duplicates = _allSongs.Where(s => SongIdentity.SameTrack(s, song)).ToList();
            foreach (var duplicate in duplicates)
                await _database.RemoveSongFromPlaylistAsync(CollectionId, duplicate.Id);
            foreach (var duplicate in duplicates)
            {
                Songs.Remove(duplicate);
                _allSongs.Remove(duplicate);
            }
            HasSongs = Songs.Count > 0;

            // Header cover and genre chips follow the remaining tracks (an
            // emptied playlist kept the removed song's cover and chips).
            if (duplicates.Any(d => string.Equals(d.ThumbnailUrl, ThumbnailUrl, StringComparison.Ordinal)))
                ThumbnailUrl = FirstCover();
            RefreshHeaderCovers();
            if (_allSongs.Count == 0)
            {
                ResetGenreFilter();
            }
            else
            {
                _genreGeneration++;
                _ = RefreshGenresAsync(_genreGeneration);
            }
            RefreshHeaderMeta();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Collection] RemoveFromPlaylist failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void PlaySong(SongItem? song)
    {
        if (song == null || Songs.Count == 0) return;
        // Album pages are song-oriented: a row always plays as the SONG (YouTube
        // tags some album tracks as videos; the Song/Video toggle watches them).
        // A PLAYLIST can hold real video rows — those play as the clip.
        if (IsPlaylistPage && song.IsVideoClip && PlayVideoRequest != null)
        {
            PlayVideoRequest(new[] { song }, song);
            return;
        }
        var index = Songs.IndexOf(song);
        // Temporary origin log: catches which tap moves the queue.
        System.Diagnostics.Debug.WriteLine($"[Queue] CollectionPlay '{song.Title}' index={index} id={song.Id}");
        _queue.SetQueue(Songs.ToList(), index < 0 ? 0 : index);
    }

    [RelayCommand]
    private void PlayAll()
    {
        if (Songs.Count > 0) _queue.SetQueue(Songs.ToList(), 0);
    }

    /// <summary>Starts the collection shuffled (self-contained: no queue side effects).</summary>
    [RelayCommand]
    private void ShuffleAll()
    {
        if (Songs.Count == 0) return;
        var shuffled = Songs.OrderBy(_ => Guid.NewGuid()).ToList();
        _queue.SetQueue(shuffled, 0);
    }

    [RelayCommand]
    private async Task ToggleLike(SongItem? song)
    {
        if (song == null) return;
        try
        {
                await _likeService.ToggleAsync(song);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Collection] ToggleLike failed: {ex.Message}");
        }
    }
}
