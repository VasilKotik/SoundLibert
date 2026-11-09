using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundLibert.Domain.Enums;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;

namespace SoundLibert.UI.ViewModels;

public partial class SearchViewModel : ViewModelBase
{
    private readonly IMusicProvider _musicProvider;
    private readonly IQueueService _queueService;
    private readonly IAudioPlayer _audioPlayer;
    private readonly IMusicDatabase _musicDatabase;
    private readonly IDownloadService _downloads;
    private readonly IViewCountService _viewCounts;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private SearchResult? _results;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private SearchFilter _selectedFilter = SearchFilter.All;

    [ObservableProperty]
    private bool _hasResults;

    [ObservableProperty]
    private bool _hasSongs;

    [ObservableProperty]
    private bool _hasVideos;

    [ObservableProperty]
    private bool _hasAlbums;

    [ObservableProperty]
    private bool _hasArtists;

    [ObservableProperty]
    private bool _hasPlaylists;

    [ObservableProperty]
    private bool _showEmptyPrompt;

    public ObservableCollection<SongItem> Songs { get; } = new();
    public ObservableCollection<SongItem> Videos { get; } = new();
    public ObservableCollection<AlbumItem> Albums { get; } = new();
    public ObservableCollection<ArtistItem> Artists { get; } = new();
    public ObservableCollection<PlaylistItem> Playlists { get; } = new();
    public ObservableCollection<string> RecentSearches { get; } = new();
    public ObservableCollection<string> Suggestions { get; } = new();

    /// <summary>Things to open right away while typing: an artist, a song.</summary>
    public ObservableCollection<ShelfCard> SuggestionCards { get; } = new();

    /// <summary>The "All" results, one shelf of each kind.</summary>
    public ObservableCollection<ShelfVm> OverviewShelves { get; } = new();

    /// <summary>What the page offers before any search: moods, what is moving, what is new.</summary>
    public ObservableCollection<ShelfVm> DiscoverShelves { get; } = new();

    /// <summary>The songs shown with the best match.</summary>
    public ObservableCollection<ShelfCard> TopResultSongs { get; } = new();

    [ObservableProperty] private ShelfCard? _topResult;
    [ObservableProperty] private bool _hasTopResult;
    [ObservableProperty] private bool _hasTopResultSongs;
    [ObservableProperty] private bool _hasOverview;
    [ObservableProperty] private bool _isLoadingDiscover;

    /// <summary>A round picture for an artist.</summary>
    public Avalonia.CornerRadius TopResultRadius => new(TopResult?.IsArtist == true ? 60 : 14);

    /// <summary>The song list of the Songs filter ("All" shows them on a shelf).</summary>
    public bool ShowSongList => HasSongs && SelectedFilter != SearchFilter.All;

    /// <summary>Moods and charts stay until there are results to show.</summary>
    public bool ShowDiscover => !IsSearching && !HasResults;

