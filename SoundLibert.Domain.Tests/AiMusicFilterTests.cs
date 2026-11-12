using SoundLibert.Domain.Enums;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;
using SoundLibert.Domain.Video;
using SoundLibert.Infra.AiMusic;

namespace SoundLibert.Domain.Tests;

/// <summary>
/// AI-generated acts stay out of recommendations, and only there: the community list by name,
/// real namesakes spared, the listener's word first, search and opened pages untouched.
/// </summary>
public class AiMusicFilterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sl-ai-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSettings _settings = new();
    private readonly AiMusicFilter _filter;

    public AiMusicFilterTests()
    {
        Directory.CreateDirectory(_dir);
        var list = Path.Combine(_dir, "ai-artists.csv");
        // A kept copy, fresh: no download in tests.
        File.WriteAllLines(list, new[]
        {
            "artist,id",
            "Kenke,1111111111111111111111",
            "The Velvet Sundown,2222222222222222222222",
            "\"Comma, Band\",3333333333333333333333",
            "Aurora,4444444444444444444444",
            "HUNTR/X,5555555555555555555555",
            "Nebula,6666666666666666666666"
        });
        _filter = new AiMusicFilter(_settings, list);
        _filter.EnsureReadyAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static SongItem Song(string title, string artist, long views = 0) => new()
    {
        Id = title,
        Title = title,
        Artists = new[] { new Artist { Name = artist } },
        ViewCount = views
    };

    [Theory]
    [InlineData("Kenke", true)]
    [InlineData("  kenke ", true)]
    [InlineData("The  Velvet   Sundown", true)]
    [InlineData("Comma, Band", true)]
    [InlineData("Imagine Dragons", false)]
    [InlineData("Kenke Fan Club", false)]
    [InlineData("", false)]
    public void KnowsListedActsByName(string name, bool ai) => Assert.Equal(ai, _filter.IsAiArtist(name));

    [Theory]
    [InlineData("Aurora")]
    [InlineData("HUNTR/X")]
    public void SparesRealNamesakesOnTheList(string name) => Assert.False(_filter.IsAiArtist(name));

    [Fact]
    public void AOneWordMatchHeardByTensOfMillionsIsHuman()
    {
        Assert.True(_filter.IsAi(Song("Drift", "Nebula", views: 120_000)));
        Assert.False(_filter.IsAi(Song("Drift", "Nebula", views: 340_000_000)));
        // Several words are a name, not a namesake.
        Assert.True(_filter.IsAi(Song("Dust on the Wind", "The Velvet Sundown", views: 90_000_000)));
    }

    [Theory]
    [InlineData("Believer (AI Cover)", true)]
    [InlineData("Song [AI]", true)]
    [InlineData("Song (A.I.)", true)]
    [InlineData("made with Suno", true)]
    [InlineData("Air", false)]
    [InlineData("Fairy Tale", false)]
    [InlineData("Said I Would", false)]
    public void TitlesThatSayItThemselves(string title, bool ai) => Assert.Equal(ai, AiMusicFilter.LooksMadeWithAi(title));

    [Fact]
    public void ShelfEntriesAreJudgedByWhatTheyCarry()
    {
        Assert.True(_filter.IsAi(new ShelfItem { Kind = ShelfItemKind.Album, Title = "Hechizo", Subtitle = "Single • Kenke • 2026" }));
        Assert.True(_filter.IsAi(new ShelfItem { Kind = ShelfItemKind.Artist, Title = "Kenke", Subtitle = "3K subscribers" }));
        Assert.False(_filter.IsAi(new ShelfItem { Kind = ShelfItemKind.Album, Title = "Evolve", Subtitle = "Album • Imagine Dragons • 2017" }));
        // A playlist is many people's music.
        Assert.False(_filter.IsAi(new ShelfItem { Kind = ShelfItemKind.Playlist, Title = "Mix", Subtitle = "Kenke, Someone" }));
        Assert.True(_filter.IsAi(new ShelfItem { Kind = ShelfItemKind.Song, Title = "x", Subtitle = "Kenke • 2M plays", Song = Song("x", "Kenke") }));
    }

    [Fact]
    public void TheListenersWordWins()
    {
        _filter.Mark("Kenke", false);
        Assert.False(_filter.IsAiArtist("Kenke"));
        _filter.Mark("Imagine Dragons", true);
        Assert.True(_filter.IsAiArtist("imagine dragons"));
        Assert.Equal(2, _filter.MarkedCount);
        _filter.ClearMarks();
        Assert.True(_filter.IsAiArtist("Kenke"));
        Assert.False(_filter.IsAiArtist("Imagine Dragons"));
    }

    [Fact]
    public void ParsesTheListWithQuotesAndWithoutAHeader()
    {
        var names = AiMusicFilter.Parse(new[] { "A Band,1", "\"Say \"\"Hi\"\", Bye\",2", "artist,id" });
        Assert.Contains("a band", names);
        Assert.Contains("say \"hi\", bye", names);
    }

    [Fact]
    public async Task RecommendationsLoseAiActsButSearchKeepsThem()
    {
        var catalogue = new FakeCatalogue();
        var provider = new RecommendationFilter(catalogue, _filter);

        var home = await provider.GetHomePageAsync();
        Assert.Single(home.Shelves);
        Assert.Equal(new[] { "Believer" }, home.Shelves[0].Items.Select(i => i.Title));

        var radio = await provider.GetRadioAsync("seed");
        Assert.Equal(new[] { "Believer" }, radio.Select(s => s.Title));

        var found = await provider.SearchAsync("kenke");
        Assert.Equal(2, found.Songs.Count);

        _settings.HideAiMusic = false;
        Assert.Equal(2, (await provider.GetRadioAsync("seed")).Count);
    }

    private sealed class FakeSettings : IAiMusicSettings
    {
        private readonly Dictionary<string, bool> _marks = new();
        public bool HideAiMusic { get; set; } = true;
        public IReadOnlyDictionary<string, bool> AiArtistMarks => _marks;

        public void SetAiArtistMark(string normalizedName, bool? isAi)
        {
            if (isAi is { } value) _marks[normalizedName] = value;
            else _marks.Remove(normalizedName);
        }
    }

    private sealed class FakeCatalogue : IMusicProvider
    {
        private static readonly SongItem[] Songs =
        {
            Song("Believer", "Imagine Dragons"),
            Song("Hechizo", "Kenke")
        };

        public Task<HomePage> GetHomePageAsync(string? chipParams = null, CancellationToken ct = default) =>
            Task.FromResult(new HomePage
            {
                Shelves = new[]
                {
                    new Shelf { Title = "Quick picks", Items = Songs.Select(s => new ShelfItem { Kind = ShelfItemKind.Song, Title = s.Title, Song = s }).ToList() },
                    new Shelf { Title = "Only AI", Items = new[] { new ShelfItem { Kind = ShelfItemKind.Artist, Title = "Kenke" } } }
                }
            });

        public Task<IReadOnlyList<SongItem>> GetRadioAsync(string videoId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SongItem>>(Songs);

        public Task<SearchResult> SearchAsync(string query, SearchFilter filter = SearchFilter.Songs, CancellationToken ct = default) =>
            Task.FromResult(new SearchResult { Songs = Songs });

        public Task<SearchSuggestions> GetSuggestionsAsync(string query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SearchOverview> SearchOverviewAsync(string query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AlbumItem?> GetAlbumAsync(string browseId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CollectionExtras> GetCollectionExtrasAsync(string browseId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SongItem>> GetAlbumSongsAsync(string playlistId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SongItem>> GetAlbumTracksAsync(string albumBrowseId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ArtistItem?> GetArtistAsync(string browseId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ArtistPage> GetArtistPageAsync(string browseId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ArtistPage> GetArtistPreviewAsync(string browseId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PlaylistItem?> GetPlaylistAsync(string playlistId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SongItem>> GetPlaylistSongsAsync(string playlistId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HomePage> GetHomeContinuationAsync(string token, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ExplorePage> GetExplorePageAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MoodCategory>> GetMoodsAndGenresAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ShelfPage> GetShelfPageAsync(string browseId, string? @params = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Shelf>> GetChartsAsync(string country, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SongRelated> GetSongRelatedAsync(string videoId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SongItem>> GetAccountLikedSongsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlaylistItem>> GetAccountPlaylistsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AlbumItem>> GetAccountAlbumsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ArtistItem>> GetAccountArtistsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SongItem>> GetRelatedTracksAsync(string videoId, CancellationToken ct = default) => throw new NotSupportedException();
        public IVideoFeed OpenArtistVideoFeed(string? artistBrowseId, string artistName) => throw new NotSupportedException();
        public IVideoFeed OpenVideoSearchFeed(string query) => throw new NotSupportedException();
        public Task<IReadOnlyList<SongItem>> GetNewVideosAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StreamInfo?> GetStreamInfoAsync(string videoId, string? playlistId = null, int audioQuality = 0, CancellationToken ct = default) => throw new NotSupportedException();
        public Dictionary<string, string> GetStreamHeaders(string streamUrl, string? fallbackClientName = null) => throw new NotSupportedException();
        public Task<StreamInfo?> GetVideoStreamInfoAsync(string videoId, bool highQuality = false, int targetHeight = 0, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long?> GetViewCountAsync(string videoId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TrackVersions?> GetTrackVersionsAsync(SongItem song, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> VerifyVideoPinAsync(SongItem song, string videoId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> VerifyKnownClipAsync(SongItem song, string videoId, string? videoType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SongItem>> GetQueueAsync(IReadOnlyList<string>? videoIds = null, string? playlistId = null, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
