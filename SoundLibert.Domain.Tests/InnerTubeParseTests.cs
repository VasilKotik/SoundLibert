using SoundLibert.Infra.InnerTube;
using SoundLibert.Infra.InnerTube.Models;

namespace SoundLibert.Domain.Tests;

public class InnerTubeParseTests
{
    private static RunsText Runs(params string[] texts) => new()
    {
        Runs = texts.Select(t => new Run { Text = t }).ToList()
    };

    // ---- SplitBySeparator ----

    [Fact]
    public void SplitBySeparator_ThreeSegments()
    {
        var runs = Runs("Gorillaz", " • ", "Album", " • ", "3:45").Runs!;
        var segments = runs.SplitBySeparator();
        Assert.Equal(3, segments.Count);
        Assert.Equal("Gorillaz", segments[0][0].Text);
        Assert.Equal("3:45", segments[2][0].Text);
    }

    // ---- ExtractArtists ----

    [Fact]
    public void ExtractArtists_SingleArtist_FromFirstSegment()
    {
        var artists = Runs("Gorillaz", " • ", "Album", " • ", "3:45").ExtractArtists();
        var name = Assert.Single(artists);
        Assert.Equal("Gorillaz", name.Name);
    }

    [Fact]
    public void ExtractArtists_MultipleArtists_EvenRuns()
    {
        // "A & B" is three runs: [A][" & "][B] -> artists A and B.
        var artists = Runs("A", " & ", "B", " • ", "Song", " • ", "3:45").ExtractArtists();
        Assert.Equal(2, artists.Count);
        Assert.Equal("A", artists[0].Name);
        Assert.Equal("B", artists[1].Name);
    }

    [Fact]
    public void ExtractArtists_Null_Empty()
        => Assert.Empty(((RunsText?)null).ExtractArtists());

    // ---- ExtractDurationSeconds ----

    [Fact]
    public void ExtractDuration_LastSegment()
        => Assert.Equal(225, Runs("Gorillaz", " • ", "Album", " • ", "3:45").ExtractDurationSeconds());

    [Fact]
    public void ExtractDuration_Hours()
        => Assert.Equal(3723, Runs("Artist", " • ", "Album", " • ", "1:02:03").ExtractDurationSeconds());

    [Fact]
    public void ExtractDuration_NoSegments_Zero()
        => Assert.Equal(0, Runs().ExtractDurationSeconds());

    // ---- ParseTime ----

    [Theory]
    [InlineData("3:45", 225)]
    [InlineData("1:02:03", 3723)]
    [InlineData("0:07", 7)]
    [InlineData("bad", 0)]
    [InlineData("", 0)]
    public void ParseTime_Various(string text, int expected)
        => Assert.Equal(expected, ThumbnailExtensions.ParseTime(text));
}
