using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using SoundLibert.Domain.Models;
using SoundLibert.Infra.Genres;
using SoundLibert.Infra.SQLite;

namespace SoundLibert.Domain.Tests;

public sealed class LibraryDataTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "soundlibert-data-test-" + Guid.NewGuid().ToString("N"));

    public LibraryDataTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private SqliteMusicDatabase Database() => new(Path.Combine(_directory, "library.db"));

    [Fact]
    public async Task Upsert_KeepsWhatIsKnown_OverEmptyValues()
    {
        var database = Database();
        await database.UpsertSongAsync(new SongEntity
        {
            Id = "a",
            Title = "Song",
            ArtistsJson = "[\"Artist\"]",
            AlbumTitle = "Album",
            DurationSeconds = 200,
            ThumbnailUrl = "https://cover"
        });

        await database.UpsertSongAsync(new SongEntity { Id = "a" });

        var song = await database.GetSongAsync("a");
        Assert.Equal("Song", song?.Title);
        Assert.Equal("[\"Artist\"]", song?.ArtistsJson);
        Assert.Equal("Album", song?.AlbumTitle);
        Assert.Equal(200, song?.DurationSeconds);
        Assert.Equal("https://cover", song?.ThumbnailUrl);
    }

    [Fact]
    public async Task Upsert_TakesNewValues()
    {
        var database = Database();
        await database.UpsertSongAsync(new SongEntity { Id = "a", Title = "Old", ThumbnailUrl = "old" });

        await database.UpsertSongAsync(new SongEntity { Id = "a", Title = "New", ThumbnailUrl = "new" });

        var song = await database.GetSongAsync("a");
        Assert.Equal("New", song?.Title);
        Assert.Equal("new", song?.ThumbnailUrl);
    }

    [Fact]
    public async Task ReconcileLikes_HandlesAHugeLibrary()
    {
        var database = Database();
        var songs = Enumerable.Range(0, 1500)
            .Select(i => new SongEntity { Id = $"s{i}", Title = $"Song {i}", IsLiked = true, IsYouTubeLiked = true })
            .ToList();
        await database.UpsertSongsAsync(songs);

        await database.ReconcileLikesAsync(songs.Take(1200).Select(s => s.Id).ToList());

        var liked = await database.GetYouTubeLikedSongIdsAsync();
        Assert.Equal(1200, liked.Count);
        Assert.Contains("s0", liked);
        Assert.DoesNotContain("s1400", liked);
    }

    [Fact]
    public async Task ReconcilePlaylists_HandlesAHugeLibrary_AndKeepsLocalOnes()
    {
        var database = Database();
        for (var i = 0; i < 1100; i += 100)
            await database.UpsertPlaylistAsync(new PlaylistEntity { Id = $"VL{i}", Title = $"P{i}", BrowseId = $"VL{i}", BookmarkedAt = DateTime.UtcNow });
        var local = await database.CreatePlaylistAsync("Mine");

        // More ids than SQLite takes as parameters of one statement.
        var keep = Enumerable.Range(0, 40_000).Select(i => $"VL{i}").Where(id => id != "VL100").ToList();
        await database.ReconcileAccountPlaylistsAsync(keep);

        var ids = (await database.GetLibraryPlaylistsAsync()).Select(p => p.Id).ToList();
        Assert.Contains("VL0", ids);
        Assert.DoesNotContain("VL100", ids);
        Assert.Contains(local.Id, ids);
    }

    [Fact]
    public async Task SignOut_KeepsWhatTheUserChoseLocally()
    {
        var database = Database();
        const string account = "someone";
        await database.ToggleArtistSubscribeAsync(new ArtistEntity { Id = "UC1", Name = "Artist", ChannelId = "UC1" }, account);
        await database.TogglePlaylistSaveAsync(new PlaylistEntity { Id = "VLsaved", Title = "Saved", BrowseId = "VLsaved" }, account);
        await database.UpsertPlaylistAsync(new PlaylistEntity { Id = "VLmirror", Title = "Mirror", BrowseId = "VLmirror", BookmarkedAt = DateTime.UtcNow });
        var mine = await database.CreatePlaylistAsync("Mine", account);
        Assert.Contains("VLmirror", (await database.GetLibraryPlaylistsAsync()).Select(p => p.Id));

        await database.ClearAccountDataAsync();

        Assert.True(await database.IsArtistSubscribedAsync("UC1", account));
        Assert.True(await database.IsPlaylistSavedAsync("VLsaved"));
        var ids = (await database.GetLibraryPlaylistsAsync()).Select(p => p.Id).ToList();
        Assert.Contains(mine.Id, ids);
        Assert.Contains("VLsaved", ids);
        Assert.DoesNotContain("VLmirror", ids);
    }

    [Fact]
    public async Task ADamagedDatabase_IsSetAside_AndTheAppStarts()
    {
        var path = Path.Combine(_directory, "library.db");
        await File.WriteAllBytesAsync(path, Encoding.ASCII.GetBytes(new string('x', 8192)));
        var database = new SqliteMusicDatabase(path);

        await database.InitializeAsync();
        await database.UpsertSongAsync(new SongEntity { Id = "a", Title = "Song" });

        Assert.Equal("Song", (await database.GetSongAsync("a"))?.Title);
        Assert.NotEmpty(Directory.GetFiles(_directory, "library.db.damaged-*"));
    }

    [Fact]
    public async Task PlaylistMove_KeepsEveryTrackOnce()
    {
        var database = Database();
        var playlist = await database.CreatePlaylistAsync("Mine");
        foreach (var id in new[] { "a", "b", "c", "d" })
            await database.AddSongToPlaylistAsync(playlist.Id, new SongEntity { Id = id, Title = id });

        await database.MovePlaylistSongAsync(playlist.Id, "a", 2);

        var order = (await database.GetPlaylistSongsAsync(playlist.Id)).Select(s => s.Id);
        Assert.Equal(new[] { "b", "c", "a", "d" }, order);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", null, true)]
    [InlineData(null, "", true)]
    [InlineData("someone", null, false)]
    [InlineData("someone", "someone", true)]
    [InlineData("someone", "another", false)]
    [InlineData(null, "someone", false)]
    public void LocalPlaylists_BelongToTheirScope(string? owner, string? scope, bool expected)
    {
        var playlist = new PlaylistEntity { Id = PlaylistEntity.LocalIdPrefix + "1", AccountKey = owner };

        Assert.Equal(expected, PlaylistEntity.InScope(playlist, scope));
    }

    [Fact]
    public void ImportedPlaylists_AreAlwaysInScope()
        => Assert.True(PlaylistEntity.InScope(new PlaylistEntity { Id = "VLPL1" }, "someone"));

    // ---- genres ----

    private sealed class FakeITunes(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(respond(request.RequestUri!.ToString()));
        }
    }

    private static HttpResponseMessage Results(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private ITunesGenreService Genres(FakeITunes handler, SqliteMusicDatabase database)
        => new(database, new HttpClient(handler), TimeSpan.Zero);

    [Fact]
    public async Task AFailedLookup_IsNotRememberedAsNoGenre()
    {
        var database = Database();
        var online = false;
        var handler = new FakeITunes(_ => online
            ? Results("{\"results\":[{\"trackName\":\"Song\",\"artistName\":\"Artist\",\"primaryGenreName\":\"Rock\"}]}")
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var genres = Genres(handler, database);

        Assert.Null(await genres.GetSongGenreAsync("a", "Song", "Artist"));
        Assert.Null(await database.GetSongGenreAsync("a"));

        online = true;
        Assert.Equal("Rock", await genres.GetSongGenreAsync("a", "Song", "Artist"));
    }

    [Fact]
    public async Task AGenuineNoGenreAnswer_IsRemembered()
    {
        var database = Database();
        var handler = new FakeITunes(_ => Results("{\"results\":[]}"));
        var genres = Genres(handler, database);

        Assert.Null(await genres.GetSongGenreAsync("a", "Song", "Artist"));
        var asked = handler.Requests;

        Assert.Null(await genres.GetSongGenreAsync("a", "Song", "Artist"));
        Assert.Equal(asked, handler.Requests);
        Assert.Equal(string.Empty, await database.GetSongGenreAsync("a"));
    }

    [Fact]
    public async Task ARateLimit_StopsAskingForAWhile()
    {
        var database = Database();
        var handler = new FakeITunes(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var genres = Genres(handler, database);

        await genres.GetSongGenreAsync("a", "Song", "Artist");
        await genres.GetSongGenreAsync("b", "Other", "Artist");

        Assert.Equal(1, handler.Requests);
        Assert.Null(await database.GetSongGenreAsync("a"));
    }

    [Fact]
    public async Task ASearchHitForAnotherSong_IsNotItsGenre()
    {
        var database = Database();
        var handler = new FakeITunes(url => url.Contains("entity=song", StringComparison.Ordinal)
            ? Results("{\"results\":[{\"trackName\":\"Something Else\",\"artistName\":\"Other\",\"primaryGenreName\":\"Opera\"}]}")
            : Results("{\"results\":[{\"artistName\":\"Artist\",\"primaryGenreName\":\"Rock\"}]}"));
        var genres = Genres(handler, database);

        Assert.Equal("Rock", await genres.GetSongGenreAsync("a", "Song", "Artist"));
    }

    // ---- track identity ----

    private static SongItem Track(string id, string title = "", string? video = null, string? song = null, string artist = "A") => new()
    {
        Id = id,
        Title = title,
        VideoVersionId = video,
        SongVersionId = song,
        Artists = new[] { new Artist { Name = artist } }
    };

    [Fact]
    public void TrackSet_AgreesWithSameTrack()
    {
        var known = new[]
        {
            Track("id1", "One"),
            Track("id2", "Two", video: "v2"),
            Track("id3", "Three", song: "s3")
        };
        var candidates = new[]
        {
            Track("id1", "Different"),
            Track("x", "Different", video: "v2"),
            Track("y", "Different", song: "s3"),
            Track("z", "  one  "),
            Track("q", "One", artist: "Somebody else"),
            Track("r", "Nothing"),
            Track(string.Empty)
        };

        var set = new SongIdentity.TrackSet(known);

        foreach (var candidate in candidates)
            Assert.Equal(known.Any(k => SongIdentity.SameTrack(candidate, k)), set.Contains(candidate));
    }
}
