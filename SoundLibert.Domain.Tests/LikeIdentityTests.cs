using Microsoft.Data.Sqlite;
using SoundLibert.App.Services;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;
using SoundLibert.Infra.SQLite;

namespace SoundLibert.Domain.Tests;

/// <summary>
/// A like belongs to the song, whichever id a list shows it under: YouTube Music
/// lists one song as the album's audio, the deluxe edition's and the music video
/// (an album page often gives the clip's id). Hearts agree everywhere, and
/// unliking from any version clears them all.
/// </summary>
public sealed class LikeIdentityTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "soundlibert-likes-test-" + Guid.NewGuid().ToString("N"));

    public LikeIdentityTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private static SongItem Song(string id, string title, string artist, string? songVersion = null) => new()
    {
        Id = id,
        Title = title,
        Artists = new[] { new Artist { Name = artist } },
        SongVersionId = songVersion
    };

    private (LikeService Likes, SqliteMusicDatabase Database) Service()
    {
        var database = new SqliteMusicDatabase(Path.Combine(_directory, "library.db"));
        return (new LikeService(database, new NoAccount()), database);
    }

    // ---- The key ----

    [Theory]
    [InlineData("I\u2019m So Sorry", "Imagine Dragons", "I'm So Sorry", "imagine dragons")]
    [InlineData("Believer (Official Music Video)", "Imagine Dragons", "Believer", "Imagine Dragons")]
    [InlineData("Imagine Dragons - Believer (Official Video)", "Imagine Dragons", "Believer", "Imagine Dragons")]
    [InlineData("Shots  [Lyric Video]", "Imagine Dragons", "shots", "Imagine Dragons")]
    [InlineData("Thunder (Audio)", "Imagine Dragons", "Thunder", "Imagine  Dragons")]
    public void OneSongOneKey(string titleA, string artistA, string titleB, string artistB)
    {
        var key = SongIdentity.Key(titleA, artistA);
        Assert.NotEqual(string.Empty, key);
        Assert.Equal(key, SongIdentity.Key(titleB, artistB));
    }

    [Theory]
    [InlineData("Believer", "Imagine Dragons", "Believer (Live)", "Imagine Dragons")]
    [InlineData("Believer", "Imagine Dragons", "Believer", "Someone Else")]
    [InlineData("Believer", "Imagine Dragons", "Thunder", "Imagine Dragons")]
    public void OtherSongsOtherKeys(string titleA, string artistA, string titleB, string artistB) =>
        Assert.NotEqual(SongIdentity.Key(titleA, artistA), SongIdentity.Key(titleB, artistB));

    [Fact]
    public void NoArtistNoKey()
    {
        Assert.Equal(string.Empty, SongIdentity.Key("Believer", null));
        Assert.Equal(string.Empty, SongIdentity.Key(new SongItem { Id = "x", Title = "Believer" }));
        Assert.Equal(string.Empty, SongIdentity.Key(new SongItem
        {
            Id = "local:1", Title = "Believer", Artists = new[] { new Artist { Name = "Imagine Dragons" } },
            IsLocal = true, LocalPath = @"C:\Music\believer.mp3"
        }));
    }

    // ---- The service ----

    [Fact]
    public async Task ALikedSongIsLikedOnItsAlbumUnderTheClipsId()
    {
        var (likes, _) = Service();
        var liked = Song("MzK09EuJnwE", "I\u2019m So Sorry", "Imagine Dragons");
        Assert.True(await likes.SetLikedAsync(liked, true));

        // The album page lists the music video's id; the deluxe edition another audio id.
        Assert.True(likes.IsLiked(Song("78El9Q-_WzU", "I\u2019m So Sorry", "Imagine Dragons")));
        Assert.True(likes.IsLiked(Song("0HgJwrwEEKQ", "I'm So Sorry", "Imagine Dragons")));
        Assert.True(likes.IsLiked(new SongItem { Id = "clip", Title = "?", SongVersionId = "MzK09EuJnwE" }));
        Assert.False(likes.IsLiked(Song("other", "Shots", "Imagine Dragons")));
    }

    [Fact]
    public async Task UnlikingAnyVersionClearsThemAll()
    {
        var (likes, database) = Service();
        await likes.SetLikedAsync(Song("audio", "Gold", "Imagine Dragons"), true);
        await likes.SetLikedAsync(Song("deluxe", "Gold", "Imagine Dragons"), true);
        var changed = new List<string>();
        likes.LikeChanged += (_, e) => changed.Add(e.SongId);

        // Unliked from the album row, whose id was never liked itself.
        var albumRow = Song("clip", "Gold", "Imagine Dragons");
        Assert.True(likes.IsLiked(albumRow));
        Assert.False(await likes.ToggleAsync(albumRow));

        Assert.False(albumRow.IsLiked);
        Assert.False((await database.GetSongAsync("audio"))?.IsLiked);
        Assert.False((await database.GetSongAsync("deluxe"))?.IsLiked);
        Assert.Equivalent(new[] { "audio", "deluxe" }, changed);
        Assert.Empty(await database.GetLikedSongIdsAsync());
    }

    [Fact]
    public async Task AStaleRowFlagDoesNotTurnTheToggleRoundTheWrongWay()
    {
        var (likes, database) = Service();
        await likes.SetLikedAsync(Song("a", "Thunder", "Imagine Dragons"), true);

        // A Home card never learnt its like: its own flag says "not liked".
        var card = Song("a", "Thunder", "Imagine Dragons");
        Assert.False(card.IsLiked);
        Assert.False(await likes.ToggleAsync(card));
        Assert.False((await database.GetSongAsync("a"))?.IsLiked);
    }

    [Fact]
    public async Task TheLikesAreReadBackFromTheLibrary()
    {
        var (first, database) = Service();
        await first.SetLikedAsync(Song("a", "Demons", "Imagine Dragons"), true);

        // A new start: nothing in memory until the library is read.
        var again = new LikeService(database, new NoAccount());
        var reloaded = false;
        again.LikesReloaded += (_, _) => reloaded = true;
        Assert.False(again.IsLiked(Song("b", "Demons", "Imagine Dragons")));
        await again.EnsureLoadedAsync();
        Assert.True(reloaded);
        Assert.True(again.IsLiked(Song("b", "Demons", "Imagine Dragons")));
    }

    private sealed class NoAccount : IAccountService
    {
        public bool IsSignedIn => false;
        public string? AccountName => null;
        public string? AccountEmail => null;
        public string? AccountHandle => null;
        public string? AccountAvatarUrl => null;
        public bool NeedsReauthentication => false;
#pragma warning disable CS0067 // never raised: the test signs nobody in
        public event EventHandler? ReauthenticationRequired;
        public event EventHandler? SignedIn;
        public event EventHandler? SignedOut;
#pragma warning restore CS0067
        public void MarkNeedsReauthentication() { }
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SignInAsync(string cookie, string dataSyncId, string? visitorData, string? name, string? email,
            CancellationToken ct = default) => Task.CompletedTask;
        public Task SignOutAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
