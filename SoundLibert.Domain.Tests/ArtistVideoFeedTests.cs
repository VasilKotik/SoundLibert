using System.Net;
using System.Text;
using SoundLibert.Domain.Models;
using SoundLibert.Domain.Video;
using SoundLibert.Infra;
using SoundLibert.Infra.InnerTube;

namespace SoundLibert.Domain.Tests;

/// <summary>
/// The artist's videos page reads the real shapes YouTube Music answers with (taken from
/// the live service): shelves on the artist page, the full list behind the Videos shelf's
/// "More", then Videos searches kept to the artist's channel.
/// </summary>
public class ArtistVideoFeedTests
{
    private const string Channel = "UCartist";
    private const string VideoList = "VLOLAKlist";

    private sealed class FakeYouTube(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(body);
            // A search's next page is asked for in the address, a browse's in the body.
            var token = System.Text.RegularExpressions.Regex.Match(request.RequestUri?.Query ?? string.Empty, "[?&]ctoken=([^&]+)");
            return respond(token.Success ? "{\"continuation\":\"" + token.Groups[1].Value + "\"," + body.TrimStart('{') : body);
        }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Failure() =>
        new(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };

    private static (InnerTubeMusicProvider Provider, FakeYouTube YouTube) Create(Func<string, HttpResponseMessage> respond)
    {
        var youTube = new FakeYouTube(respond);
        return (new InnerTubeMusicProvider(new InnerTubeClient(new HttpClient(youTube))), youTube);
    }

    private static bool Asks(string body, string key, string value) =>
        body.Contains($"\"{key}\":\"{value}\"", StringComparison.Ordinal);

    // ---- The shapes ----

    private static string ShelfCard(string id, string title, string subtitle = "Artist • 3B views") =>
        "{\"musicTwoRowItemRenderer\":{\"title\":{\"runs\":[{\"text\":\"" + title + "\"}]}," +
        "\"subtitle\":{\"runs\":[{\"text\":\"" + subtitle.Split(" • ")[0] + "\"}" +
        (subtitle.Contains(" • ") ? ",{\"text\":\" • \"},{\"text\":\"" + subtitle.Split(" • ")[1] + "\"}" : "") + "]}," +
        "\"navigationEndpoint\":{\"watchEndpoint\":{\"videoId\":\"" + id + "\",\"watchEndpointMusicSupportedConfigs\":" +
        "{\"watchEndpointMusicConfig\":{\"musicVideoType\":\"MUSIC_VIDEO_TYPE_OMV\"}}}}}}";

    private static string Carousel(string title, string? more, params string[] cards) =>
        "{\"musicCarouselShelfRenderer\":{\"header\":{\"musicCarouselShelfBasicHeaderRenderer\":{\"title\":{\"runs\":[{\"text\":\"" +
        title + "\"}]}" +
        (more == null ? "" : ",\"moreContentButton\":{\"buttonRenderer\":{\"navigationEndpoint\":{\"browseEndpoint\":{\"browseId\":\"" +
                             more + "\",\"params\":\"listParams\"}}}}") +
        "}},\"contents\":[" + string.Join(",", cards) + "]}}";

    private static string Page(params string[] sections) =>
        "{\"contents\":{\"singleColumnBrowseResultsRenderer\":{\"tabs\":[{\"tabRenderer\":{\"content\":" +
        "{\"sectionListRenderer\":{\"contents\":[" + string.Join(",", sections) + "]}}}}]}}}";

