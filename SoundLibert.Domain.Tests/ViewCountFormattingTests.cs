using SoundLibert.Domain.Models;

namespace SoundLibert.Domain.Tests;

public class ViewCountFormattingTests
{
    [Theory]
    [InlineData(1_000_000_000L, "1B views")]
    [InlineData(1_200_000_000L, "1.2B views")]
    [InlineData(723_000_000L, "723M views")]
    [InlineData(45_000L, "45K views")]
    [InlineData(4_519L, "4.5K views")]
    [InlineData(0L, "0 views")]
    public void FormatViews_UsesMagnitudeLabels(long views, string expected)
        => Assert.Equal(expected, ViewCountFormatting.FormatViews(views));

    [Fact]
    public void SongItem_ViewCountText_EmptyUntilKnown()
    {
        var song = new SongItem { Id = "x" };
        Assert.False(song.HasViewCount);
        Assert.Equal(string.Empty, song.ViewCountText);

        // A row of unknown kind is a song: songs are played, videos are viewed.
        song.ViewCount = 999;
        Assert.True(song.HasViewCount);
        Assert.Equal("999 plays", song.ViewCountText);
    }

    [Fact]
    public void SongItem_ViewCountText_AnAudioTrackIsPlayed()
    {
        Assert.Equal("7B plays", new SongItem { Id = "x", ViewCount = 7_000_000_000, VideoType = "MUSIC_VIDEO_TYPE_ATV" }.ViewCountText);
        Assert.Equal("3B views", new SongItem { Id = "x", ViewCount = 3_000_000_000, VideoType = "MUSIC_VIDEO_TYPE_OMV" }.ViewCountText);
    }

    [Fact]
    public void SongItem_ViewCountText_YouTubesOwnWordWins()
    {
        // An album page lists the clip's id for a song and says "plays": so do we.
        Assert.Equal("252M plays", new SongItem
        {
            Id = "x", ViewCount = 252_000_000, VideoType = "MUSIC_VIDEO_TYPE_OMV", CountsPlays = true
        }.ViewCountText);
        Assert.Equal("1.2B views", new SongItem { Id = "x", ViewCount = 1_200_000_000, CountsPlays = false }.ViewCountText);
    }

    [Fact]
    public void SongItem_AnAlbumsTrackIsASongWhateverItsId()
    {
        var track = new SongItem { Id = "x", ViewCount = 11_000_000, VideoType = "MUSIC_VIDEO_TYPE_OMV", IsAlbumTrack = true };
        Assert.False(track.IsVideoClip);
        Assert.Equal("11M plays", track.ViewCountText);
    }
}
