using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;
using SoundLibert.Domain.Video;

namespace SoundLibert.UI.ViewModels;

/// <summary>
/// A real YouTube Music artist page: how big they are, the newest release, top songs and
/// every shelf YouTube has for them. Every item keeps its id, so the page cross-links
/// into the rest of the catalogue.
/// </summary>
public partial class ArtistViewModel : ViewModelBase
{
    private readonly IMusicProvider _provider;
    private readonly IQueueService _queue;
    private readonly IMusicDatabase _database;
    private readonly ILikeService _likeService;
    private readonly IAccountService? _account;
    private readonly IDownloadService _downloads;
    private readonly IViewCountService _viewCounts;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string? _thumbnailUrl;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasSongs;
    [ObservableProperty] private bool _hasDiscography;
    [ObservableProperty] private string _emptyMessage = "Nothing here yet.";

    /// <summary>"535M monthly audience · 33.4M subscribers".</summary>
    [ObservableProperty] private string? _audienceText;

    /// <summary>"23.7B views".</summary>
    [ObservableProperty] private string? _viewsText;

    /// <summary>The newest single or album.</summary>
    [ObservableProperty] private ShelfCard? _latestRelease;
    [ObservableProperty] private bool _hasLatestRelease;

    /// <summary>The whole "Top songs" list, when YouTube has one.</summary>
    [ObservableProperty] private string? _topSongsListId;
    [ObservableProperty] private bool _hasTopSongsList;

    public string ArtistId { get; private set; } = string.Empty;

    public ObservableCollection<SongItem> Songs { get; } = new();

    /// <summary>Everything below the top songs, from "Albums" to "Fans might also like".</summary>
    public ObservableCollection<ShelfVm> Shelves { get; } = new();

    /// <summary>For the full discography page.</summary>
    public List<AlbumItem> Albums { get; private set; } = new();
    public List<AlbumItem> Singles { get; private set; } = new();
    public List<AlbumItem> AppearsOn { get; private set; } = new();
    public List<PlaylistItem> Playlists { get; private set; } = new();

    /// <summary>Set by the shell: opens the full-page discography view.</summary>
    public Action? OpenDiscographyRequest { get; set; }

    /// <summary>Set by the shell: starts a radio from a song.</summary>
    public Action<SongItem>? StartRadioRequest { get; set; }

    [RelayCommand]
    private void ShowAllDiscography() => OpenDiscographyRequest?.Invoke();

    /// <summary>
    /// Set by the shell so tapping a clip row replays it as a clip.
    /// Song rows keep the plain queue path below.
    /// </summary>
    public Action<IReadOnlyList<SongItem>, SongItem?>? PlayVideoRequest { get; set; }

    /// <summary>True when the user is signed in (subscribe needs an account).</summary>
    [ObservableProperty] private bool _canSubscribe;
    [ObservableProperty] private bool _isSubscribed;

    /// <summary>Label for the subscribe toggle.</summary>
    public string SubscribeButtonText => IsSubscribed ? "Subscribed" : "Subscribe";

    partial void OnIsSubscribedChanged(bool value) => OnPropertyChanged(nameof(SubscribeButtonText));

    public ArtistViewModel(IMusicProvider provider, IQueueService queue, IMusicDatabase database,
        ILikeService likeService, IDownloadService downloads, IViewCountService viewCounts,
        IAccountService? account = null)
    {
        _provider = provider;
        _queue = queue;
        _database = database;
        _likeService = likeService;
        _downloads = downloads;
        _viewCounts = viewCounts;
        _account = account;
        CanSubscribe = account?.IsSignedIn == true;
    }

