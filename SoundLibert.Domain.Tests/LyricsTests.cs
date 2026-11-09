using SoundLibert.Domain.Lyrics;
using SoundLibert.Domain.Models;

namespace SoundLibert.Domain.Tests;

// Covers the §22/§24 matrix that can run headless: parsing, line/word sync,
// seeking (forward/backward are the same pure call), malformed input, track
// switching (fresh model per track — stateless engine, nothing leaks).
public sealed class SyncedLyricsParserTests
{
    private const string Lrc = "[ti:Title]\n[ar:Artist]\n[offset:+500]\n"
        + "[00:12.40]I remember when...\n"
        + "[00:16.80]Somewhere in...\n"
        + "[00:16.80][00:21.10]And then...\n"
        + "[00:30.00]\n";

    [Fact]
    public void Parse_ValidLrc_ProducesLineSyncWithDerivedEnds()
    {
        var lyrics = SyncedLyricsParser.Parse(Lrc);

        Assert.Equal(LyricsSyncType.Line, lyrics.SyncType);
        // 12.40, 16.80, 16.80, 21.10, 30.00(minus 0.5 offset) => 5 lines sorted.
        Assert.Equal(5, lyrics.Lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(11.9), lyrics.Lines[0].Start);
        Assert.Equal("I remember when...", lyrics.Lines[0].Text);
        // End derives from the next line's start.
        Assert.Equal(lyrics.Lines[1].Start, lyrics.Lines[0].End);
        // Metadata tags produce no lines.
        Assert.DoesNotContain(lyrics.Lines, l => l.Text.Contains("Title"));
    }

    [Fact]
    public void Parse_EmptyTimestampLine_KeepsInstrumentalBreak()
    {
        var lyrics = SyncedLyricsParser.Parse(Lrc);

        var silent = lyrics.Lines[4];
        Assert.Equal(string.Empty, silent.Text);
        Assert.Equal(TimeSpan.FromSeconds(29.5), silent.Start);
    }

    [Fact]
    public void Parse_PlainText_YieldsNoSync()
    {
        var lyrics = SyncedLyricsParser.Parse("line one\nline two");

        Assert.Equal(LyricsSyncType.None, lyrics.SyncType);
        Assert.Equal(2, lyrics.Lines.Count);
        Assert.All(lyrics.Lines, l => Assert.Equal(TimeSpan.Zero, l.Start));
    }

    [Fact]
    public void Parse_MalformedInput_NeverThrowsAndStaysSane()
    {
        var cases = new[] { null, "", "   ", "[not a tag", "[99:99:99]oops", "[offset:abc]x\n[foo]y", "[00:10.00" };
        foreach (var c in cases)
        {
            var ex = Record.Exception(() => SyncedLyricsParser.Parse(c));
            Assert.Null(ex);
        }

        var bad = SyncedLyricsParser.Parse("[99:99:99]oops");
        Assert.Equal(LyricsSyncType.Line, bad.SyncType);
        Assert.Single(bad.Lines);
    }

    [Fact]
    public void Parse_NegativeAfterOffset_ClampsToZero()
    {
        var lyrics = SyncedLyricsParser.Parse("[offset:+5000]\n[00:01.00]early");

        Assert.Equal(TimeSpan.Zero, lyrics.Lines[0].Start);
    }

    [Fact]
    public void Parse_LastLineEnd_UsesTrackDurationWhenKnown()
    {
        var lyrics = SyncedLyricsParser.Parse("[00:10.00]a\n[00:20.00]b", TimeSpan.FromSeconds(60));

        Assert.Equal(TimeSpan.FromSeconds(60), lyrics.Lines[1].End);
    }
}

public sealed class LyricsSyncEngineTests
{
    private static SyncedLyrics Sample() => SyncedLyricsParser.Parse(
        "[00:12.40]A\n[00:16.80]B\n[00:21.10]C\n");

