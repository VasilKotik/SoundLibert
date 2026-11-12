using SoundLibert.Domain.Models;
using SoundLibert.Infra;
using SoundLibert.Infra.InnerTube.Models;
using static SoundLibert.Infra.ClipChoice;

namespace SoundLibert.Domain.Tests;

/// <summary>
/// The rules behind the clip that stands behind a song, with the cases that went
/// wrong in a real library: a fan's 19-second teaser for "Gods Don't Pray", fan
/// channels named after the artist, a label's clip that was missed, a cover image
/// held for the length of the song.
/// </summary>
public class ClipChoiceTests
{
    private static readonly string[] Dragons = { "Imagine Dragons" };

    [Theory]
    [InlineData("Imagine Dragons", ChannelKind.Artist)]
    [InlineData("ImagineDragonsVEVO", ChannelKind.Artist)]
    [InlineData("Imagine Dragons Official", ChannelKind.Artist)]
    [InlineData("Imagine Dragons - Topic", ChannelKind.Topic)]
    [InlineData("Imagine Dragons Fan", ChannelKind.Fan)]
    [InlineData("ImagineDragons Team", ChannelKind.Fan)]
    [InlineData("Imagine Dragons Radio", ChannelKind.Fan)]
    [InlineData("Best of Imagine Dragons Hits", ChannelKind.Fan)]
    [InlineData("Atlantic Records", ChannelKind.Other)]
    [InlineData("Song des Tages", ChannelKind.Other)]
    public void Channels_AreTheArtistsOrSomebodyElses(string author, ChannelKind expected)
        => Assert.Equal(expected, ClassifyChannel(author, Dragons));

    [Fact]
    public void Channels_NeedTheWholeName()
    {
        Assert.Equal(ChannelKind.Other, ClassifyChannel("Asian Music", new[] { "Sia" }));
        Assert.Equal(ChannelKind.Artist, ClassifyChannel("Official Arctic Monkeys", new[] { "Arctic Monkeys" }));
        Assert.Equal(ChannelKind.Artist, ClassifyChannel("TheKidLAROIVEVO", new[] { "The Kid LAROI" }));
        // An unknown word next to the name proves nothing either way.
        Assert.Equal(ChannelKind.Other, ClassifyChannel("Kalush Orchestra", new[] { "KALUSH" }));
    }

    [Theory]
    [InlineData(1920, 1080, true)]
    [InlineData(1920, 816, true)]
    [InlineData(640, 480, true)]
    [InlineData(574, 480, false)]
    [InlineData(1080, 1080, false)]
    [InlineData(1080, 1920, false)]
    public void Frames_MustFillAWideBackground(int width, int height, bool wide)
        => Assert.Equal(wide, IsWideFrame(width, height));

    [Theory]
    [InlineData(19, 170, false)]
    [InlineData(19, 0, false)]
    [InlineData(45, 0, false)]
    [InlineData(211, 211, true)]
    [InlineData(354, 275, true)]
    [InlineData(700, 200, false)]
    [InlineData(1200, 0, false)]
    [InlineData(0, 200, true)]
    public void Lengths_MustFitTheSong(int clip, int song, bool plausible)
        => Assert.Equal(plausible, IsPlausibleLength(clip, song));

    [Fact]
    public void StillCovers_AreNoClips()
    {
        Assert.True(IsStillPicture(0.0165));
        Assert.False(IsStillPicture(0.039));
        Assert.False(IsStillPicture(0));
    }

    [Theory]
    [InlineData("#imaginedragons gods don't pray #shorts", true)]
    [InlineData("Gods Don't Pray (Vertical Video)", true)]
    [InlineData("Gods Don't Pray - snippet", true)]
    [InlineData("Gods Don't Pray (Official Music Video)", false)]
    public void ShortForms_AreNoClips(string title, bool isShort)
        => Assert.Equal(isShort, IsShortFormTitle(title, "Gods Don't Pray"));

    [Fact]
    public void ShortForms_SpareTheSongsOwnName()
        => Assert.False(IsShortFormTitle("Vertical (Official Video)", "Vertical"));

