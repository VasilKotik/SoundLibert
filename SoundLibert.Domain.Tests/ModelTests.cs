using SoundLibert.Domain.Models;

namespace SoundLibert.Domain.Tests;

public class ModelTests
{
    private static SongItem Song(string id, string title = "T", string artist = "A",
        string? videoVersion = null, string? songVersion = null) => new()
    {
        Id = id,
        Title = title,
        Artists = new[] { new Artist { Name = artist } },
        VideoVersionId = videoVersion,
        SongVersionId = songVersion
    };

    // ---- SongItem.DurationDisplay ----

    [Theory]
    [InlineData(0, "--:--")]
    [InlineData(59, "0:59")]
    [InlineData(214, "3:34")]
    [InlineData(2820, "47:00")]
    [InlineData(3600, "1:00:00")]
    [InlineData(7500, "2:05:00")]   // used to wrap to "5:00"
    public void DurationDisplay_FormatsMinutesAndHours(int seconds, string expected)
        => Assert.Equal(expected, new SongItem { DurationSeconds = seconds }.DurationDisplay);

    [Fact]
    public void DurationSeconds_RaisesChangeForDisplay()
    {
        var song = new SongItem();
        var changed = new List<string?>();
        song.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        song.DurationSeconds = 120;
        Assert.Contains(nameof(SongItem.DurationDisplay), changed);
    }

    // ---- SongIdentity.SameTrack ----

    [Fact]
    public void SameTrack_IdMatch_True()
        => Assert.True(SongIdentity.SameTrack(Song("abc"), Song("abc", "Other", "X")));

    [Fact]
    public void SameTrack_VideoVersionMatch_True()
        => Assert.True(SongIdentity.SameTrack(
            Song("atv", videoVersion: "omv"),
            Song("omv2", videoVersion: "omv")));

    [Fact]
    public void SameTrack_SongVersionMatch_True()
        => Assert.True(SongIdentity.SameTrack(
            Song("a", songVersion: "s1"),
            Song("b", songVersion: "s1")));

    [Fact]
    public void SameTrack_TitleArtistFallback_True()
        => Assert.True(SongIdentity.SameTrack(
            Song("a", "Feel Good Inc.", "Gorillaz"),
            Song("b", "Feel Good Inc.", "Gorillaz")));

    [Fact]
    public void SameTrack_DifferentTracks_False()
        => Assert.False(SongIdentity.SameTrack(
            Song("a", "Feel Good Inc.", "Gorillaz"),
            Song("b", "Clint Eastwood", "Gorillaz")));

    [Fact]
    public void SameTrack_Null_False()
        => Assert.False(SongIdentity.SameTrack(null, Song("a")));

    // ---- SessionState round-trip ----

    [Fact]
    public void Session_RoundTrip_KeepsFields()
    {
        var song = Song("id1", "Title", "Artist", videoVersion: "id1");
        song.ThumbnailUrl = "thumb";
        song.DurationSeconds = 200;

        var track = SessionState.FromSong(song, keepPins: true);
        var back = SessionState.ToSong(track);

        Assert.Equal("id1", back.Id);
        Assert.Equal("Title", back.Title);
        Assert.Equal("Artist", back.ArtistDisplay);
        Assert.Equal(200, back.DurationSeconds);
        Assert.Equal("id1", back.VideoVersionId);
    }

    [Fact]
    public void Session_DropsGuessedPins_WhenNotKept()
    {
        // Song row whose clip pin differs from its own id (a guess).
        var song = Song("songId", videoVersion: "guessedClip");
        var track = SessionState.FromSong(song, keepPins: false);

        var back = SessionState.ToSong(track);
        Assert.Null(back.VideoVersionId);   // guessed pin dropped -> re-discover
    }

    [Fact]
    public void Session_KeepsSelfIdPin_EvenWhenNotKept()
    {
        // A true clip row: its video pin equals its own id.
        var song = Song("clipId", videoVersion: "clipId");
        var back = SessionState.ToSong(SessionState.FromSong(song, keepPins: false));
        Assert.Equal("clipId", back.VideoVersionId);
    }

    // ---- EntityMappings round-trip ----

    [Fact]
    public void EntityMapping_RoundTrip_PreservesDownloadAndLocal()
    {
        var song = Song("dl1");
        song.DownloadPath = @"C:\music\dl1.m4a";
        song.DownloadedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var entity = song.ToEntity();
        var back = entity.ToSongItem();

        Assert.Equal(song.DownloadPath, back.DownloadPath);
        Assert.Equal(song.DownloadedAt, back.DownloadedAt);
        Assert.True(back.IsDownloaded);
    }

    [Fact]
    public void EntityMapping_LocalPath_SetsIsLocal()
    {
        var song = Song("l1");
        song.LocalPath = @"C:\music\song.mp3";
        var back = song.ToEntity().ToSongItem();
        Assert.True(back.IsLocal);
        Assert.False(back.IsDownloaded);
    }
}