    [Fact]
    public void FindLineIndex_BeforeFirst_ReturnsMinusOne()
    {
        Assert.Equal(-1, LyricsSyncEngine.FindLineIndex(Sample(), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void FindLineIndex_ExactlyOnLine_ReturnsIt()
    {
        Assert.Equal(1, LyricsSyncEngine.FindLineIndex(Sample(), TimeSpan.FromSeconds(16.8)));
    }

    [Fact]
    public void FindLineIndex_BetweenLines_ReturnsPrevious()
    {
        // 17.2s is between B (16.8) and C (21.1) → B.
        Assert.Equal(1, LyricsSyncEngine.FindLineIndex(Sample(), TimeSpan.FromSeconds(17.2)));
    }

    [Fact]
    public void FindLineIndex_ShortlyAfterLast_ReturnsLast()
    {
        // 5 s past the last line (21.1): still highlighted.
        Assert.Equal(2, LyricsSyncEngine.FindLineIndex(Sample(), TimeSpan.FromSeconds(26)));
    }

    [Fact]
    public void FindLineIndex_StaleLine_ExpiresLikeSpotify()
    {
        // 10 min past the last line, or 18 s into an unmarked gap: the
        // highlight clears instead of sticking to a stale line.
        Assert.Equal(-1, LyricsSyncEngine.FindLineIndex(Sample(), TimeSpan.FromMinutes(10)));
        var gappy = SyncedLyricsParser.Parse("[00:12.40]A\n[01:00.00]B\n");
        Assert.Equal(-1, LyricsSyncEngine.FindLineIndex(gappy, TimeSpan.FromSeconds(30)));
        // …but right after the line starts it still highlights.
        Assert.Equal(0, LyricsSyncEngine.FindLineIndex(gappy, TimeSpan.FromSeconds(14)));
    }

    [Fact]
    public void FindLineIndex_PlainOrEmpty_ReturnsMinusOne()
    {
        Assert.Equal(-1, LyricsSyncEngine.FindLineIndex(SyncedLyricsParser.Parse("a\nb"), TimeSpan.FromSeconds(99)));
        Assert.Equal(-1, LyricsSyncEngine.FindLineIndex(SyncedLyrics.Empty, TimeSpan.Zero));
        Assert.Equal(-1, LyricsSyncEngine.FindLineIndex(null, TimeSpan.Zero));
    }

    [Fact]
    public void FindLineIndex_SeekingBothWays_IsStateless()
    {
        var lyrics = Sample();
        Assert.Equal(2, LyricsSyncEngine.FindLineIndex(lyrics, TimeSpan.FromSeconds(25)));
        Assert.Equal(0, LyricsSyncEngine.FindLineIndex(lyrics, TimeSpan.FromSeconds(13)));
        Assert.Equal(1, LyricsSyncEngine.FindLineIndex(lyrics, TimeSpan.FromSeconds(17)));
    }

    [Fact]
    public void FindLineIndex_DuplicateTimestamps_PicksLast()
    {
        var lyrics = SyncedLyricsParser.Parse("[00:10.00]one\n[00:10.00]two\n");

        Assert.Equal(1, LyricsSyncEngine.FindLineIndex(lyrics, TimeSpan.FromSeconds(10)));
    }

    private static SyncedLyricLine WordLine() => new()
    {
        Start = TimeSpan.FromSeconds(12.4),
        End = TimeSpan.FromSeconds(13.6),
        Text = "I remember when",
        Words = new[]
        {
            new SyncedLyricWord { Start = TimeSpan.FromSeconds(12.4), End = TimeSpan.FromSeconds(12.7), Text = "I" },
            new SyncedLyricWord { Start = TimeSpan.FromSeconds(12.7), End = TimeSpan.FromSeconds(13.3), Text = "remember" },
            new SyncedLyricWord { Start = TimeSpan.FromSeconds(13.3), End = TimeSpan.FromSeconds(13.6), Text = "when" },
        }
    };

    [Fact]
    public void FindWordIndex_BeforeInsideAfter()
    {
        var line = WordLine();
        Assert.Equal(-1, LyricsSyncEngine.FindWordIndex(line, TimeSpan.FromSeconds(12.0)));
        Assert.Equal(1, LyricsSyncEngine.FindWordIndex(line, TimeSpan.FromSeconds(13.1)));
        Assert.Equal(2, LyricsSyncEngine.FindWordIndex(line, TimeSpan.FromSeconds(13.5)));
    }

    [Fact]
    public void FindWordIndex_NoWords_ReturnsMinusOne()
    {
        Assert.Equal(-1, LyricsSyncEngine.FindWordIndex(new SyncedLyricLine { Text = "x" }, TimeSpan.FromSeconds(5)));
        Assert.Equal(-1, LyricsSyncEngine.FindWordIndex(null, TimeSpan.FromSeconds(5)));
    }
}

public sealed class LrclibLyricsSelectionTests
{
    // Exercise private selection without changing the provider's public API
    // or making network requests. Payloads use the real LRCLIB response shape.
    private static (object? Track, string Via) Pick(string json, int duration,
        string title = "Clint Eastwood", string artist = "Gorillaz")
    {
        var provider = typeof(SoundLibert.Infra.Lyrics.LrclibLyricsProvider);
        var trackType = provider.GetNestedType("LrcTrack", System.Reflection.BindingFlags.NonPublic)!;
        var listType = typeof(List<>).MakeGenericType(trackType);
        var tracks = System.Text.Json.JsonSerializer.Deserialize(json, listType,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var method = provider.GetMethod("PickBest",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var result = method.Invoke(null, new[] { tracks, duration, title, artist })!;
        return (result.GetType().GetField("Item1")!.GetValue(result),
            (string)result.GetType().GetField("Item2")!.GetValue(result)!);
    }

    [Fact]
    public void KnownDuration_ExactNamesWithWrongRecording_NeverReturnSync()
    {
        var result = Pick("""
            [{"trackName":"Clint Eastwood","artistName":"Gorillaz","duration":396,
              "syncedLyrics":"[00:10.00]words","plainLyrics":"words"}]
            """, 341);
        Assert.NotNull(result.Track);
        Assert.Equal("plain", result.Via);
    }

    [Fact]
    public void KnownDuration_SimilarNamesWithWrongRecording_NeverReturnSync()
    {
        var result = Pick("""
            [{"trackName":"Clint Eastwood (Live)","artistName":"Gorillaz","duration":396,
              "syncedLyrics":"[00:10.00]words","plainLyrics":"words"}]
            """, 341);
        Assert.Equal("plain", result.Via);
    }

    [Theory]
    [InlineData(339.0, "duration")]
    [InlineData(343.0, "duration")]
    [InlineData(343.1, "plain")]
    [InlineData(338.9, "plain")]
    public void DurationBoundary_UsesFractionalSeconds(double duration, string expected)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new[] { new {
            trackName = "Clint Eastwood", artistName = "Gorillaz", duration,
            syncedLyrics = "[00:10.00]words", plainLyrics = "words" } });
        Assert.Equal(expected, Pick(json, 341).Via);
    }

    [Fact]
    public void SyncedCandidate_WinsOverCloserPlainCandidate()
    {
        var result = Pick("""
            [{"trackName":"Clint Eastwood","artistName":"Gorillaz","duration":341,
              "plainLyrics":"plain"},
             {"trackName":"Clint Eastwood","artistName":"Gorillaz","duration":342,
              "syncedLyrics":"[00:10.00]timed"}]
            """, 341);
        Assert.Equal("duration", result.Via);
        Assert.Equal(342.0, result.Track!.GetType().GetProperty("Duration")!.GetValue(result.Track));
    }

    [Fact]
    public void EmptySyncedField_IsPlain()
    {
        Assert.Equal("plain", Pick("""
            [{"trackName":"Clint Eastwood","artistName":"Gorillaz","duration":341,
              "syncedLyrics":"  ","plainLyrics":"words"}]
            """, 341).Via);
    }

    [Fact]
    public void UnknownDuration_AllowsSimilaritySync()
    {
        Assert.Equal("similarity", Pick("""
            [{"trackName":"Clint Eastwood","artistName":"Gorillaz","duration":341,
              "syncedLyrics":"[00:10.00]words"}]
            """, 0).Via);
    }

    [Fact]
    public void SameDuration_WrongSongIsRejected()
    {
        Assert.Null(Pick("""
            [{"trackName":"Unrelated song","artistName":"Other band","duration":341,
              "syncedLyrics":"[00:10.00]wrong"}]
            """, 341).Track);
    }

    [Theory]
    [InlineData("Clint Eastwood (Official Video)", "Clint Eastwood")]
    [InlineData("Clint Eastwood [Official Audio]", "Clint Eastwood")]
    [InlineData("Clint Eastwood (Live)", "Clint Eastwood (Live)")]
    public void CleanTitle_StripsUploadMarkersButPreservesVersion(string title, string expected)
    {
        var method = typeof(SoundLibert.Infra.Lyrics.LrclibLyricsProvider).GetMethod("CleanVideoTitle",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.Equal(expected, method.Invoke(null, new object[] { title }));
    }
}
