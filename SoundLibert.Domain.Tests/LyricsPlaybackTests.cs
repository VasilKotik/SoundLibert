using System.Net;
using System.Net.Http.Headers;
using SoundLibert.Domain.Lyrics;
using SoundLibert.Domain.Models;
using SoundLibert.Infra.Lyrics;

namespace SoundLibert.Domain.Tests;

public sealed class LyricsPlaybackTests
{
    private const string Credits = "Gorillaz, Del The Funky Homosapien";
    private const string PlainRecord = """
        [{"id":1,"trackName":"Clint Eastwood","artistName":"Gorillaz, Del The Funky Homosapien",
          "duration":341,"plainLyrics":"cached words"}]
        """;
    private const string TimedRecords = """
        [{"id":2,"trackName":"Clint Eastwood","artistName":"Gorillaz","duration":341,
          "syncedLyrics":"[00:50.00]long recording","plainLyrics":"long recording"},
         {"id":3,"trackName":"Clint Eastwood","artistName":"Gorillaz","duration":267.021,
          "syncedLyrics":"[00:47.43]short recording\n[01:05.64]next cue","plainLyrics":"short recording"}]
        """;

    [Fact]
    public async Task ClintEastwood_UsesDecodedLengthAndFindsTimingDespiteCombinedCreditsAndPollutedAlbum()
    {
        using var handler = new SearchHandler(uri => uri.Query.Contains("album_name=")
            ? Json("[]") : Json(uri.Query.Contains("artist_name=") ? PlainRecord : TimedRecords));
        using var client = new HttpClient(handler);
        var provider = new LrclibLyricsProvider(client);

        var duration = LyricsPlaybackPolicy.DurationSeconds(341, TimeSpan.FromSeconds(267.021));
        var text = await provider.GetLyricsAsync("1V_xRb0x9aw", "Clint Eastwood", Credits, "1.1B views", duration);
        var parsed = SyncedLyricsParser.Parse(text);

        Assert.Equal(267, duration);
        Assert.Equal(LyricsSyncType.Line, parsed.SyncType);
        Assert.Equal("short recording", parsed.Lines[0].Text);
        Assert.Equal(0, LyricsSyncEngine.FindLineIndex(parsed, TimeSpan.FromSeconds(47.5)));
        Assert.Equal(1, LyricsSyncEngine.FindLineIndex(parsed, TimeSpan.FromSeconds(66)));
        Assert.Equal(3, handler.Requests.Count);
        Assert.DoesNotContain("artist_name=", handler.Requests[^1].Query);
        Assert.DoesNotContain("album_name=", handler.Requests[^1].Query);
    }

    [Fact]
    public async Task TitleOnlyRetrieval_StillRejectsWrongArtistAndWrongDurationTiming()
    {
        const string wrongRecords = """
            [{"trackName":"Clint Eastwood","artistName":"Other Band","duration":267,
              "syncedLyrics":"[00:10.00]wrong artist"},
             {"trackName":"Clint Eastwood","artistName":"Gorillaz","duration":341,
              "syncedLyrics":"[00:10.00]wrong recording"}]
            """;
        using var handler = new SearchHandler(uri => Json(uri.Query.Contains("artist_name=") ? PlainRecord : wrongRecords));
        using var client = new HttpClient(handler);
        var text = await new LrclibLyricsProvider(client).GetLyricsAsync("id", "Clint Eastwood", Credits, null, 267);

        Assert.Equal("cached words", text);
        Assert.Equal(LyricsSyncType.None, SyncedLyricsParser.Parse(text).SyncType);
    }

