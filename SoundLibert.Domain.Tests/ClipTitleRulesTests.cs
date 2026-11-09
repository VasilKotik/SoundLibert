using SoundLibert.Infra;

namespace SoundLibert.Domain.Tests;

public class ClipTitleRulesTests
{
    [Theory]
    [InlineData("Alive (Official Video)")]
    [InlineData("Discover")]
    [InlineData("Recover (Official Video)")]
    [InlineData("Detour (Official Video)")]
    [InlineData("Tourniquet")]
    [InlineData("Obsession")]
    [InlineData("Oliver Tree - Life Goes On (Official Video)")]
    [InlineData("Audioslave - Like a Stone (Official Video)")]
    [InlineData("Oasis - Live Forever (Official Video)")]
    [InlineData("Live and Let Die (Official Video)")]
    [InlineData("Cover Me Up")]
    [InlineData("Anti-Hero (Official Music Video)")]
    [InlineData("Song - Remastered 2011")]
    public void WholeWords_KeepRealClips(string title)
        => Assert.True(ClipTitleRules.IsRealMusicVideoTitle(title));

    [Theory]
    [InlineData("Hello (Live at Wembley)")]
    [InlineData("Hello - Live")]
    [InlineData("Hello Live")]
    [InlineData("Hello - Live 2019")]
    [InlineData("Hello (Official Lyric Video)")]
    [InlineData("Hello Lyric Video")]
    [InlineData("Hello (Lyrics)")]
    [InlineData("Hello (Official Audio)")]
    [InlineData("Hello - Acoustic Version")]
    [InlineData("Hello (Piano Cover)")]
    [InlineData("Hello | Spotify Sessions")]
    [InlineData("Hello (Slowed + Reverb)")]
    [InlineData("Hello 8D")]
    [InlineData("Hello: Tiny Desk Concert")]
    [InlineData("Hello (Official Visualizer)")]
    public void NonClips_AreRejected(string title)
        => Assert.False(ClipTitleRules.IsRealMusicVideoTitle(title));

    [Theory]
    [InlineData("Long Live (Official Video)", "Long Live", true)]
    [InlineData("Taylor Swift - Long Live", "Long Live", true)]
    [InlineData("LONG LIVE", "Long Live", true)]
    [InlineData("Long Live (Live)", "Long Live", false)]
    [InlineData("Hallelujah Cover", "Hallelujah", false)]
    [InlineData("Live - Lightning Crashes (Official Video)", "Lightning Crashes", true)]
    [InlineData("Stay (Acoustic) [Official Video]", "Stay (Acoustic)", true)]
    [InlineData("Stay [Official Video]", "Stay (Acoustic)", true)]
    [InlineData("Reverb", "Reverb", true)]
    public void TheSongsOwnWords_DoNotCount(string title, string song, bool expected)
        => Assert.Equal(expected, ClipTitleRules.IsRealMusicVideoTitle(title, song));

    [Theory]
    [InlineData("Hello Karaoke Version")]
    [InlineData("Hello (Cover)")]
    [InlineData("Hello - Remix")]
    [InlineData("Nightcore - Hello")]
    [InlineData("Hello 10 Hours")]
    [InlineData("Hello (Fan Made)")]
    [InlineData("Hello Tribute")]
    public void Junk_IsRejected(string title)
        => Assert.True(ClipTitleRules.IsRejectedUploadTitle(title));

    [Theory]
    [InlineData("Discover (Official Video)")]
    [InlineData("Tribute (Official Video)")]
    [InlineData("Cover Me Up")]
    [InlineData("24 Hours")]
    [InlineData("Hello (Live at Wembley)")]
    [InlineData("Hello (Official Lyric Video)")]
    public void OfficialUploads_AreNotJunk(string title)
        => Assert.False(ClipTitleRules.IsRejectedUploadTitle(title));

    [Theory]
    [InlineData("Hello", "Adele - Hello (Official Video)", true)]
    [InlineData("Hello (Remastered)", "Hello", true)]
    [InlineData("22", "Taylor Swift - 22 (Official Video)", true)]
    [InlineData("Hey", "Pixies - Hey", true)]
    [InlineData("青と夏", "Mrs. GREEN APPLE「青と夏」Official Music Video", true)]
    [InlineData("AM", "Arctic Monkeys - A.M.", true)]
    [InlineData("7", "7 (Official Music Video)", true)]
    [InlineData("Hey", "They Know", false)]
    [InlineData("7", "7 Rings", false)]
    [InlineData("Hello", "Goodbye", false)]
    [InlineData("", "Hello", false)]
    [InlineData("Stay", "", false)]
    public void TitlesMatch_ShortTitlesAsWholeWords(string song, string upload, bool expected)
        => Assert.Equal(expected, ClipTitleRules.TitlesMatch(song, upload));
}