    [Theory]
    [InlineData("La Noche Brillará/ The Night Begins to Shine-B.E.R. [Letra en español]")]
    [InlineData("Teddy Swims - Lose Control (Tradução) (Legendado) (Ao Vivo)")]
    [InlineData("Океан Ельзи - Обійми (текст)")]
    public void LyricsAbroad_AreNoClips(string title)
        => Assert.True(IsLyricsUploadTitle(title));

    [Theory]
    [InlineData("Lose Control (Official Music Video)", 1)]
    [InlineData("Lose Control (Piano Version)", 2)]
    [InlineData("Teddy Swims - Lose Control (The Village Sessions)", 2)]
    [InlineData("Teddy Swims - Lose Control (Live)", 2)]
    [InlineData("Marcus King - Lose Control [Official Visualizer]", 3)]
    [InlineData("Teddy Swims - Lose Control (Official Lyric Video)", 0)]
    [InlineData("Teddy Swims - Lose Control (Official Audio)", 0)]
    [InlineData("Teddy Swims - Lose Control (Lyric Visualizer)", 0)]
    [InlineData("Lose Control (Piano Cover)", 0)]
    [InlineData("Lose Control (Slowed + Reverb)", 0)]
    public void Tiers_PutTheMusicVideoFirst(string title, int tier)
        => Assert.Equal(tier, InnerTubeMusicProvider.ClipTier(title, "Lose Control"));

    [Fact]
    public void ArtistNames_IncludeEveryoneOnTheLine()
    {
        var song = new SongItem { Artists = new[] { new Artist { Name = "Lil Wayne, Wiz Khalifa & Imagine Dragons" } } };
        var names = InnerTubeMusicProvider.ArtistNamesOf(song);
        Assert.Equal("Lil Wayne, Wiz Khalifa & Imagine Dragons", names[0]);
        Assert.Contains("Lil Wayne", names);
        Assert.Contains("Wiz Khalifa", names);
        Assert.Contains("Imagine Dragons", names);
    }

    // ---- Whole checks against a video's own player data ----

    private const string ArtistChannel = "UCartist000000000000000";

    private static SongItem Song(string title, int seconds, string artist = "Imagine Dragons")
        => new() { Id = "song", Title = title, DurationSeconds = seconds, Artists = new[] { new Artist { Name = artist, Id = ArtistChannel } } };

    private static PlayerResponse Video(string id, string title, string author, int seconds,
        int width = 1920, int height = 1080, int bitrate = 3_000_000, string? channelId = null) => new()
    {
        PlayabilityStatus = new PlayabilityStatus { Status = "OK" },
        VideoDetails = new VideoDetails
        {
            VideoId = id, Title = title, Author = author, ChannelId = channelId,
            LengthSeconds = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        },
        StreamingData = new StreamingData
        {
            AdaptiveFormats = new List<StreamingFormat>
            {
                new() { MimeType = "video/mp4", Width = width, Height = height, Fps = 24, Bitrate = bitrate }
            }
        }
    };

    private static InnerTubeMusicProvider.ClipCheck Judge(SongItem song, PlayerResponse video, string? rowType, bool pinned = false)
        => InnerTubeMusicProvider.JudgeClip(song, InnerTubeMusicProvider.ArtistNamesOf(song),
            InnerTubeMusicProvider.ArtistChannelsOf(song), video.VideoDetails!.VideoId!, rowType, pinned, video);

    [Fact]
    public void AFansTeaser_NeverStandsBehindTheSong()
    {
        var song = Song("Gods Don't Pray", 170);
        var teaser = Video("-fyf-tWIyWA", "#imaginedragons gods don't pray", "Imagine Dragons Fan", 19, 574, 480);
        Assert.Equal(InnerTubeMusicProvider.ClipVerdict.Reject, Judge(song, teaser, "MUSIC_VIDEO_TYPE_UGC").Verdict);
        Assert.Equal(InnerTubeMusicProvider.ClipVerdict.Reject, Judge(song, teaser, null, pinned: true).Verdict);
    }