    [Fact]
    public async Task AcceptedTimedPrimaryHit_DoesNotBroadenTheQuery()
    {
        using var handler = new SearchHandler(_ => Json(TimedRecords));
        using var client = new HttpClient(handler);
        var text = await new LrclibLyricsProvider(client).GetLyricsAsync("id", "Clint Eastwood", Credits, "album", 267);

        Assert.Equal(LyricsSyncType.Line, SyncedLyricsParser.Parse(text).SyncType);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UnknownDuration_DoesNotAddTitleOnlyTimingSearch()
    {
        using var handler = new SearchHandler(_ => Json(PlainRecord));
        using var client = new HttpClient(handler);
        var text = await new LrclibLyricsProvider(client).GetLyricsAsync("id", "Clint Eastwood", Credits, null, 0);

        Assert.Equal(LyricsSyncType.None, SyncedLyricsParser.Parse(text).SyncType);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FailedOptionalAlbumQuery_DoesNotSuppressSafeFallbacks()
    {
        using var handler = new SearchHandler(uri =>
        {
            if (!uri.Query.Contains("album_name="))
                return Json(uri.Query.Contains("artist_name=") ? PlainRecord : TimedRecords);
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        });
        using var client = new HttpClient(handler);
        var text = await new LrclibLyricsProvider(client).GetLyricsAsync("id", "Clint Eastwood", Credits, "1.1B views", 267);

        Assert.Equal(LyricsSyncType.Line, SyncedLyricsParser.Parse(text).SyncType);
        // The short-backoff album query is retried once (4 = album ×2 + 2 fallbacks).
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task LongServerBackoff_StopsFurtherQueries()
    {
        using var handler = new SearchHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
            return response;
        });
        using var client = new HttpClient(handler);
        var text = await new LrclibLyricsProvider(client).GetLyricsAsync("id", "Clint Eastwood", Credits, "album", 267);

        Assert.Null(text);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("timeout")]
    [InlineData("backoff")]
    public async Task FailedOptionalLookup_RetainsAcceptedPlainLyrics(string failure)
    {
        using var handler = new SearchHandler(uri =>
        {
            if (uri.Query.Contains("artist_name=")) return Json(PlainRecord);
            if (failure == "transport") throw new HttpRequestException("offline");
            if (failure == "timeout") throw new TaskCanceledException("request timeout");
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
            return response;
        });
        using var client = new HttpClient(handler);
        var text = await new LrclibLyricsProvider(client).GetLyricsAsync("id", "Clint Eastwood", Credits, null, 267);

        Assert.Equal("cached words", text);
        Assert.Equal(LyricsSyncType.None, SyncedLyricsParser.Parse(text).SyncType);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(341, 267.021, 267)]
    [InlineData(341, 0, 341)]
    [InlineData(0, 267.021, 267)]
    [InlineData(-1, 0, 0)]
    public void DurationPolicy_PrefersOpenedAudioAndFallsBackOnlyWhenUnavailable(int catalogue, double decoded, int expected)
        => Assert.Equal(expected, LyricsPlaybackPolicy.DurationSeconds(catalogue, TimeSpan.FromSeconds(decoded)));

    [Theory]
    [InlineData("1V_xRb0x9aw", "1V_xRb0x9aw", 341, 267.021, false)]
    [InlineData("1V_xRb0x9aw", "1V_xRb0x9aw", 267, 267.021, true)]
    [InlineData("1V_xRb0x9aw", "I7yqFVEvdY0", 341, 341, false)]
    [InlineData("same", "same", 0, 267, false)]
    [InlineData("same", "same", 267, 269, true)]
    [InlineData("same", "same", 267, 269.01, false)]
    public void LegacyCacheTiming_RequiresSameRecordingAndConsistentDuration(string rowId, string playbackId,
        int catalogue, double decoded, bool expected)
        => Assert.Equal(expected, LyricsPlaybackPolicy.CanUseLegacyTiming(rowId, playbackId, catalogue, TimeSpan.FromSeconds(decoded)));

    [Fact]
    public void RecordingCache_DoesNotShareSongVideoOrDifferentDecodedLengths()
    {
        var song = LyricsPlaybackPolicy.CacheKey("owner", "I7yqFVEvdY0", 341);
        var video = LyricsPlaybackPolicy.CacheKey("owner", "1V_xRb0x9aw", 267);
        Assert.NotEqual(song, video);
        Assert.NotEqual(video, LyricsPlaybackPolicy.CacheKey("owner", "1V_xRb0x9aw", 341));
        Assert.NotEqual(video, LyricsPlaybackPolicy.CacheKey("other-owner", "1V_xRb0x9aw", 267));
        Assert.NotEqual(LyricsPlaybackPolicy.CacheKey("owner", "id:267", 341), LyricsPlaybackPolicy.CacheKey("owner", "id", 267));
    }

    private static HttpResponseMessage Json(string text)
        => new(HttpStatusCode.OK) { Content = new StringContent(text, System.Text.Encoding.UTF8, "application/json") };

    private sealed class SearchHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
        }
    }
}