    private static string Row(string id, string title, string? channel = Channel, string artist = "Artist", string length = "3:37") =>
        "{\"musicResponsiveListItemRenderer\":{\"playlistItemData\":{\"videoId\":\"" + id + "\"},\"flexColumns\":[" +
        "{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":{\"runs\":[{\"text\":\"" + title + "\"}]}}}," +
        "{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":{\"runs\":[{\"text\":\"" + artist + "\"" +
        (channel == null ? "" : ",\"navigationEndpoint\":{\"browseEndpoint\":{\"browseId\":\"" + channel + "\"}}") +
        "}]}}}],\"fixedColumns\":[{\"musicResponsiveListItemFixedColumnRenderer\":{\"text\":{\"runs\":[{\"text\":\"" + length + "\"}]}}}]}}";

    /// <summary>The newer next-page form: a last row instead of a "continuations" list.</summary>
    private static string NextRow(string token) =>
        "{\"continuationItemRenderer\":{\"continuationEndpoint\":{\"continuationCommand\":{\"token\":\"" + token + "\"}}}}";

    private static string ListPage(params string[] rows) =>
        Page("{\"musicPlaylistShelfRenderer\":{\"contents\":[" + string.Join(",", rows) + "]}}");

    private static string AppendedPage(params string[] rows) =>
        "{\"onResponseReceivedActions\":[{\"appendContinuationItemsAction\":{\"continuationItems\":[" + string.Join(",", rows) + "]}}]}";

    private static string ClassicPage(string? token, params string[] rows) =>
        "{\"continuationContents\":{\"musicPlaylistShelfContinuation\":{\"contents\":[" + string.Join(",", rows) + "]" +
        (token == null ? "" : ",\"continuations\":[{\"nextContinuationData\":{\"continuation\":\"" + token + "\"}}]") + "}}}";

    private static string Search(string? token, params string[] rows) =>
        "{\"contents\":{\"tabbedSearchResultsRenderer\":{\"tabs\":[{\"tabRenderer\":{\"content\":{\"sectionListRenderer\":{\"contents\":[" +
        "{\"musicShelfRenderer\":{\"contents\":[" + string.Join(",", rows) + "]" +
        (token == null ? "" : ",\"continuations\":[{\"nextContinuationData\":{\"continuation\":\"" + token + "\"}}]") +
        "}}]}}}}]}}}";

    private static string SearchPage(string? token, params string[] rows) =>
        "{\"continuationContents\":{\"musicShelfContinuation\":{\"contents\":[" + string.Join(",", rows) + "]" +
        (token == null ? "" : ",\"continuations\":[{\"nextContinuationData\":{\"continuation\":\"" + token + "\"}}]") + "}}}";

    private static async Task<List<SongItem>> ReadAll(IVideoFeed feed)
    {
        var all = new List<SongItem>();
        for (var i = 0; feed.HasMore && i < 200; i++) all.AddRange(await feed.NextPageAsync());
        return all;
    }

    private static string Ids(IEnumerable<SongItem> videos) => string.Join(",", videos.Select(v => v.Id));

    /// <summary>An artist page with both shelves, and a two-page list behind "More".</summary>
    private static Func<string, HttpResponseMessage> FullArtist(string? searchAnswer = null) => body =>
    {
        if (Asks(body, "continuation", "listNext")) return Json(AppendedPage(Row("v4", "Four")));
        if (Asks(body, "browseId", VideoList))
            return Json(ListPage(Row("v1", "One", length: "4:05"), Row("v3", "Three"), NextRow("listNext")));
        if (Asks(body, "browseId", Channel))
            return Json(Page(
                Carousel("Top songs", null),
                Carousel("Albums", "MPAD", ShelfCard("album", "An album")),
                Carousel("Videos", VideoList, ShelfCard("v1", "One"), ShelfCard("v2", "Two")),
                Carousel("Live performances", "UCartist", ShelfCard("l1", "One (Live)"))));
        if (body.Contains("\"query\"", StringComparison.Ordinal)) return Json(searchAnswer ?? Search(null));
        return Json("{}");
    };

    // ---- Reading ----

    [Fact]
    public async Task First_page_is_the_shelves_of_the_artist_page_and_costs_one_request()
    {
        var (provider, youTube) = Create(FullArtist());
        var feed = provider.OpenArtistVideoFeed(Channel, "Artist");

        var page = await feed.NextPageAsync();

        Assert.Equal("v1,v2,l1", Ids(page));
        Assert.Single(youTube.Requests);
        Assert.True(feed.HasMore);
    }

    [Fact]
    public async Task Goes_on_with_the_full_list_behind_the_shelf_and_its_next_pages()
    {
        var (provider, youTube) = Create(FullArtist());
        var feed = provider.OpenArtistVideoFeed(Channel, "Artist");

        var all = await ReadAll(feed);

        Assert.Equal("v1,v2,l1,v3,v4", Ids(all));
        Assert.Contains(youTube.Requests, r => Asks(r, "browseId", VideoList) && Asks(r, "params", "listParams"));
    }

    [Fact]
    public async Task Reads_the_older_next_page_form_of_the_list_too()
    {
        var (provider, _) = Create(body =>
        {
            if (Asks(body, "continuation", "p2")) return Json(ClassicPage(null, Row("v3", "Three")));
            if (Asks(body, "browseId", VideoList)) return Json(ListPage(Row("v2", "Two"), NextRow("p2")));
            if (Asks(body, "browseId", Channel)) return Json(Page(Carousel("Videos", VideoList, ShelfCard("v1", "One"))));
            return Json(Search(null));
        });

        Assert.Equal("v1,v2,v3", Ids(await ReadAll(provider.OpenArtistVideoFeed(Channel, "Artist"))));
    }

    [Fact]
    public async Task A_shelf_card_gets_its_length_when_the_list_brings_it()
    {
        var (provider, _) = Create(FullArtist());
        var feed = provider.OpenArtistVideoFeed(Channel, "Artist");

        var shelf = await feed.NextPageAsync();
        Assert.Equal(0, shelf[0].DurationSeconds);

        await feed.NextPageAsync();

        Assert.Equal(245, shelf[0].DurationSeconds);
        Assert.Equal("4:05", shelf[0].DurationBadge);
    }

    [Fact]
    public async Task Every_video_is_pinned_to_itself_so_it_plays_as_a_video()
    {
        var (provider, _) = Create(FullArtist());

        var all = await ReadAll(provider.OpenArtistVideoFeed(Channel, "Artist"));

        Assert.All(all, v =>
        {
            Assert.Equal(v.Id, v.VideoVersionId);
            Assert.Equal(v.Id, v.SongVersionId);
        });
        Assert.Equal("MUSIC_VIDEO_TYPE_OMV", all[0].VideoType);
    }

    [Fact]
    public async Task A_video_without_a_picture_gets_the_one_every_video_id_has()
    {
        var (provider, _) = Create(FullArtist());

        var all = await ReadAll(provider.OpenArtistVideoFeed(Channel, "Artist"));

        Assert.Equal("https://i.ytimg.com/vi/v1/hqdefault.jpg", all[0].ThumbnailUrl);
    }

    [Fact]
    public async Task Search_stage_keeps_only_the_artists_own_uploads()
    {
        var search = Search(null,
            Row("own", "Own upload"),
            Row("named", "Named channel", channel: null),
            Row("fan", "Artist - Fan upload", channel: "UCfan", artist: "Some Fan"));
        var (provider, _) = Create(FullArtist(search));

        var all = await ReadAll(provider.OpenArtistVideoFeed(Channel, "Artist"));

        Assert.Contains(all, v => v.Id == "own");
        Assert.Contains(all, v => v.Id == "named");
        Assert.DoesNotContain(all, v => v.Id == "fan");
    }

    [Fact]
    public async Task Search_stage_follows_pages_and_stops_after_pages_with_nothing_of_ours()
    {
        var fanPage = new[] { Row("fan", "Fan", channel: "UCfan", artist: "Fan") };
        var (provider, youTube) = Create(body =>
        {
            if (Asks(body, "continuation", "s1")) return Json(SearchPage("s2", Row("own2", "Own two")));
            if (body.Contains("\"continuation\":\"s", StringComparison.Ordinal)) return Json(SearchPage("sNext", fanPage));
            if (body.Contains("\"query\"", StringComparison.Ordinal)) return Json(Search("s1", Row("own1", "Own one")));
            return Json(Page(Carousel("Videos", null, ShelfCard("v1", "One"))));
        });

        var all = await ReadAll(provider.OpenArtistVideoFeed(Channel, "Artist"));

        Assert.Equal("v1,own1,own2", Ids(all));
        // Three queries, each given up three empty pages after its last find: not eight pages each.
        Assert.True(youTube.Requests.Count < 20, $"{youTube.Requests.Count} requests");
    }

    [Fact]
    public async Task Finds_the_channel_by_name_when_it_is_not_given()
    {
        var artistSearch = Search(null,
            "{\"musicResponsiveListItemRenderer\":{\"navigationEndpoint\":{\"browseEndpoint\":{\"browseId\":\"" + Channel + "\"}},\"flexColumns\":[" +
            "{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":{\"runs\":[{\"text\":\"Artist\"}]}}}," +
            "{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":{\"runs\":[{\"text\":\"Artist\"}]}}}]}}");
        var (provider, _) = Create(body =>
        {
            if (Asks(body, "browseId", Channel)) return Json(Page(Carousel("Videos", null, ShelfCard("v1", "One"))));
            if (Asks(body, "query", "Artist") && !body.Contains("EgWKAQIQAW", StringComparison.Ordinal)) return Json(artistSearch);
            return Json(Search(null));
        });

        var all = await ReadAll(provider.OpenArtistVideoFeed(null, "Artist"));

        Assert.Equal("v1", Ids(all));
    }

    [Fact]
    public async Task An_artist_that_cannot_be_found_still_gets_a_channel_name_search()
    {
        var (provider, _) = Create(body => Json(body.Contains("EgWKAQIQAW", StringComparison.Ordinal)
            ? Search(null, Row("named", "A video", channel: null), Row("fan", "Fan", channel: "UCfan", artist: "Fan"))
            : Search(null)));

        Assert.Equal("named", Ids(await ReadAll(provider.OpenArtistVideoFeed(null, "Artist"))));
    }

    [Fact]
    public async Task A_failed_request_is_asked_again_without_losing_or_repeating_anything()
    {
        var failures = 1;
        var respond = FullArtist();
        var (provider, _) = Create(body =>
        {
            if (Asks(body, "continuation", "listNext") && failures-- > 0) return Failure();
            return respond(body);
        });
        var feed = provider.OpenArtistVideoFeed(Channel, "Artist");

        var all = new List<SongItem>(await feed.NextPageAsync());
        all.AddRange(await feed.NextPageAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => feed.NextPageAsync());
        Assert.True(feed.HasMore);
        all.AddRange(await ReadAll(feed));

        Assert.Equal("v1,v2,l1,v3,v4", Ids(all));
    }

    // ---- What does not belong ----

    [Theory]
    [InlineData("Song #shorts")]
    [InlineData("Song #Short")]
    [InlineData("Song (Lviv | Live) [Shorts]")]
    [InlineData("Song (Karaoke Version)")]
    public async Task Junk_titles_stay_out(string title)
    {
        var (provider, _) = Create(FullArtist(Search(null, Row("junk", title), Row("good", "A good one"))));

        var all = await ReadAll(provider.OpenArtistVideoFeed(Channel, "Artist"));

        Assert.DoesNotContain(all, v => v.Id == "junk");
        Assert.Contains(all, v => v.Id == "good");
    }

    [Fact]
    public async Task A_clip_under_a_minute_is_a_teaser()
    {
        var (provider, _) = Create(FullArtist(Search(null, Row("tease", "Teaser", length: "0:45"), Row("good", "Good"))));

        var all = await ReadAll(provider.OpenArtistVideoFeed(Channel, "Artist"));

        Assert.DoesNotContain(all, v => v.Id == "tease");
    }

    [Fact]
    public async Task A_clip_uploaded_twice_shows_once()
    {
        var (provider, _) = Create(FullArtist(Search(null, Row("again", "One (Official)"), Row("reup", "ONE"))));

        var all = await ReadAll(provider.OpenArtistVideoFeed(Channel, "Artist"));

        Assert.DoesNotContain(all, v => v.Id == "reup");
        Assert.Contains(all, v => v.Id == "again");
    }

    [Fact]
    public async Task Other_peoples_uploads_listed_on_a_made_up_artists_page_stay_out()
    {
        var (provider, _) = Create(body =>
        {
            if (Asks(body, "browseId", VideoList))
                return Json(ListPage(
                    Row("own", "Own upload"),
                    Row("feat", "A song (feat. Artist)", channel: "UCother", artist: "Other"),
                    Row("fan", "Flute healing music", channel: "UCfan", artist: "Some Fan")));
            if (Asks(body, "browseId", Channel))
                return Json(Page(Carousel("Videos", VideoList,
                    ShelfCard("shelf", "On the shelf"),
                    ShelfCard("stranger", "Calm music", "A Stranger • 1K views"))));
            return Json(Search(null));
        });

        Assert.Equal("shelf,own,feat", Ids(await ReadAll(provider.OpenArtistVideoFeed(Channel, "Artist"))));
    }

    [Fact]
    public async Task A_shelf_of_something_else_stays_out()
    {
        var (provider, _) = Create(FullArtist());

        var all = await ReadAll(provider.OpenArtistVideoFeed(Channel, "Artist"));

        Assert.DoesNotContain(all, v => v.Id == "album");
    }

    [Fact]
    public async Task An_empty_name_has_nothing_to_read()
    {
        var (provider, youTube) = Create(_ => Json("{}"));

        var feed = provider.OpenArtistVideoFeed(null, "  ");

        Assert.False(feed.HasMore);
        Assert.Empty(await feed.NextPageAsync());
        Assert.Empty(youTube.Requests);
    }

    // ---- The others ----

    [Fact]
    public async Task New_videos_come_from_their_grid()
    {
        var grid = "{\"gridRenderer\":{\"items\":[" +
                   ShelfCard("n1", "New one", "Someone • 17K views") + "," + ShelfCard("n2", "New two") + "]}}";
        var (provider, youTube) = Create(_ => Json(Page(grid)));

        var videos = await provider.GetNewVideosAsync();

        Assert.Equal("n1,n2", Ids(videos));
        Assert.Equal("Someone", videos[0].ArtistDisplay);
        Assert.Equal(17000, videos[0].ViewCount);
        Assert.Contains(youTube.Requests, r => Asks(r, "browseId", "FEmusic_new_releases_videos"));
    }

    [Fact]
    public async Task New_videos_failure_is_reported_not_hidden_as_empty()
    {
        var (provider, _) = Create(_ => Failure());

        await Assert.ThrowsAnyAsync<Exception>(() => provider.GetNewVideosAsync());
    }

    [Fact]
    public async Task A_search_feed_reads_its_pages_and_ends()
    {
        var (provider, _) = Create(body => Asks(body, "continuation", "s1")
            ? Json(SearchPage(null, Row("b", "Bravo", channel: "UCother", artist: "Other")))
            : Json(Search("s1", Row("a", "Alpha", channel: "UCother", artist: "Other"))));
        var feed = provider.OpenVideoSearchFeed("anything live");

        Assert.Equal("a,b", Ids(await ReadAll(feed)));
        Assert.False(feed.HasMore);
    }
}
