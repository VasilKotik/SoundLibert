using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundLibert.Domain.Enums;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;
using SoundLibert.Domain.Video;
using HomeFeedCacheStore = SoundLibert.UI.Services.HomeFeedCache;

namespace SoundLibert.UI.ViewModels;

/// <summary>
/// The Start screen. Two streams take turns: what YouTube Music has for this listener
/// (quick picks, hits, local shelves), and what the app builds from their listening and
/// the charts (similar songs, new from their artists, trending, moods). The personal part
/// picks different seeds every time, the feed reloads when it is a few minutes old, and
/// the page keeps growing as it scrolls. Everything loads asynchronously, so the page
/// appears at once.
/// </summary>
public partial class HomeViewModel : ViewModelBase
{
    private readonly IQueueService _queue;
    private readonly IMusicDatabase _database;
    private readonly IMusicProvider _music;
    private readonly IAccountService _account;

    [ObservableProperty] private string _greeting = "Welcome";
    [ObservableProperty] private string _subtitle = "Your music world, ready when you are.";
    [ObservableProperty] private bool _isLoadingSections;
    [ObservableProperty] private bool _isLoadingMore;
    [ObservableProperty] private bool _hasHomeSections;
    [ObservableProperty] private bool _hasRecentlyPlayed;
    [ObservableProperty] private bool _hasTopArtists;
    [ObservableProperty] private bool _hasQuickPicks;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _hasChips;
    [ObservableProperty] private bool _hasSpotlight;
    [ObservableProperty] private string _listeningTimeText = "0m";
    [ObservableProperty] private string _totalPlaysText = "0";
    [ObservableProperty] private IReadOnlyList<SpotlightEntry>? _spotlight;

    /// <summary>Under the greeting: time listened once there is any, a welcome before that.</summary>
    public string HeaderLine => string.IsNullOrWhiteSpace(ListeningTimeText) ||
                                ListeningTimeText is "0m" or "0s" or "0 s" or "0 min"
        ? Subtitle
        : $"{ListeningTimeText} listened";

    partial void OnListeningTimeTextChanged(string value) => OnPropertyChanged(nameof(HeaderLine));
    partial void OnSubtitleChanged(string value) => OnPropertyChanged(nameof(HeaderLine));

    /// <summary>Shelf skeletons while the very first feed loads (no cache yet).</summary>
    public bool ShowFeedSkeleton => IsLoadingSections && !HasHomeSections;

    /// <summary>The "search for something" empty state — only once loading is over.</summary>
    public bool ShowEmptyFeed => !IsLoadingSections && !HasHomeSections;