    partial void OnTopResultChanged(ShelfCard? value) => OnPropertyChanged(nameof(TopResultRadius));
    partial void OnHasSongsChanged(bool value) => OnPropertyChanged(nameof(ShowSongList));
    partial void OnSelectedFilterChanged(SearchFilter value) => OnPropertyChanged(nameof(ShowSongList));
    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(ShowDiscover));
    partial void OnHasResultsChanged(bool value) => OnPropertyChanged(nameof(ShowDiscover));

    /// <summary>Human summary of the current results ("12 songs · 3 albums").</summary>
    public string ResultsSummary
    {
        get
        {
            var parts = new List<string>();
            if (Songs.Count > 0) parts.Add($"{Songs.Count} song{(Songs.Count == 1 ? "" : "s")}");
            if (Videos.Count > 0) parts.Add($"{Videos.Count} video{(Videos.Count == 1 ? "" : "s")}");
            if (Albums.Count > 0) parts.Add($"{Albums.Count} album{(Albums.Count == 1 ? "" : "s")}");
            if (Artists.Count > 0) parts.Add($"{Artists.Count} artist{(Artists.Count == 1 ? "" : "s")}");
            if (Playlists.Count > 0) parts.Add($"{Playlists.Count} playlist{(Playlists.Count == 1 ? "" : "s")}");
            return parts.Count == 0 ? string.Empty : string.Join(" · ", parts);
        }
    }

    [ObservableProperty]
    private bool _hasRecentSearches;

    [ObservableProperty]
    private bool _hasSuggestions;

    private CancellationTokenSource? _suggestCts;
    private CancellationTokenSource? _searchCts;

    /// <summary>
    /// Cancels and disposes a CTS and clears the field, so a stale reference
    /// to an already-disposed source can never be cancelled again.
    /// </summary>
    private static void CancelAndDispose(ref CancellationTokenSource? cts)
    {
        var old = cts;
        cts = null;
        if (old == null) return;
        try { old.Cancel(); } catch (ObjectDisposedException) { }
        try { old.Dispose(); } catch { }
    }

    /// <summary>Debounced live search suggestions as the user types.</summary>
    partial void OnQueryChanged(string value)
    {
        // An emptied field is back to exploring.
        if (string.IsNullOrWhiteSpace(value)) ClearResults();
        _ = UpdateSuggestionsAsync(value);
    }

    private void ClearResults()
    {
        CancelAndDispose(ref _searchCts);
        IsSearching = false;
        HasResults = false;
        HasSongs = HasVideos = HasAlbums = HasArtists = HasPlaylists = false;
        ShowEmptyPrompt = false;
        Songs.Clear();
        Videos.Clear();
        Albums.Clear();
        Artists.Clear();
        Playlists.Clear();
        OverviewShelves.Clear();
        HasOverview = false;
        TopResult = null;
        HasTopResult = false;
        TopResultSongs.Clear();
        HasTopResultSongs = false;
    }

    private async Task UpdateSuggestionsAsync(string query)
    {
        CancelAndDispose(ref _suggestCts);

        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
        {
            Suggestions.Clear();
            SuggestionCards.Clear();
            HasSuggestions = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _suggestCts = cts;
        try
        {
            await Task.Delay(220, cts.Token);
            var found = await _musicProvider.GetSuggestionsAsync(query.Trim(), cts.Token);
            if (cts.IsCancellationRequested) return;

            Suggestions.Clear();
            foreach (var text in found.Texts.Distinct(StringComparer.OrdinalIgnoreCase).Take(6)) Suggestions.Add(text);
            SuggestionCards.Clear();
            var cards = found.Items.Take(5).Select(i => new ShelfCard(i)).ToList();
            _ = new RowShelfVm(cards);
            foreach (var card in cards) SuggestionCards.Add(card);
            HasSuggestions = Suggestions.Count + SuggestionCards.Count > 0;
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer keystroke
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Search] Suggestions failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task UseSuggestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Query = text;
        await SearchAsync();
    }

    private readonly ILikeService _likeService;
    private readonly IAccountService _account;

    public SearchViewModel(IMusicProvider musicProvider, IQueueService queueService,
        IAudioPlayer audioPlayer, IMusicDatabase musicDatabase, ILikeService likeService,
        IAccountService account, IDownloadService downloads, IViewCountService viewCounts)
    {
        _musicProvider = musicProvider;
        _queueService = queueService;
        _audioPlayer = audioPlayer;
        _musicDatabase = musicDatabase;
        _likeService = likeService;
        _account = account;
        _downloads = downloads;
        _viewCounts = viewCounts;
    }

    [RelayCommand]
    private async Task ToggleLikeAsync(SongItem? song)
    {
        if (song == null) return;
        try
        {
            await _likeService.ToggleAsync(song);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Search] ToggleLike failed: {ex.Message}");
        }
    }

    public override void Initialize()
    {
        _ = LoadRecentAsync();
        _ = LoadDiscoverAsync();
    }

    private static readonly TimeSpan DiscoverTtl = TimeSpan.FromMinutes(10);
    private DateTime _discoverLoadedAt = DateTime.MinValue;

    /// <summary>What there is to explore before any search.</summary>
    private async Task LoadDiscoverAsync()
    {
        if (IsLoadingDiscover || DateTime.UtcNow - _discoverLoadedAt < DiscoverTtl) return;

        IsLoadingDiscover = DiscoverShelves.Count == 0;
        try
        {
            var country = ListenerCountry.Current;
            var moodsTask = _musicProvider.GetMoodsAndGenresAsync();
            var exploreTask = _musicProvider.GetExplorePageAsync();
            var chartsTask = _musicProvider.GetChartsAsync(country.Code);
            await Task.WhenAll(
                moodsTask.ContinueWith(_ => { }, TaskScheduler.Default),
                exploreTask.ContinueWith(_ => { }, TaskScheduler.Default),
                chartsTask.ContinueWith(_ => { }, TaskScheduler.Default));

            var moods = moodsTask.IsCompletedSuccessfully ? moodsTask.Result : Array.Empty<MoodCategory>();
            var explore = exploreTask.IsCompletedSuccessfully ? exploreTask.Result : null;
            var charts = chartsTask.IsCompletedSuccessfully ? chartsTask.Result : Array.Empty<Shelf>();

            var shelves = new List<ShelfVm>();
            if (moods.Count > 0)
                shelves.Add(new TileGridVm
                {
                    Title = "Moods & moments",
                    Tiles = moods[0].Tiles.Select(t => new MoodTileVm(t)).ToList()
                });
            if (explore?.Trending is { Items.Count: > 0 } trending)
                shelves.Add(ShelfVm.From(trending, "Trending now"));
            if (charts.FirstOrDefault(c => c.Title.Contains("artist", StringComparison.OrdinalIgnoreCase)) is { Items.Count: > 0 } artists)
                shelves.Add(ShelfVm.From(artists, country.Name != null ? $"Top artists in {country.Name}" : "Top artists"));
            if (explore?.NewReleases is { Items.Count: > 0 } releases)
                shelves.Add(ShelfVm.From(releases, "New releases"));
            if (explore?.NewVideos is { Items.Count: > 0 } videos)
                shelves.Add(ShelfVm.From(videos, "New music videos",
                    openFeed: () => new SoundLibert.Domain.Video.SinglePageVideoFeed(ct => _musicProvider.GetNewVideosAsync(ct))));
            if (moods.Count > 1)
                shelves.Add(new TileGridVm
                {
                    Title = "Genres",
                    Tiles = moods[1].Tiles.Select(t => new MoodTileVm(t)).ToList()
                });

            if (shelves.Count == 0) return;
            DiscoverShelves.Clear();
            foreach (var shelf in shelves) DiscoverShelves.Add(shelf);
            _discoverLoadedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Search] discover failed: {ex.Message}");
        }
        finally
        {
            IsLoadingDiscover = false;
        }
    }

    /// <summary>A shelf's "Show all": the same search, in that kind only.</summary>
    private void ShowFilter(SearchFilter filter)
    {
        SelectedFilter = filter;
        _ = SearchAsync();
    }

    private void ShowOverview(SearchOverview overview, HashSet<string> liked)
    {
        foreach (var song in overview.Songs.SelectMany(s => s.Items).Where(i => i.Song != null).Select(i => i.Song!))
        {
            song.IsLiked = liked.Contains(song.Id);
            song.IsDownloaded = _downloads.IsDownloaded(song.Id);
            Songs.Add(song);
        }

        if (overview.TopResult is { } best)
        {
            var bestCard = new ShelfCard(best);
            _ = new RowShelfVm(new[] { bestCard });
            TopResult = bestCard;
        }
        HasTopResult = TopResult != null;

        var songs = overview.TopResultSongs.Select(i => new ShelfCard(i)).ToList();
        _ = new RowShelfVm(songs);
        foreach (var card in songs) TopResultSongs.Add(card);
        HasTopResultSongs = TopResultSongs.Count > 0;

        void Add(IReadOnlyList<Shelf> shelves, SearchFilter filter)
        {
            foreach (var shelf in shelves)
            {
                var vm = ShelfVm.From(shelf);
                vm.ShowAllAction = () => ShowFilter(filter);
                OverviewShelves.Add(vm);
            }
        }

        Add(overview.Songs, SearchFilter.Songs);
        Add(overview.Artists, SearchFilter.Artists);
        Add(overview.Videos, SearchFilter.Videos);
        Add(overview.Albums, SearchFilter.Albums);
        Add(overview.Playlists, SearchFilter.CommunityPlaylists);
        HasOverview = OverviewShelves.Count > 0;
    }

    private async Task LoadRecentAsync()
    {
        try
        {
            try { await _musicDatabase.ApplyLikeScopeAsync(_account.AccountScopeKey); } catch { }
            var recent = await _musicDatabase.GetSearchHistoryAsync(8, _account.AccountScopeKey);
            RecentSearches.Clear();
            foreach (var q in recent) RecentSearches.Add(q);
            HasRecentSearches = RecentSearches.Count > 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Search] Load recent failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task UseRecentSearch(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        Query = query;
        await SearchAsync();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(Query)) return;

        // A newer search supersedes this one: rapid re-searches / filter flips
        // must not let a slow older response overwrite the fresh results.
        CancelAndDispose(ref _searchCts);
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        // Read now: a newer search disposes the source, and the property then throws.
        var token = cts.Token;
        var query = Query.Trim();

        CancelAndDispose(ref _suggestCts);
        HasSuggestions = false;
        Suggestions.Clear();
        SuggestionCards.Clear();

        IsSearching = true;
        HasResults = false;
        HasSongs = HasVideos = HasAlbums = HasArtists = HasPlaylists = false;
        ShowEmptyPrompt = false;
        Songs.Clear();
        Videos.Clear();
        Albums.Clear();
        Artists.Clear();
        Playlists.Clear();
        OverviewShelves.Clear();
        HasOverview = false;
        TopResult = null;
        HasTopResult = false;
        TopResultSongs.Clear();
        HasTopResultSongs = false;
        try
        {
            if (SelectedFilter == SearchFilter.All)
            {
                var overview = await _musicProvider.SearchOverviewAsync(query, token);
                if (token.IsCancellationRequested) return;
                ShowOverview(overview, (await _musicDatabase.GetLikedSongIdsAsync()).ToHashSet(StringComparer.Ordinal));
                if (token.IsCancellationRequested) return;

                HasSongs = Songs.Count > 0;
                HasResults = overview.HasAnything;
                ShowEmptyPrompt = !HasResults;
                OnPropertyChanged(nameof(ResultsSummary));
                await _musicDatabase.AddSearchHistoryAsync(query, _account.AccountScopeKey);
                await LoadRecentAsync();
                return;
            }

            Results = await _musicProvider.SearchAsync(query, SelectedFilter, token);
            if (token.IsCancellationRequested) return;

            // Hydrate the unified liked state; account-origin metadata stays internal.
            var liked = await _musicDatabase.GetLikedSongIdsAsync();
            if (token.IsCancellationRequested) return;
            // The Videos filter returns video rows (clips); every other filter
            // returns songs. They are kept in separate lists so the view can
            // label them correctly and play clips as video.
            var intoVideos = SelectedFilter == SearchFilter.Videos;
            foreach (var song in Results.Songs)
            {
                song.IsLiked = liked.Contains(song.Id);
                song.IsDownloaded = _downloads.IsDownloaded(song.Id);
                if (intoVideos) Videos.Add(song);
                else Songs.Add(song);
            }
            foreach (var album in Results.Albums) Albums.Add(album);
            foreach (var artist in Results.Artists) Artists.Add(artist);
            foreach (var playlist in Results.Playlists) Playlists.Add(playlist);

            // Spotify-style global view counts: cached first, then resolved in
            // the background (capped) so the list appears instantly.
            var viewRows = Songs.Concat(Videos).ToList();
            if (viewRows.Count > 0)
            {
                await _viewCounts.HydrateAsync(viewRows, token);
                _ = _viewCounts.ResolveMissingAsync(viewRows, token);
            }

            HasSongs = Songs.Count > 0;
            HasVideos = Videos.Count > 0;
            HasAlbums = Albums.Count > 0;
            HasArtists = Artists.Count > 0;
            HasPlaylists = Playlists.Count > 0;
            HasResults = HasSongs || HasVideos || HasAlbums || HasArtists || HasPlaylists;
            ShowEmptyPrompt = !HasResults;
            OnPropertyChanged(nameof(ResultsSummary));

            await _musicDatabase.AddSearchHistoryAsync(query, _account.AccountScopeKey);
            await LoadRecentAsync();
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer search
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Search failed: {ex.Message}");
            ReportError?.Invoke("Couldn't search right now. Check your connection and try again.");
            if (_searchCts == cts) HasResults = false;
        }
        finally
        {
            // A newer run owns the spinner now — don't clear it from here.
            if (_searchCts == cts) IsSearching = false;
        }
    }

    /// <summary>
    /// Set by the shell so tapping a clip row replays it as a clip.
    /// Song rows keep the plain queue path below.
    /// </summary>
    public Action<IReadOnlyList<SongItem>, SongItem?>? PlayVideoRequest { get; set; }

    /// <summary>Set by the shell: reports a friendly, user-facing error.</summary>
    public Action<string>? ReportError { get; set; }

    [RelayCommand]
    private async Task PlaySongAsync(SongItem song)
    {
        // Only the Videos results are clips to watch. A row from the Songs
        // results always plays as the SONG — a clip-tagged row must never
        // hijack playback (the Song/Video toggle is how a clip is watched).
        if (Videos.Contains(song) && PlayVideoRequest != null)
        {
            PlayVideoRequest(new[] { song }, song);
            return;
        }
        var index = Songs.IndexOf(song);
        if (index < 0) index = 0;
        System.Diagnostics.Debug.WriteLine($"PlaySongAsync: {song.Title} (index={index})");
        _queueService.SetQueue(Songs.ToList(), index);
    }
}