    [Fact]
    public void ALabelsClip_PassesWhenTaggedAndNamingTheArtist()
    {
        var song = Song("Sucker for Pain", 244, "Lil Wayne, Wiz Khalifa & Imagine Dragons");
        var clip = Video("-59jGD4WrmE",
            "Lil Wayne, Wiz Khalifa & Imagine Dragons w/ Logic & Ty Dolla $ign ft X Ambassadors - Sucker for Pain",
            "Atlantic Records", 298);
        Assert.Equal(InnerTubeMusicProvider.ClipVerdict.Accept, Judge(song, clip, "MUSIC_VIDEO_TYPE_OMV").Verdict);
        Assert.Equal(InnerTubeMusicProvider.ClipVerdict.Reject, Judge(song, clip, "MUSIC_VIDEO_TYPE_UGC").Verdict);
    }

    [Fact]
    public void AnotherArtistsSongOfTheSameName_IsRefused()
    {
        var song = Song("Sucker", 225, "Marcus King");
        var other = Video("CnAmeh0-E-U", "Jonas Brothers - Sucker (Official Video)", "JonasBrothersVEVO", 201);
        Assert.Equal(InnerTubeMusicProvider.ClipVerdict.Reject, Judge(song, other, "MUSIC_VIDEO_TYPE_OMV").Verdict);
    }

    [Fact]
    public void AChannelCalledLikeTheArtist_MustProveItself()
    {
        var song = Song("Battle Cry", 274);
        var impostor = Video("PTeuj1YOKbs", "Imagine Dragons : Battle Cry", "ImagineDragons", 273);
        Assert.Equal(InnerTubeMusicProvider.ClipVerdict.Reject, Judge(song, impostor, "MUSIC_VIDEO_TYPE_UGC").Verdict);

        var own = Video("own", "Imagine Dragons - Battle Cry", "ImagineDragons", 273, channelId: ArtistChannel);
        Assert.Equal(InnerTubeMusicProvider.ClipVerdict.Accept, Judge(song, own, "MUSIC_VIDEO_TYPE_UGC").Verdict);

        var vevo = Video("vevo", "Imagine Dragons - Battle Cry (Official Music Video)", "ImagineDragonsVEVO", 273);
        Assert.Equal(InnerTubeMusicProvider.ClipVerdict.Accept, Judge(song, vevo, null).Verdict);
    }

    [Fact]
    public void ACoverHeldStill_IsNoClip()
    {
        var song = Song("Not Like Us", 275, "Kendrick Lamar");
        // 0.0165 bits per pixel per frame, as measured on the real upload.
        var still = Video("T6eK-2OQtew", "Not Like Us", "Kendrick Lamar", 273, 3840, 2160, bitrate: 3_280_000);
        var check = Judge(song, still, "MUSIC_VIDEO_TYPE_OMV");
        Assert.Equal(InnerTubeMusicProvider.ClipVerdict.Reject, check.Verdict);
        Assert.Contains("still", check.Note);
    }

    [Fact]
    public void AVerticalUpload_IsNoClip()
    {
        var song = Song("Believer", 205);
        var vertical = Video("v", "Imagine Dragons - Believer (Official Music Video)", "ImagineDragonsVEVO", 217, 1080, 1920);
        Assert.Equal(InnerTubeMusicProvider.ClipVerdict.Reject, Judge(song, vertical, "MUSIC_VIDEO_TYPE_OMV").Verdict);
    }

    [Fact]
    public void ASavedLabelClip_StaysWithoutItsSearchRow()
    {
        var song = Song("Monster", 179, "Skillet");
        var clip = Video("1mjlM_RnsVE", "Skillet - Monster (Official Video)", "Atlantic Records", 187, 640, 360, bitrate: 600_000);
        Assert.Equal(InnerTubeMusicProvider.ClipVerdict.Accept, Judge(song, clip, null, pinned: true).Verdict);
    }

    [Fact]
    public void TheMusicVideo_IsAcceptedAsTierOne()
    {
        var song = Song("Believer", 205);
        var clip = Video("7wtfhZwyrcc", "Imagine Dragons - Believer (Official Music Video)", "ImagineDragonsVEVO", 217);
        var check = Judge(song, clip, "MUSIC_VIDEO_TYPE_OMV");
        Assert.Equal(InnerTubeMusicProvider.ClipVerdict.Accept, check.Verdict);
        Assert.Equal(1, check.Tier);
    }
}