    partial void OnIsLoadingSectionsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFeedSkeleton));
        OnPropertyChanged(nameof(ShowEmptyFeed));
    }

    partial void OnHasHomeSectionsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFeedSkeleton));
        OnPropertyChanged(nameof(ShowEmptyFeed));
    }

    public ObservableCollection<SongItem> RecentlyPlayed { get; } = new();

    /// <summary>Compact "jump back in" tiles (top 6 recent tracks).</summary>
    public ObservableCollection<SongItem> QuickPicks { get; } = new();

    public ObservableCollection<HomeArtistItem> TopArtists { get; } = new();

    /// <summary>The moods the feed can be narrowed to.</summary>
    public ObservableCollection<HomeChipVm> Chips { get; } = new();

    /// <summary>Everything below the personal part, in the order it shows.</summary>
    public ObservableCollection<ShelfVm> Shelves { get; } = new();

    /// <summary>Taste artist names (most recent, LRU deduped) for live/concert picks.</summary>
    private readonly List<string> _tasteArtists = new();

    /// <summary>The same artists with their channels, where known.</summary>
    private readonly List<Artist> _tasteArtistRefs = new();

    /// <summary>YouTube's own video shelves for this account (none when signed out).</summary>
    private List<SongItem> _videosForYou = new();
    private List<SongItem> _liveForYou = new();

    /// <summary>What the video rails show: their pages open with it.</summary>
    private List<SongItem> _videoRail = new();
    private List<SongItem> _liveRail = new();

    /// <summary>
    /// Set by the shell so tapping a watched CLIP tile replays it as a clip
    /// (same callback the Library's videos tabs use). Song tiles keep the
    /// plain queue path below.
    /// </summary>
    public Action<IReadOnlyList<SongItem>, SongItem?>? PlayVideoRequest { get; set; }

    /// <summary>
    /// True when the row IS a watched clip (pinned to its own video id),
    /// not a song that merely has a known clip. Such rows must replay
    /// through the video path, never a song queue.
    /// </summary>
    private static bool IsClipRow(SongItem song)
    {
        if (string.IsNullOrWhiteSpace(song.VideoVersionId)) return false;
        if (string.Equals(song.VideoVersionId, song.Id, StringComparison.Ordinal)) return true;
        return string.Equals(song.VideoType, "MUSIC_VIDEO_TYPE_OMV", StringComparison.OrdinalIgnoreCase)
            || string.Equals(song.VideoType, "MUSIC_VIDEO_TYPE_UGC", StringComparison.OrdinalIgnoreCase);
    }

    private readonly IAppSettings? _settings;

    public HomeViewModel(IQueueService queue, IMusicDatabase database, IMusicProvider musicProvider,
        IAccountService account, IAppSettings? settings = null)
    {
        _queue = queue;
        _database = database;
        _music = musicProvider;
        _account = account;
        _settings = settings;
    }

    /// <summary>
    /// The listener has just said who they like (the welcome screen): a new feed from that,
    /// now, in place of a load that started before they chose.
    /// </summary>
    public void TasteChanged()
    {
        _loadCts?.Cancel();
        IsRefreshing = false;
        _ = RefreshCoreAsync(force: true);
    }

    /// <summary>Set by the shell: reports a friendly, user-facing error.</summary>
    public Action<string>? ReportError { get; set; }

    private bool _reportedError;

    /// <summary>Reports at most one error per refresh (Home has many parallel calls).</summary>
    private void ReportOnce(string message)
    {
        if (_reportedError) return;
        _reportedError = true;
        ReportError?.Invoke(message);
    }

    /// <summary>Current account scope (null = device-local): taste seeds follow the sign-in.</summary>
    private string? Scope => _account.AccountScopeKey;

    public override void Initialize()
    {
        Greeting = GreetingForHour(DateTime.Now.Hour);
        // Every visit refreshes the local part (stats, Jump back in, top
        // artists — instant). The network shelves reload when they are a few
        // minutes old, after a sign-in change, or from the Refresh button.
        _ = RefreshCoreAsync(force: false);
    }

    private CancellationTokenSource? _loadCts;

    /// <summary>When the network shelves were last rebuilt, and for which account scope.</summary>
    private DateTime _lastNetworkLoad = DateTime.MinValue;
    private string? _networkScope;
    private bool _cacheApplied;
    private static readonly TimeSpan NetworkFeedTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OpenPageFeedTtl = TimeSpan.FromMinutes(15);

    [RelayCommand]
    private Task RefreshAsync() => RefreshCoreAsync(force: true);

    /// <summary>The page has stayed open a while: a new feed, unless somebody is reading further down.</summary>
    public void RefreshIfStale()
    {
        if (_scrolledDown || _selectedChip != null) return;
        if (DateTime.UtcNow - _lastNetworkLoad <= OpenPageFeedTtl) return;
        _ = RefreshCoreAsync(force: false);
    }

    private async Task RefreshCoreAsync(bool force)
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        _reportedError = false;
        // A new load supersedes the previous one (fast tab switching used to
        // pile request storms on top of each other).
        _loadCts?.Cancel();
        try { _loadCts?.Dispose(); } catch { }
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        try
        {
            await LoadAllAsync(force, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // superseded — the newer load owns the UI now
        }
        finally
        {
            if (_loadCts == cts) IsRefreshing = false;
        }
    }

    /// <summary>Personal history first (fast, local), then the network shelves.</summary>
    private async Task LoadAllAsync(bool force, CancellationToken ct)
    {
        // Per-scope hearts first (idempotent) so seeds below read this scope.
        try { await _database.ApplyLikeScopeAsync(Scope, ct); } catch { }

        // First load of the session: show the last feed from disk at once.
        if (!_cacheApplied)
        {
            _cacheApplied = true;
            await ApplyCachedFeedAsync();
        }

        await LoadPersonalAsync(ct);

        var scopeChanged = !string.Equals(_networkScope, Scope, StringComparison.Ordinal);
        var stale = DateTime.UtcNow - _lastNetworkLoad > NetworkFeedTtl;
        if (force || stale || scopeChanged || !HasHomeSections)
            await LoadRecommendationsAsync(ct);
    }

    private async Task ApplyCachedFeedAsync()
    {
        var snapshot = await HomeFeedCacheStore.TryLoadAsync(Scope);
        if (snapshot == null) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            AttachFeeds(snapshot.Shelves);
            ApplyChips(snapshot.Chips);
            ApplyFeed(new Feed(snapshot.Shelves.ToList(), snapshot.Spotlight, null, Array.Empty<MoodTile>()));
        });
        System.Diagnostics.Trace.WriteLine($"[Home] cached feed shown: {snapshot.Shelves.Count} shelves");
    }

    // ---- The feed on screen ----

    private sealed record Feed(List<ShelfVm> Shelves, IReadOnlyList<SpotlightEntry> Spotlight, string? TailToken, IReadOnlyList<MoodTile> TailMoods);

    private const int MostShelvesFromScrolling = 60;

    private Feed? _mainFeed;
    private Feed? _waitingFeed;
    private HomeChipVm? _selectedChip;
    private CancellationTokenSource? _chipCts;
    private string? _tailToken;
    private readonly Queue<MoodTile> _tailMoods = new();
    private readonly HashSet<string> _shownShelves = new(StringComparer.OrdinalIgnoreCase);
    private int _shelvesFromScrolling;
    private bool _scrolledDown;

    private static string KeyOf(ShelfVm shelf) => $"{shelf.Eyebrow}|{shelf.Title}";

    private void ApplyFeed(Feed feed)
    {
        // Somebody reading further down keeps their page: the new one swaps in when
        // they are back at the top.
        if (_scrolledDown && Shelves.Count > 0)
        {
            _waitingFeed = feed;
            return;
        }

        _waitingFeed = null;
        _mainFeed = feed;
        if (_selectedChip != null) return;
        ShowMainFeed(feed);
    }

    private void ShowMainFeed(Feed feed)
    {
        Spotlight = feed.Spotlight;
        HasSpotlight = feed.Spotlight.Count > 0;
        ShowShelves(feed.Shelves);

        _tailToken = feed.TailToken;
        _tailMoods.Clear();
        foreach (var tile in feed.TailMoods) _tailMoods.Enqueue(tile);
        _shelvesFromScrolling = 0;
    }

    private void ShowShelves(IEnumerable<ShelfVm> shelves)
    {
        Shelves.Clear();
        _shownShelves.Clear();
        foreach (var shelf in shelves)
        {
            _shownShelves.Add(KeyOf(shelf));
            Shelves.Add(shelf);
        }
        HasHomeSections = Shelves.Count > 0;
    }

    private void ApplyChips(IReadOnlyList<HomeChip> chips)
    {
        if (chips.Count == 0) return;
        var selected = _selectedChip?.Text;
        Chips.Clear();
        _selectedChip = null;
        foreach (var chip in chips)
        {
            var vm = new HomeChipVm(chip);
            if (string.Equals(chip.Text, selected, StringComparison.Ordinal))
            {
                vm.IsSelected = true;
                _selectedChip = vm;
            }
            Chips.Add(vm);
        }
        HasChips = Chips.Count > 0;
    }

    /// <summary>The page's scroll position, from the view.</summary>
    public void ReportScroll(bool scrolledDown)
    {
        _scrolledDown = scrolledDown;
        if (!scrolledDown && _waitingFeed is { } feed) ApplyFeed(feed);
    }

    /// <summary>Narrows the feed to a mood; the same chip again brings the whole feed back.</summary>
    [RelayCommand]
    private async Task SelectChipAsync(HomeChipVm? chip)
    {
        if (chip == null) return;

        _chipCts?.Cancel();
        var cts = new CancellationTokenSource();
        _chipCts = cts;

        if (_selectedChip != null) _selectedChip.IsSelected = false;
        if (ReferenceEquals(_selectedChip, chip))
        {
            _selectedChip = null;
            IsLoadingSections = false;
            if (_mainFeed != null) ShowMainFeed(_mainFeed);
            return;
        }

        chip.IsSelected = true;
        _selectedChip = chip;
        HasSpotlight = false;
        Shelves.Clear();
        HasHomeSections = false;
        IsLoadingSections = true;
        try
        {
            var page = await _music.GetHomePageAsync(chip.Chip.Params, cts.Token);
            if (cts.IsCancellationRequested) return;
            ShowShelves(page.Shelves.Where(s => !IsDroppedShelf(s.Title)).Select(s => ShelfVm.From(s)));
            _tailToken = page.ContinuationToken;
            _tailMoods.Clear();
            _shelvesFromScrolling = 0;
        }
        catch (OperationCanceledException)
        {
            // Another chip was chosen.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Home] mood {chip.Text} failed: {ex.Message}");
            ReportOnce("Couldn't load this mood. Check your connection.");
        }
        finally
        {
            if (_chipCts == cts) IsLoadingSections = false;
        }
    }

    /// <summary>The page was scrolled near its end: the next shelves.</summary>
    public async Task LoadMoreAsync()
    {
        if (IsLoadingMore || IsLoadingSections) return;
        if (_shelvesFromScrolling >= MostShelvesFromScrolling) return;
        if (_tailToken == null && _tailMoods.Count == 0) return;

        var chip = _selectedChip;
        IsLoadingMore = true;
        try
        {
            var shelves = new List<ShelfVm>();
            if (_tailToken != null)
            {
                var page = await _music.GetHomeContinuationAsync(_tailToken);
                _tailToken = page.ContinuationToken;
                shelves.AddRange(page.Shelves.Where(s => !IsDroppedShelf(s.Title)).Select(s => ShelfVm.From(s)));
            }
            else if (chip == null && _tailMoods.TryDequeue(out var tile))
            {
                // A mood opens with its two best shelves; the tile has the rest.
                var page = await _music.GetShelfPageAsync(tile.BrowseId, tile.Params);
                shelves.AddRange(page.Shelves.Take(2).Select(s => ShelfVm.From(s, eyebrow: tile.Name.ToUpperInvariant())));
            }

            if (!ReferenceEquals(chip, _selectedChip)) return;
            foreach (var shelf in shelves)
            {
                if (!_shownShelves.Add(KeyOf(shelf))) continue;
                Shelves.Add(shelf);
                _shelvesFromScrolling++;
            }
            HasHomeSections = Shelves.Count > 0;
        }
        catch (Exception ex)
        {
            // The next scroll asks again.
            System.Diagnostics.Trace.WriteLine($"[Home] more shelves failed: {ex.Message}");
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    // ---- The personal part ----

    /// <summary>Recently played + top artists from the local play history.</summary>
    private async Task LoadPersonalAsync(CancellationToken ct)
    {
        try
        {
            var stats = await _database.GetPlayStatsAsync(null, Scope, ct);
            var recent = await _database.GetRecentlyPlayedAsync(14, Scope, ct);
            var listening = FormatDuration(stats.TotalListeningTime);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ListeningTimeText = listening;

                RecentlyPlayed.Clear();
                foreach (var song in recent) RecentlyPlayed.Add(song.ToSongItem());
                HasRecentlyPlayed = RecentlyPlayed.Count > 0;

                QuickPicks.Clear();
                foreach (var song in recent.Take(6)) QuickPicks.Add(song.ToSongItem());
                HasQuickPicks = QuickPicks.Count > 0;

                TotalPlaysText = stats.TotalPlays.ToString();

                TopArtists.Clear();
                foreach (var artist in stats.TopArtists.Take(14))
                    TopArtists.Add(new HomeArtistItem { Name = artist.Name, PlayCount = artist.PlayCount });
                HasTopArtists = TopArtists.Count > 0;

                Subtitle = stats.TotalPlays > 0
                    ? $"{stats.TotalPlays} plays · {listening} listened"
                    : "Your music world, ready when you are.";
            });

            _ = EnrichArtistThumbnailsAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer Home load
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Home] personal load failed: {ex.Message}");
            ReportOnce("Couldn't load your Home feed. Check your connection.");
        }
    }

    /// <summary>
    /// Resolves a cover + id for EVERY top artist (search the catalogue, 4 at a
    /// time), then drops rows that resolved to an artist already shown above.
    /// Before: only the first 8 got a picture (the rest stayed blank circles)
    /// and a name without an exact match took the first search hit, so e.g.
    /// "Gorillaz, MF DOOM" became a second Gorillaz.
    /// </summary>
    private async Task EnrichArtistThumbnailsAsync(CancellationToken ct)
    {
        var snapshot = await Dispatcher.UIThread.InvokeAsync(() => TopArtists.ToList());
        // The tiles are rebuilt on every Home visit; the searches need not be.
        using var gate = new SemaphoreSlim(4);

        var tasks = snapshot.Select(async artist =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var match = await MatchArtistAsync(artist.Name, ct);
                if (match == null) return;

                ct.ThrowIfCancellationRequested();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    artist.Id = match.Id;
                    if (!string.IsNullOrWhiteSpace(match.ThumbnailUrl)) artist.ThumbnailUrl = match.ThumbnailUrl;
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Home] artist art failed for {artist.Name}: {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        if (ct.IsCancellationRequested) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var artist in TopArtists.ToList())
            {
                if (string.IsNullOrEmpty(artist.Id) || seen.Add(artist.Id)) continue;
                TopArtists.Remove(artist);
            }
            HasTopArtists = TopArtists.Count > 0;
        });
    }

    /// <summary>Artists resolved so far, by the name in the play history.</summary>
    private readonly ConcurrentDictionary<string, ArtistItem> _artistMatches = new(StringComparer.OrdinalIgnoreCase);

    private async Task<ArtistItem?> MatchArtistAsync(string name, CancellationToken ct)
    {
        if (_artistMatches.TryGetValue(name, out var known)) return known;

        var result = await _music.SearchAsync(name, SearchFilter.Artists, ct);
        var wanted = NormalizeArtistName(name);
        var match = result.Artists.FirstOrDefault(a => NormalizeArtistName(a.Name) == wanted)
            ?? result.Artists.FirstOrDefault(a =>
            {
                var found = NormalizeArtistName(a.Name);
                return found.Length >= 3 && (found.Contains(wanted) || wanted.Contains(found));
            })
            ?? result.Artists.Take(3).FirstOrDefault(a => ArtistNames.SoundTheSame(a.Name, name));
        if (match != null) _artistMatches[name] = match;
        return match;
    }

    private static string NormalizeArtistName(string? name)
        => new string((name ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    // ---- The network part ----

    /// <summary>A part that fails just goes missing: the rest of the page still arrives.</summary>
    private static async Task<T?> SoftlyAsync<T>(string what, Func<Task<T>> read) where T : class
    {
        try
        {
            return await read();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Home] {what} failed: {ex.Message}");
            return null;
        }
    }

    private async Task LoadRecommendationsAsync(CancellationToken ct)
    {
        await Dispatcher.UIThread.InvokeAsync(() => IsLoadingSections = _selectedChip == null);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // A sign-in during this load must not label the old account's feed as the new one's.
        var scope = Scope;
        // Different seeds and moods every time: this is what makes Home change.
        var random = new Random();

        try
        {
            var taste = await LoadTasteSeedAsync(ct);
            var country = ListenerCountry.Current;

            var homeTask = SoftlyAsync("feed", () => _music.GetHomePageAsync(null, ct));
            var exploreTask = SoftlyAsync("explore", () => _music.GetExplorePageAsync(ct));
            var chartsTask = SoftlyAsync("charts", () => _music.GetChartsAsync(country.Code, ct));
            var moodsTask = SoftlyAsync("moods", () => _music.GetMoodsAndGenresAsync(ct));
            var artistsTask = ReadTasteArtistsAsync(random, ct);
            var becauseTask = BuildBecauseYouListenedAsync(taste, random, ct);
            var todayTask = SoftlyAsync("today", () => BuildForTodayAsync(ct));

            var home = await homeTask;

            // Nothing on screen yet (first run, no cache): show the feed as soon as it
            // arrives. Otherwise keep the current page until everything is ready.
            if (!HasHomeSections && _selectedChip == null && home is { Shelves.Count: > 0 })
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ApplyChips(home.Chips);
                    ShowShelves(home.Shelves.Where(s => !IsDroppedShelf(s.Title)).Select(s => ShelfVm.From(s)));
                });
            }

            var (youTube, tailToken) = await ReadYouTubeShelvesAsync(home, ct);
            var artists = await artistsTask;
            var because = await becauseTask;
            var explore = await exploreTask;
            var charts = await chartsTask;
            var moods = await moodsTask;
            var today = await todayTask;
            var videos = await BuildVideosAsync(taste, artists, ct);
            var live = await BuildLiveAsync(taste, ct);
            ct.ThrowIfCancellationRequested();

            var ours = new List<ShelfVm>();
            void AddOurs(ShelfVm? shelf)
            {
                if (shelf != null) ours.Add(shelf);
            }

            var newReleases = NewFromYourArtists(artists, random);
            var similar = ArtistsYouMightLike(artists, random);
            var chartArtists = charts?.FirstOrDefault(s => s.Title.Contains("artist", StringComparison.OrdinalIgnoreCase));
            var chartVideos = charts?.FirstOrDefault(s => s.Title.Contains("video", StringComparison.OrdinalIgnoreCase));

            AddOurs(because.Count > 0 ? because[0].Songs : null);
            AddOurs(videos.Count > 0
                ? ShelfVm.From(new Shelf { Title = "Music videos", Items = videos }, feedKey: "videos", openFeed: OpenVideosFeed)
                : null);
            AddOurs(explore?.Trending is { Items.Count: > 0 } trending ? ShelfVm.From(trending, "Trending now") : null);
            AddOurs(newReleases);
            AddOurs(similar);
            AddOurs(explore?.NewReleases is { Items.Count: > 0 } albums ? ShelfVm.From(albums, "New releases") : null);
            AddOurs(chartArtists is { Items.Count: > 0 }
                ? ShelfVm.From(chartArtists, country.Name != null ? $"Top artists in {country.Name}" : "Top artists")
                : null);
            AddOurs(because.Count > 1 ? because[1].Songs : null);
            AddOurs(explore?.NewVideos is { Items.Count: > 0 } newVideos
                ? ShelfVm.From(newVideos, "New music videos", feedKey: "new", openFeed: OpenNewVideosFeed)
                : null);
            AddOurs(today);
            AddOurs(because.Count > 0 ? because[0].Playlists : null);
            AddOurs(live.Count > 0
                ? ShelfVm.From(new Shelf { Title = "Concerts & live", Items = live }, feedKey: "live", openFeed: OpenLiveFeed)
                : null);
            AddOurs(chartVideos is { Items.Count: > 0 } ? ShelfVm.From(chartVideos, "Video charts") : null);

            var allTiles = (moods ?? Array.Empty<MoodCategory>()).SelectMany(c => c.Tiles).ToList();
            if (allTiles.Count > 0)
                AddOurs(new TileRailVm
                {
                    Title = "Moods and genres",
                    Tiles = allTiles.OrderBy(_ => random.Next()).Select(t => new MoodTileVm(t)).ToList()
                });

            // The two streams take turns, so no kind of shelf comes in a long run.
            var merged = new List<ShelfVm>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < Math.Max(youTube.Count, ours.Count); i++)
            {
                if (i < youTube.Count && seen.Add(KeyOf(youTube[i]))) merged.Add(youTube[i]);
                if (i < ours.Count && seen.Add(KeyOf(ours[i]))) merged.Add(ours[i]);
            }

            if (merged.Count == 0)
            {
                // Offline / everything failed: keep what is on screen (cached
                // feed) and retry on the next visit.
                return;
            }

            var spotlight = BuildSpotlight(newReleases, explore, chartArtists, country.Name, random);
            var feed = new Feed(merged, spotlight, tailToken, allTiles.OrderBy(_ => random.Next()).ToList());
            var chips = home?.Chips ?? Array.Empty<HomeChip>();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyChips(chips);
                ApplyFeed(feed);
            });
            System.Diagnostics.Trace.WriteLine($"[Home] network feed applied after {clock.ElapsedMilliseconds} ms ({merged.Count} shelves)");
            _lastNetworkLoad = DateTime.UtcNow;
            _networkScope = scope;
            _ = HomeFeedCacheStore.SaveAsync(new HomeFeedCacheStore.Snapshot(scope, merged, spotlight, chips));
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer Home load
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Home] recommendations failed: {ex.Message}");
            ReportOnce("Couldn't load recommendations. Check your connection.");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsLoadingSections = false);
        }
    }

    /// <summary>
    /// What YouTube's own feed has on screen first: a few of its portions, so the local
    /// shelves are there at once. The rest arrives while scrolling.
    /// </summary>
    private async Task<(List<ShelfVm> Shelves, string? TailToken)> ReadYouTubeShelvesAsync(HomePage? home, CancellationToken ct)
    {
        const int PortionsAtOnce = 3;

        _videosForYou = new List<SongItem>();
        _liveForYou = new List<SongItem>();
        var shelves = new List<ShelfVm>();
        if (home == null) return (shelves, null);

        void Take(IEnumerable<Shelf> portion)
        {
            foreach (var shelf in portion)
            {
                // YouTube's video shelves go on the video rails, as videos.
                var clips = shelf.Items.Where(i => i.Song != null).Select(i => i.Song!).ToList();
                if (shelf.Title.Contains("music videos for you", StringComparison.OrdinalIgnoreCase)) _videosForYou = clips;
                else if (shelf.Title.Contains("live & concerts", StringComparison.OrdinalIgnoreCase)) _liveForYou = clips;
                if (!IsDroppedShelf(shelf.Title)) shelves.Add(ShelfVm.From(shelf));
            }
        }

        Take(home.Shelves);
        var token = home.ContinuationToken;
        for (var i = 0; i < PortionsAtOnce && !string.IsNullOrEmpty(token); i++)
        {
            var next = await SoftlyAsync("feed portion", () => _music.GetHomeContinuationAsync(token!, ct));
            if (next == null) break;
            Take(next.Shelves);
            token = next.ContinuationToken;
        }
        return (shelves, token);
    }

    /// <summary>
    /// "Listen again" repeats "Jump back in", the video shelves go on the video rails, and
    /// "New releases" comes with the Explore page in full.
    /// </summary>
    private static bool IsDroppedShelf(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var t = title.ToLowerInvariant();
        return t.Contains("music videos for you") || t.Contains("live & concerts")
            || t.Contains("listen again") || t.Contains("new releases");
    }

    /// <summary>
    /// The user's taste: recently played first, then likes, then the tracks of
    /// their local playlists. Drives everything personal on the page.
    /// </summary>
    private async Task<List<SongItem>> LoadTasteSeedAsync(CancellationToken ct)
    {
        try
        {
            var recent = await _database.GetRecentlyPlayedAsync(30, Scope, ct);
            var seeds = recent.Select(e => e.ToSongItem()).ToList();

            if (seeds.Count < 5)
            {
                var liked = await _database.GetLikedSongsAsync(ct);
                seeds.AddRange(liked.Take(10).Select(e => e.ToSongItem())
                    .Where(s => seeds.All(x => !string.Equals(x.Id, s.Id, StringComparison.Ordinal))));
            }

            if (seeds.Count == 0)
            {
                foreach (var pl in (await _database.GetLibraryPlaylistsAsync(ct))
                             .Where(p => Domain.Models.PlaylistEntity.InScope(p, Scope)).Take(2))
                {
                    var tracks = await _database.GetPlaylistSongsAsync(pl.Id, ct);
                    seeds.AddRange(tracks.Select(e => e.ToSongItem()));
                    if (seeds.Count >= 5) break;
                }
            }

            _tasteArtistRefs.Clear();
            // The artists they really play first, then the ones they chose when they
            // arrived: the choice starts Home and gives way as the listening grows.
            var chosen = (_settings?.FavouriteArtists ?? Array.Empty<FavouriteArtist>())
                .Where(f => !string.IsNullOrWhiteSpace(f.Name))
                .Select(f => new Artist { Name = f.Name, Id = string.IsNullOrWhiteSpace(f.Id) ? null : f.Id });
            foreach (var artist in seeds.Select(s => s.Artists.FirstOrDefault()).Concat(chosen))
            {
                if (artist == null || string.IsNullOrWhiteSpace(artist.Name)) continue;
                if (_tasteArtistRefs.All(a => !string.Equals(a.Name, artist.Name, StringComparison.OrdinalIgnoreCase)))
                    _tasteArtistRefs.Add(artist);
            }

            _tasteArtists.Clear();
            _tasteArtists.AddRange(_tasteArtistRefs.Select(a => a.Name));

            return seeds;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Home] taste seeds failed: {ex.Message}");
            return new List<SongItem>();
        }
    }

    /// <summary>The artist pages behind the personal shelves. Six of the user's artists, different ones each time.</summary>
    private async Task<List<ArtistPage>> ReadTasteArtistsAsync(Random random, CancellationToken ct)
    {
        var chosen = _tasteArtistRefs.OrderBy(_ => random.Next()).Take(6).ToList();
        var pages = await Task.WhenAll(chosen.Select(artist => SoftlyAsync($"artist {artist.Name}", async () =>
        {
            var id = !string.IsNullOrWhiteSpace(artist.Id)
                ? artist.Id
                : (await MatchArtistAsync(artist.Name, ct))?.Id;
            return string.IsNullOrWhiteSpace(id) ? null! : await _music.GetArtistPreviewAsync(id, ct);
        })));
        return pages.Where(p => p != null).Select(p => p!).ToList();
    }

    /// <summary>
    /// Their newest singles and albums from the last two years. The artists take turns, so one
    /// busy artist does not fill the row.
    /// </summary>
    private static ShelfVm? NewFromYourArtists(IReadOnlyList<ArtistPage> pages, Random random)
    {
        const int MostReleasesPerArtist = 4;
        var thisYear = DateTime.Now.Year;
        var releases = pages
            .SelectMany(p => p.Singles.Take(5).Concat(p.Albums.Take(3)).Select(a => (Album: a, Artist: p.Artist.Name)))
            .Where(r => int.TryParse(r.Album.Year, out var year) && year >= thisYear - 1)
            .DistinctBy(r => r.Album.Id)
            .GroupBy(r => r.Artist)
            .SelectMany(artist => artist
                .OrderByDescending(r => r.Album.Year)
                .ThenBy(_ => random.Next())
                .Take(MostReleasesPerArtist)
                .Select((r, turn) => (r.Album, r.Artist, Turn: turn)))
            .OrderBy(r => r.Turn)
            .ThenByDescending(r => r.Album.Year)
            .ThenBy(_ => random.Next())
            .Take(ShelfVm.RailLimit)
            .Select(r => new ShelfItem
            {
                Kind = ShelfItemKind.Album,
                Id = r.Album.Id,
                Title = r.Album.Title,
                Subtitle = string.IsNullOrWhiteSpace(r.Album.Subtitle) ? r.Artist : $"{r.Artist} • {r.Album.Subtitle}",
                ThumbnailUrl = r.Album.ThumbnailUrl
            })
            .ToList();
        return releases.Count >= 3 ? ShelfVm.From(new Shelf { Title = "New from your artists", Items = releases }) : null;
    }

    /// <summary>Who the fans of their artists also listen to: somebody new to find.</summary>
    private ShelfVm? ArtistsYouMightLike(IReadOnlyList<ArtistPage> pages, Random random)
    {
        var known = new HashSet<string>(_tasteArtists, StringComparer.OrdinalIgnoreCase);
        var artists = pages
            .SelectMany(p => p.RelatedArtists)
            .Where(a => !known.Contains(a.Name))
            .DistinctBy(a => a.Id)
            .OrderBy(_ => random.Next())
            .Take(ShelfVm.RailLimit)
            .Select(ShelfItems.Artist)
            .ToList();
        return artists.Count >= 3 ? ShelfVm.From(new Shelf { Title = "Artists you might like", Items = artists }) : null;
    }

    private sealed record BecauseShelves(ShelfVm Songs, ShelfVm? Playlists);

    /// <summary>"Because you listened to …": YouTube's own similar songs for two of the user's tracks.</summary>
    private async Task<List<BecauseShelves>> BuildBecauseYouListenedAsync(IReadOnlyList<SongItem> taste, Random random, CancellationToken ct)
    {
        var seeds = taste.Where(s => !s.IsLocal).DistinctBy(s => s.Id).OrderBy(_ => random.Next()).Take(2).ToList();
        var answers = await Task.WhenAll(seeds.Select(seed => SoftlyAsync($"related {seed.Id}",
            () => _music.GetSongRelatedAsync(seed.Id, ct))));

        var built = new List<BecauseShelves>();
        for (var i = 0; i < seeds.Count; i++)
        {
            var related = answers[i];
            var songs = related?.Shelves.FirstOrDefault(s => s.Layout == ShelfLayout.Rows && s.Items.Count > 0);
            if (songs == null) continue;
            var playlists = related!.Shelves.FirstOrDefault(s => s.Title.Contains("playlist", StringComparison.OrdinalIgnoreCase));
            built.Add(new BecauseShelves(
                ShelfVm.From(songs, seeds[i].Title, "BECAUSE YOU LISTENED TO"),
                playlists != null ? ShelfVm.From(playlists, "Playlists you might like") : null));
        }
        return built;
    }

    /// <summary>
    /// The "Music videos" rail: the official clips of the user's OWN tracks first
    /// (recently played / liked songs resolved to their official music video), then
    /// YouTube's picks for the account, then clips from the channels of their artists.
    /// Plain artist-name searches returned random catalogue clips, so nothing here
    /// comes from one.
    /// </summary>
    private async Task<List<ShelfItem>> BuildVideosAsync(IReadOnlyList<SongItem> taste, IReadOnlyList<ArtistPage> artists, CancellationToken ct)
    {
        var videos = new List<ShelfItem>();
        var seenVideos = new HashSet<string>(StringComparer.Ordinal);

        // 1) The user's own tracks first: already-resolved clips cost nothing,
        //    the rest are resolved concurrently (bounded so Home stays snappy).
        var seeds = taste.Take(24).ToList();
        foreach (var seed in seeds)
        {
            if (!string.IsNullOrWhiteSpace(seed.VideoVersionId)
                && seenVideos.Add(seed.VideoVersionId!))
            {
                videos.Add(ClipItem(seed, seed.VideoVersionId!));
            }
        }

        // Negative cache first: seeds with a fresh "no clip" record skip
        // discovery entirely (fast indexed reads, no network).
        var unresolved = new List<SongItem>();
        foreach (var s in seeds.Where(s => string.IsNullOrWhiteSpace(s.VideoVersionId) && !s.IsLocal).Take(8))
        {
            bool skip = false;
            try { skip = await _database.HasFreshClipMissAsync(s.Id, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Home] clip-miss check failed: {ex.Message}");
            }
            if (!skip) unresolved.Add(s);
        }
        if (unresolved.Count > 0)
        {
            var resolved = await Task.WhenAll(unresolved.Select(async s =>
            {
                try { return (Song: s, Versions: await _music.GetTrackVersionsAsync(s, ct)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Home] clip lookup failed for {s.Id}: {ex.Message}");
                    return (Song: s, Versions: (TrackVersions?)null);
                }
            }));
            foreach (var (song, versions) in resolved)
            {
                if (!string.IsNullOrWhiteSpace(versions?.VideoId))
                {
                    // Two songs can share a clip: that is no "no clip" answer.
                    if (seenVideos.Add(versions.VideoId!))
                        videos.Add(ClipItem(song, versions.VideoId!));
                }
                else if (versions != null)
                {
                    // Genuine "no clip" (not a failure — those return null):
                    // stamp the negative cache so the next shelf is instant.
                    try { await _database.SetClipMissAtAsync(song.Id, DateTime.UtcNow, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"[Home] clip-miss stamp failed: {ex.Message}");
                    }
                }
            }
        }

        // 2) What YouTube itself picked for this account.
        foreach (var song in _videosForYou)
            if (videos.Count < ShelfVm.RailLimit && seenVideos.Add(song.Id))
                videos.Add(ShelfItems.Video(song));

        // 3) The channels of the user's artists, a few from each in turn.
        for (var round = 0; round < 10 && videos.Count < ShelfVm.RailLimit; round++)
        {
            foreach (var page in artists)
            {
                if (round >= page.Videos.Count || videos.Count >= ShelfVm.RailLimit) continue;
                var clip = page.Videos[round];
                if (!seenVideos.Add(clip.Id)) continue;
                videos.Add(new ShelfItem
                {
                    Kind = ShelfItemKind.Video,
                    Id = clip.Id,
                    Title = clip.Title,
                    Subtitle = clip.ViewCount > 0 ? $"{clip.ArtistDisplay} • {clip.ViewCountText}" : clip.ArtistDisplay,
                    ThumbnailUrl = clip.ThumbnailUrl,
                    Song = clip
                });
            }
        }

        _videoRail = videos.Where(v => v.Song != null).Select(v => v.Song!).ToList();
        System.Diagnostics.Debug.WriteLine($"[Home] video rail: {videos.Count} clips");
        return videos;
    }

    /// <summary>Concerts by the user's artists. Official clips rarely are, so these are searched for.</summary>
    private async Task<List<ShelfItem>> BuildLiveAsync(IReadOnlyList<SongItem> taste, CancellationToken ct)
    {
        const int Enough = 18;

        var live = new List<ShelfItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var song in _liveForYou)
            if (seen.Add(song.Id)) live.Add(ShelfItems.Video(song));

        var queries = TasteArtistNames(taste).Take(6)
            .SelectMany(a => new[] { $"{a} live concert", $"{a} live performance" })
            .ToList();
        var results = await Task.WhenAll(queries.Select(q => SafeSearchVideosAsync(q, ct)));
        foreach (var songs in results)
        {
            foreach (var song in songs)
            {
                if (live.Count >= Enough) break;
                if (!IsLiveRecording(song) || !seen.Add(song.Id)) continue;
                song.VideoVersionId = song.Id;
                song.SongVersionId = song.Id;
                live.Add(new ShelfItem
                {
                    Kind = ShelfItemKind.Video,
                    Id = song.Id,
                    Title = song.Title,
                    Subtitle = song.ViewCount > 0 ? $"{song.ArtistDisplay} • {song.ViewCountText}" : song.ArtistDisplay,
                    ThumbnailUrl = song.ThumbnailUrl,
                    Song = song
                });
            }
        }

        _liveRail = live.Where(v => v.Song != null).Select(v => v.Song!).ToList();
        return live;
    }

    /// <summary>A rail entry for a track's official clip: the clip's own id and picture.</summary>
    private static ShelfItem ClipItem(SongItem song, string videoId)
    {
        var picture = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
        return new ShelfItem
        {
            Kind = ShelfItemKind.Video,
            Id = videoId,
            Title = song.Title,
            Subtitle = song.ArtistDisplay,
            ThumbnailUrl = picture,
            Song = new SongItem
            {
                Id = videoId,
                Title = song.Title,
                Artists = song.Artists,
                ThumbnailUrl = picture,
                VideoVersionId = videoId,
                SongVersionId = videoId,
                VideoType = "MUSIC_VIDEO_TYPE_OMV"
            }
        };
    }

    /// <summary>The big block at the top: something new from them, then what moves the charts.</summary>
    private static IReadOnlyList<SpotlightEntry> BuildSpotlight(
        ShelfVm? newFromArtists, ExplorePage? explore, Shelf? chartArtists, string? country, Random random)
    {
        var entries = new List<SpotlightEntry>();

        if (newFromArtists is CardShelfVm { Items.Count: > 0 } mine)
            entries.Add(new SpotlightEntry("NEW FROM YOUR ARTISTS", new ShelfCard(mine.Items[0].Item)));

        if (explore?.Trending is { Items.Count: > 0 } trending)
            entries.Add(new SpotlightEntry("TRENDING NOW · #1", new ShelfCard(trending.Items[0])));

        if (explore?.NewReleases is { Items.Count: > 0 } releases)
            entries.Add(new SpotlightEntry("NEW RELEASE", new ShelfCard(releases.Items[random.Next(Math.Min(6, releases.Items.Count))])));

        if (chartArtists is { Items.Count: > 0 })
            entries.Add(new SpotlightEntry(
                country != null ? $"TOP ARTIST IN {country.ToUpperInvariant()}" : "TOP ARTIST",
                new ShelfCard(chartArtists.Items[0])));

        if (explore?.NewVideos is { Items.Count: > 0 } videos)
            entries.Add(new SpotlightEntry("NEW MUSIC VIDEO", new ShelfCard(videos.Items[random.Next(Math.Min(6, videos.Items.Count))])));

        return entries;
    }

    // ---- The pages behind the rails' "Show all" ----

    /// <summary>What the rail shows, then the channels of the user's artists, then what is new.</summary>
    public IVideoFeed OpenVideosFeed()
    {
        var feeds = new List<IVideoFeed> { new SinglePageVideoFeed(_videoRail.ToList()) };
        if (_tasteArtistRefs.Count > 0)
            feeds.Add(new MixedVideoFeed(_tasteArtistRefs
                .Select(a => _music.OpenArtistVideoFeed(a.Id, a.Name)).ToList()));
        feeds.Add(OpenNewVideosFeed());
        return new ConcatVideoFeed(feeds.ToArray());
    }

    public IVideoFeed OpenNewVideosFeed() => new SinglePageVideoFeed(ct => _music.GetNewVideosAsync(ct));

    /// <summary>What the rail shows, then concert searches for the user's artists.</summary>
    public IVideoFeed OpenLiveFeed()
    {
        var searches = _tasteArtists.Take(8).Select(name => (IVideoFeed)new FilteredVideoFeed(
            _music.OpenVideoSearchFeed($"{name} live concert"), IsLiveRecording)).ToList();
        return new ConcatVideoFeed(new SinglePageVideoFeed(_liveRail.ToList()), new MixedVideoFeed(searches));
    }

    /// <summary>Shelves from the launch cache get their pages back.</summary>
    private void AttachFeeds(IEnumerable<ShelfVm> shelves)
    {
        foreach (var shelf in shelves)
        {
            if (shelf is not VideoShelfVm videos) continue;
            var songs = videos.Items.Where(c => c.Item.Song != null).Select(c => c.Item.Song!).ToList();
            switch (shelf.FeedKey)
            {
                case "videos":
                    _videoRail = songs;
                    shelf.OpenFeed = OpenVideosFeed;
                    break;
                case "new":
                    shelf.OpenFeed = OpenNewVideosFeed;
                    break;
                case "live":
                    _liveRail = songs;
                    shelf.OpenFeed = OpenLiveFeed;
                    break;
            }
        }
    }

    private static bool IsLiveRecording(SongItem video) =>
        IsLiveConcertTitle(video.Title) && !IsNonLiveUploadTitle(video.Title) && video.DurationSeconds <= 10800;

    // Whole words: "Alive" is no live recording, and "Discover" no cover.
    private static readonly Regex LiveConcertWords = new(
        @"\b(?:live|concerts?|tours?|unplugged|performances?|festivals?|full show)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex NonLiveUploadWords = new(
        @"\b(?:lyrics?|visuali[sz]ers?|karaoke|covers?|remix(?:es|ed)?|reactions?|reviews?|compilations?|tributes?|fan[- ]?made)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsLiveConcertTitle(string title) => LiveConcertWords.IsMatch(title);

    private static bool IsNonLiveUploadTitle(string title) => NonLiveUploadWords.IsMatch(title);

    private List<string> TasteArtistNames(IReadOnlyList<SongItem> taste)
        => _tasteArtists.Count > 0
            ? _tasteArtists.Take(4).ToList()
            : taste.Select(s => s.Artists.FirstOrDefault()?.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();

    /// <summary>A mood shelf that follows the time of day.</summary>
    private async Task<ShelfVm> BuildForTodayAsync(CancellationToken ct)
    {
        var (label, query) = TodayMood();
        var result = await _music.SearchAsync(query, SearchFilter.FeaturedPlaylists, ct);

        var items = result.Playlists
            .Where(p => !string.IsNullOrWhiteSpace(p.Title) && !string.IsNullOrEmpty(p.Id))
            .Take(ShelfVm.RailLimit)
            .Select(ShelfItems.Playlist)
            .ToList();
        return ShelfVm.From(new Shelf { Title = label, Items = items });
    }

    /// <summary>A Videos search that fails soft (video tiles).</summary>
    private async Task<IReadOnlyList<SongItem>> SafeSearchVideosAsync(string query, CancellationToken ct)
    {
        try
        {
            var result = await _music.SearchAsync(query, SearchFilter.Videos, ct);
            return result.Songs
                .Where(s => !string.IsNullOrWhiteSpace(s.Title) && !string.IsNullOrEmpty(s.Id))
                .ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Home] video search failed: {ex.Message}");
            return Array.Empty<SongItem>();
        }
    }

    private static (string Label, string Query) TodayMood() => DateTime.Now.Hour switch
    {
        < 5 => ("Late night", "late night lofi"),
        < 12 => ("Good morning", "morning acoustic"),
        < 18 => ("Afternoon focus", "focus instrumental"),
        < 22 => ("Evening wind-down", "evening chill"),
        _ => ("Night drive", "night drive synth")
    };

    [RelayCommand]
    private void PlayRecent(SongItem? song)
    {
        if (song == null) return;
        var list = RecentlyPlayed.ToList();
        var index = list.FindIndex(x => string.Equals(x.Id, song.Id, StringComparison.Ordinal));
        var row = index >= 0 ? list[index] : song;
        // A watched clip replays as a clip — a song queue would degrade it
        // to audio-only ("clips play as songs" bug).
        if (IsClipRow(row) && PlayVideoRequest != null)
        {
            PlayVideoRequest(new[] { row }, row);
            return;
        }
        if (index >= 0)
            _queue.SetQueue(list, index);
        else
            _queue.SetQueue(new[] { song }, 0);
    }

    private static string FormatDuration(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:D2}m"
            : t.TotalMinutes >= 1 ? $"{t.Minutes}m"
            : $"{t.Seconds}s";

    private static string GreetingForHour(int hour) => hour switch
    {
        < 5 => "Good night",
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening"
    };
}