    /// <summary>
    /// Toggles the local follow for this artist. Nothing is written to YouTube
    /// (the account is read-only): a follow/unfollow is stored locally, so the
    /// Library Artists section and any later visit read the same state.
    /// </summary>
    [RelayCommand]
    private async Task ToggleSubscribeAsync()
    {
        if (_account?.IsSignedIn != true || string.IsNullOrWhiteSpace(ArtistId)) return;
        try
        {
            IsSubscribed = await _database.ToggleArtistSubscribeAsync(new ArtistEntity
            {
                Id = ArtistId,
                Name = Title,
                ThumbnailUrl = ThumbnailUrl ?? string.Empty,
                ChannelId = ArtistId
            }, _account.AccountScopeKey);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Artist] Subscribe failed: {ex.Message}");
        }
    }

    public async Task LoadAsync(string artistId, string title, string? thumbnail)
    {
        try { await _database.ApplyLikeScopeAsync(_account?.AccountScopeKey); } catch { }
        ArtistId = artistId;
        Title = title;
        ThumbnailUrl = thumbnail;
        // Subscribe state comes from the local follow (imported from YouTube +
        // local follows/vetoes) — the provider exposes no state getter.
        try
        {
            IsSubscribed = await _database.IsArtistSubscribedAsync(artistId, _account?.AccountScopeKey);
        }
        catch
        {
            IsSubscribed = false;
        }
        IsLoading = true;

        try
        {
            var page = await _provider.GetArtistPageAsync(artistId);

            if (!string.IsNullOrWhiteSpace(page.Artist.Name)) Title = page.Artist.Name;
            if (!string.IsNullOrWhiteSpace(page.Artist.ThumbnailUrl)) ThumbnailUrl = page.Artist.ThumbnailUrl;
            Description = page.Description;
            AudienceText = string.Join(" · ", new[] { page.MonthlyAudienceText, page.SubscribersText }
                .Where(t => !string.IsNullOrWhiteSpace(t)));
            ViewsText = page.ViewsText;
            TopSongsListId = page.TopSongsListId;
            HasTopSongsList = !string.IsNullOrWhiteSpace(page.TopSongsListId);

            var liked = await _database.GetLikedSongIdsAsync();
            Songs.Clear();
            foreach (var song in page.Songs)
            {
                song.IsLiked = liked.Contains(song.Id);
                song.IsDownloaded = _downloads.IsDownloaded(song.Id);
                Songs.Add(song);
            }

            Albums = page.Albums.ToList();
            Singles = page.Singles.ToList();
            AppearsOn = page.AppearsOn.ToList();
            Playlists = page.Playlists.ToList();

            HasSongs = Songs.Count > 0;
            HasDiscography = Albums.Count + Singles.Count + AppearsOn.Count + Playlists.Count > 0;
            LatestRelease = NewestOf(Singles, Albums);
            HasLatestRelease = LatestRelease != null;
            BuildShelves(page);

            // Artist-page top songs come without a length ("--:--"): fetch the
            // full rows in one batch call and fill the durations in place.
            _ = FillMissingDurationsAsync(Songs.ToList());

            // Global play counts, for rows YouTube sent without one.
            var viewSongs = Songs.ToList();
            if (viewSongs.Count > 0)
            {
                await _viewCounts.HydrateAsync(viewSongs);
                _ = _viewCounts.ResolveMissingAsync(viewSongs);
            }

            EmptyMessage = "No music found for this artist.";
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Artist] Load failed for {artistId}: {ex.Message}");
            EmptyMessage = "Could not load this artist.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>The newest release: the later year wins, a single when it is the same year.</summary>
    private static ShelfCard? NewestOf(IReadOnlyList<AlbumItem> singles, IReadOnlyList<AlbumItem> albums)
    {
        var single = singles.FirstOrDefault();
        var album = albums.FirstOrDefault();
        var newest = single == null ? album
            : album == null ? single
            : string.CompareOrdinal(album.Year, single.Year) > 0 ? album : single;
        return newest == null ? null : new ShelfCard(ShelfItems.Album(newest));
    }

    private void BuildShelves(ArtistPage page)
    {
        var name = page.Artist.Name;
        Func<IVideoFeed> everyVideo = () => _provider.OpenArtistVideoFeed(ArtistId, Title);

        Shelves.Clear();
        void Add(string title, IEnumerable<ShelfItem> items, Action? showAll = null, Func<IVideoFeed>? feed = null)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            var shelf = ShelfVm.From(new Shelf { Title = title, Items = list }, openFeed: feed);
            shelf.ShowAllAction = showAll;
            Shelves.Add(shelf);
        }

        Add("Albums", Albums.Select(ShelfItems.Album), ShowAllDiscography);
        Add("Singles & EPs", Singles.Select(ShelfItems.Album), ShowAllDiscography);
        Add("Videos", page.Videos.Select(ShelfItems.Video), feed: everyVideo);
        Add("Live performances", page.LivePerformances.Select(ShelfItems.Video), feed: everyVideo);
        Add("Featured on", AppearsOn.Select(ShelfItems.Album), ShowAllDiscography);
        Add(string.IsNullOrWhiteSpace(name) ? "Playlists" : $"Playlists by {name}", Playlists.Select(ShelfItems.Playlist), ShowAllDiscography);
        Add("Fans might also like", page.RelatedArtists.Select(ShelfItems.Artist));
    }

    private async Task FillMissingDurationsAsync(IReadOnlyList<SongItem> songs)
    {
        var missing = songs.Where(s => s.DurationSeconds <= 0 && !s.IsLocal).ToList();
        if (missing.Count == 0) return;
        try
        {
            var full = await _provider.GetQueueAsync(missing.Select(s => s.Id).ToList());
            var byId = full.Where(s => s.DurationSeconds > 0)
                .GroupBy(s => s.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().DurationSeconds, StringComparer.Ordinal);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var song in missing)
                    if (byId.TryGetValue(song.Id, out var seconds)) song.DurationSeconds = seconds;
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Artist] duration lookup failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void PlaySong(SongItem? song)
    {
        if (song == null || Songs.Count == 0) return;
        // Top songs are song-oriented: always play the SONG (a clip is watched
        // via the Song/Video toggle), so a clip-tagged row cannot hijack it.
        var index = Songs.IndexOf(song);
        _queue.SetQueue(Songs.ToList(), index < 0 ? 0 : index);
    }

    [RelayCommand]
    private void PlayAll()
    {
        if (Songs.Count > 0) _queue.SetQueue(Songs.ToList(), 0);
    }

    [RelayCommand]
    private void ShufflePlay()
    {
        if (Songs.Count == 0) return;
        var songs = Songs.ToList();
        var random = new Random();
        for (var i = songs.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (songs[i], songs[j]) = (songs[j], songs[i]);
        }
        _queue.SetQueue(songs, 0);
    }

    /// <summary>A radio from their best-known song.</summary>
    [RelayCommand]
    private void StartRadio()
    {
        if (Songs.Count > 0) StartRadioRequest?.Invoke(Songs[0]);
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
            Trace.WriteLine($"[Artist] ToggleLike failed: {ex.Message}");
        }
    }
}
