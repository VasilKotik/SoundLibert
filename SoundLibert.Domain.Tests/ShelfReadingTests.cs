using System.Net;
using System.Text;
using SoundLibert.Domain.Models;
using SoundLibert.Infra;
using SoundLibert.Infra.InnerTube;

namespace SoundLibert.Domain.Tests;

/// <summary>
/// Home, Explore, charts, moods, search and artist pages read the shapes YouTube Music
/// really answers with (taken from the live service).
/// </summary>
public class ShelfReadingTests
{
    private sealed class FakeYouTube(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            if (body.Length == 0) return Json("{}"); // the visitor id bootstrap
            Requests.Add(body);
            return respond(body);
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

    private static string Text(string text) => "{\"runs\":[{\"text\":\"" + text + "\"}]}";

    private static string Thumb(string url) =>
        "{\"musicThumbnailRenderer\":{\"thumbnail\":{\"thumbnails\":[{\"url\":\"" + url + "\"}]}}}";

    private static string BrowseCard(string id, string title, string subtitle) =>
        "{\"musicTwoRowItemRenderer\":{\"title\":" + Text(title) + ",\"subtitle\":" + Subtitle(subtitle.Split(" • ")) +
        ",\"thumbnailRenderer\":" + Thumb("https://img/" + id) +
        ",\"navigationEndpoint\":{\"browseEndpoint\":{\"browseId\":\"" + id + "\"}}}}";

    private static string WatchCard(string id, string title, string artist, string views, string type) =>
        "{\"musicTwoRowItemRenderer\":{\"title\":" + Text(title) +
        ",\"subtitle\":{\"runs\":[{\"text\":\"" + artist + "\"},{\"text\":\" • \"},{\"text\":\"" + views + "\"}]}" +
        ",\"navigationEndpoint\":{\"watchEndpoint\":{\"videoId\":\"" + id + "\",\"watchEndpointMusicSupportedConfigs\":" +
        "{\"watchEndpointMusicConfig\":{\"musicVideoType\":\"" + type + "\"}}}}}}";

    private static string SongRow(string id, string title, params string[] columns) =>
        "{\"musicResponsiveListItemRenderer\":{\"playlistItemData\":{\"videoId\":\"" + id + "\"},\"flexColumns\":[" +
        "{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":" + Text(title) + "}}" +
        string.Concat(columns.Select(c => ",{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":" + c + "}}")) + "]}}";

    private static string Subtitle(params string[] parts) =>
        "{\"runs\":[" + string.Join(",{\"text\":\" • \"},", parts.Select(p => "{\"text\":\"" + p + "\"}")) + "]}";

    private static string ChartRow(string id, string name, string rank, string icon, string subscribers) =>
        "{\"musicResponsiveListItemRenderer\":{\"navigationEndpoint\":{\"browseEndpoint\":{\"browseId\":\"" + id + "\"}}," +
        "\"customIndexColumn\":{\"musicCustomIndexColumnRenderer\":{\"text\":" + Text(rank) + ",\"icon\":{\"iconType\":\"" + icon + "\"}}}," +
        "\"flexColumns\":[{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":" + Text(name) + "}}," +
        "{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":" + Text(subscribers) + "}}]}}";

    private static string Carousel(string title, string? more, params string[] cards) =>
        "{\"musicCarouselShelfRenderer\":{\"header\":{\"musicCarouselShelfBasicHeaderRenderer\":{\"title\":" + Text(title) +
        (more == null ? "" : ",\"moreContentButton\":{\"buttonRenderer\":{\"navigationEndpoint\":{\"browseEndpoint\":{\"browseId\":\"" +
                             more + "\",\"params\":\"moreParams\"}}}}") +
        "}},\"contents\":[" + string.Join(",", cards) + "]}}";

    private static string Page(string extra, params string[] sections) =>
        "{\"contents\":{\"singleColumnBrowseResultsRenderer\":{\"tabs\":[{\"tabRenderer\":{\"content\":" +
        "{\"sectionListRenderer\":{" + extra + "\"contents\":[" + string.Join(",", sections) + "]}}}}]}}}";

    private static string Chips(params (string Text, string Params)[] chips) =>
        "\"header\":{\"chipCloudRenderer\":{\"chips\":[" +
        string.Join(",", chips.Select(c => "{\"chipCloudChipRenderer\":{\"text\":" + Text(c.Text) +
            ",\"navigationEndpoint\":{\"browseEndpoint\":{\"browseId\":\"FEmusic_home\",\"params\":\"" + c.Params + "\"}}}}")) + "]}},";

    private static string NextToken(string token) =>
        "\"continuations\":[{\"nextContinuationData\":{\"continuation\":\"" + token + "\"}}],";

    private static string Portion(string? token, params string[] sections) =>
        "{\"continuationContents\":{\"sectionListContinuation\":{" + (token == null ? "" : NextToken(token)) +
        "\"contents\":[" + string.Join(",", sections) + "]}}}";

    // ---- Home ----

    [Fact]
    public async Task Home_reads_its_shelves_moods_and_next_portion()
    {
        var (provider, _) = Create(_ => Json(Page(
            Chips(("Relax", "chipA"), ("Party", "chipB")) + NextToken("t1"),
            Carousel("Quick picks", null, SongRow("s1", "Alma Rota", Subtitle("Dalvin", "48M plays"), Text("La Bachata"))),
            Carousel("Take it easy", null, BrowseCard("VLRDCLAK1", "Mellow Pop", "Lewis Capaldi, Ed Sheeran")))));

        var home = await provider.GetHomePageAsync();

        Assert.Equal(new[] { "Relax", "Party" }, home.Chips.Select(c => c.Text));
        Assert.Equal("chipB", home.Chips[1].Params);
        Assert.Equal("t1", home.ContinuationToken);
        Assert.Equal(new[] { "Quick picks", "Take it easy" }, home.Shelves.Select(s => s.Title));
        Assert.Equal(ShelfLayout.Rows, home.Shelves[0].Layout);
        Assert.Equal(ShelfLayout.Cards, home.Shelves[1].Layout);
    }

    [Fact]
    public async Task Home_quick_picks_are_songs_with_their_plays()
    {
        var (provider, _) = Create(_ => Json(Page("",
            Carousel("Quick picks", null, SongRow("s1", "Alma Rota", Subtitle("Dalvin", "48M plays"), Text("La Bachata"))))));

        var song = (await provider.GetHomePageAsync()).Shelves[0].Items[0];

        Assert.Equal(ShelfItemKind.Song, song.Kind);
        Assert.Equal("Dalvin • 48M plays", song.Subtitle);
        Assert.Equal("Dalvin", song.Song!.ArtistDisplay);
        Assert.Equal(48_000_000, song.Song.ViewCount);
    }

    [Fact]
    public async Task Home_next_portion_comes_by_its_token()
    {
        var (provider, youTube) = Create(_ => Json(Portion("t2",
            Carousel("Ukrainian hits", null, BrowseCard("VLRDCLAK2", "00's hits", "Okean Elzy, TNMK")))));

        var next = await provider.GetHomeContinuationAsync("t1");

        Assert.Equal("Ukrainian hits", next.Shelves[0].Title);
        Assert.Equal("t2", next.ContinuationToken);
        Assert.Contains(youTube.Requests, r => Asks(r, "continuation", "t1"));
    }

    [Fact]
    public async Task A_mood_chip_asks_for_the_same_feed_with_its_params()
    {
        var (provider, youTube) = Create(_ => Json(Page("")));

        await provider.GetHomePageAsync("chipB");

        Assert.Contains(youTube.Requests, r => Asks(r, "browseId", "FEmusic_home") && Asks(r, "params", "chipB"));
    }

    [Fact]
    public async Task A_shelf_with_nothing_readable_is_left_out()
    {
        var (provider, _) = Create(_ => Json(Page("", Carousel("Podcasts", null, BrowseCard("MPSPabc", "A show", "Podcast")))));

        Assert.Empty((await provider.GetHomePageAsync()).Shelves);
    }

    // ---- What a card is ----

    [Theory]
    [InlineData("UCabc", ShelfItemKind.Artist)]
    [InlineData("MPREb_abc", ShelfItemKind.Album)]
    [InlineData("VLRDCLAK5", ShelfItemKind.Playlist)]
    public async Task A_card_is_what_its_page_is(string id, ShelfItemKind kind)
    {
        var (provider, _) = Create(_ => Json(Page("", Carousel("Shelf", null, BrowseCard(id, "A title", "Some • words")))));

        var card = (await provider.GetHomePageAsync()).Shelves[0].Items[0];

        Assert.Equal(kind, card.Kind);
        Assert.Equal("https://img/" + id, card.ThumbnailUrl);
        Assert.Equal("Some • words", card.Subtitle);
    }

    [Fact]
    public async Task A_music_video_card_plays_as_itself_and_a_song_card_does_not()
    {
        var (provider, _) = Create(_ => Json(Page("", Carousel("Shelf", null,
            WatchCard("v1", "Believer", "Imagine Dragons", "3B views", "MUSIC_VIDEO_TYPE_OMV"),
            WatchCard("s1", "Demons", "Imagine Dragons", "2B plays", "MUSIC_VIDEO_TYPE_ATV")))));

        var items = (await provider.GetHomePageAsync()).Shelves[0].Items;

        Assert.Equal(ShelfItemKind.Video, items[0].Kind);
        Assert.Equal("v1", items[0].Song!.VideoVersionId);
        Assert.Equal(3_000_000_000, items[0].Song!.ViewCount);
        Assert.Equal(ShelfItemKind.Song, items[1].Kind);
        Assert.Null(items[1].Song!.VideoVersionId);
    }

    // ---- Explore, charts, moods ----

    [Fact]
    public async Task Explore_finds_its_shelves_by_name()
    {
        var (provider, youTube) = Create(_ => Json(Page("",
            Carousel("New albums & singles", "FEmusic_new_releases_albums", BrowseCard("MPREb_1", "An album", "Album • Someone")),
            Carousel("Trending", "VLOLAK1", ChartRow("UCx", "Someone", "1", "ARROW_DROP_UP", "1K subscribers")),
            Carousel("New music videos", "FEmusic_new_releases_videos", WatchCard("v1", "New", "Someone", "1K views", "MUSIC_VIDEO_TYPE_OMV")))));

        var explore = await provider.GetExplorePageAsync();

        Assert.Equal("FEmusic_new_releases_albums", explore.NewReleases!.MoreBrowseId);
        Assert.Equal("Trending", explore.Trending!.Title);
        Assert.Equal("New music videos", explore.NewVideos!.Title);
        Assert.Contains(youTube.Requests, r => Asks(r, "browseId", "FEmusic_explore"));
    }

    [Fact]
    public async Task Charts_ask_for_the_country_and_read_rank_and_trend()
    {
        var (provider, youTube) = Create(_ => Json(Page("",
            Carousel("Top artists", null,
                ChartRow("UC1", "Шугар", "2", "ARROW_DROP_UP", "211K subscribers"),
                ChartRow("UC2", "Someone", "3", "ARROW_DROP_DOWN", "90K subscribers"),
                ChartRow("UC3", "Steady", "4", "ARROW_CHART_NEUTRAL", "80K subscribers")))));

        var shelf = (await provider.GetChartsAsync("UA"))[0];

        Assert.Contains(youTube.Requests, r => r.Contains("\"formData\":{\"selectedValues\":[\"UA\"]}", StringComparison.Ordinal));
        Assert.Equal(ShelfLayout.Rows, shelf.Layout);
        Assert.Equal(ShelfItemKind.Artist, shelf.Items[0].Kind);
        Assert.Equal(2, shelf.Items[0].Rank);
        Assert.Equal(ChartTrend.Up, shelf.Items[0].Trend);
        Assert.Equal(ChartTrend.Down, shelf.Items[1].Trend);
        Assert.Equal(ChartTrend.Same, shelf.Items[2].Trend);
        Assert.Equal("211K subscribers", shelf.Items[0].Subtitle);
    }

    [Fact]
    public async Task A_browse_without_a_country_sends_no_form_data()
    {
        var (provider, youTube) = Create(_ => Json(Page("")));

        await provider.GetExplorePageAsync();

        Assert.DoesNotContain(youTube.Requests, r => r.Contains("formData", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Moods_come_with_their_colour_and_where_they_lead()
    {
        static string Tile(string name, long color, string @params) =>
            "{\"musicNavigationButtonRenderer\":{\"buttonText\":" + Text(name) + ",\"solid\":{\"leftStripeColor\":" + color + "}," +
            "\"clickCommand\":{\"browseEndpoint\":{\"browseId\":\"FEmusic_moods_and_genres_category\",\"params\":\"" + @params + "\"}}}}";
        var grid = "{\"gridRenderer\":{\"header\":{\"gridHeaderRenderer\":{\"title\":" + Text("Moods & moments") + "}},\"items\":[" +
                   Tile("Chill", 4288988671, "chill") + "," + Tile("Energize", 4294961024, "energize") + "]}}";
        var (provider, _) = Create(_ => Json(Page("", grid)));

        var category = (await provider.GetMoodsAndGenresAsync())[0];

        Assert.Equal("Moods & moments", category.Title);
        Assert.Equal("Chill", category.Tiles[0].Name);
        Assert.Equal(0xFFA4C5FFu, category.Tiles[0].Color);
        Assert.Equal("energize", category.Tiles[1].Params);
    }

    [Fact]
    public async Task A_shelf_page_has_a_title_and_reads_a_grid_too()
    {
        var page = "{\"header\":{\"musicHeaderRenderer\":{\"title\":" + Text("Chill") + "}},\"contents\":" +
                   "{\"singleColumnBrowseResultsRenderer\":{\"tabs\":[{\"tabRenderer\":{\"content\":{\"sectionListRenderer\":{\"contents\":[" +
                   "{\"gridRenderer\":{\"items\":[" + BrowseCard("VLx", "A list", "Playlist • YouTube Music") + "]}}]}}}}]}}}";
        var (provider, youTube) = Create(_ => Json(page));

        var shelves = await provider.GetShelfPageAsync("FEmusic_moods_and_genres_category", "chill");

        Assert.Equal("Chill", shelves.Title);
        Assert.Equal("A list", shelves.Shelves[0].Items[0].Title);
        Assert.Contains(youTube.Requests, r => Asks(r, "params", "chill"));
    }

    // ---- After the last track ----

    private static string CollectionPage(string extra, params string[] sections) =>
        "{\"contents\":{\"twoColumnBrowseResultsRenderer\":{\"tabs\":[{\"tabRenderer\":{\"content\":{\"sectionListRenderer\":{\"contents\":[" +
        "{\"musicResponsiveHeaderRenderer\":{\"title\":" + Text("Evolve") + ",\"straplineTextOne\":{\"runs\":[{\"text\":\"Imagine Dragons\"," +
        "\"navigationEndpoint\":{\"browseEndpoint\":{\"browseId\":\"UCdragons\"}}}]},\"subtitle\":" + Subtitle("Album", "2017") + "}}]}}}}]," +
        "\"secondaryContents\":{\"sectionListRenderer\":{" + extra + "\"contents\":[" + string.Join(",", sections) + "]}}}}}";

    private static string Tracks(params string[] rows) =>
        "{\"musicShelfRenderer\":{\"contents\":[" + string.Join(",", rows) + "]}}";

    [Fact]
    public async Task An_album_offers_what_comes_after_it_without_its_tracks()
    {
        var (provider, _) = Create(_ => Json(CollectionPage("",
            Tracks(SongRow("s1", "Believer", Text(""), Text("7B plays"))),
            Carousel("Other versions", null, BrowseCard("MPREb_d", "Evolve (Deluxe)", "Album • Imagine Dragons")),
            Carousel("Releases for you", null, BrowseCard("MPREb_c", "Parachutes", "Album • Coldplay")))));

        var extras = await provider.GetCollectionExtrasAsync("MPREb_e");

        Assert.Equal(new[] { "Other versions", "Releases for you" }, extras.Shelves.Select(s => s.Title));
        Assert.Equal("UCdragons", extras.Artist!.Id);
        Assert.Equal("Imagine Dragons", extras.Artist.Name);
    }

    [Fact]
    public async Task A_playlists_related_ones_come_as_the_next_part_of_its_page()
    {
        var (provider, youTube) = Create(body => Asks(body, "continuation", "more")
            ? Json(Portion(null, Carousel("Related playlists", null, BrowseCard("VLr", "sushi", "Playlist • Someone"))))
            : Json(CollectionPage(NextToken("more"), "{\"musicPlaylistShelfRenderer\":{\"contents\":[]}}")));

        var extras = await provider.GetCollectionExtrasAsync("VLp");

        Assert.Equal("Related playlists", extras.Shelves.Single().Title);
        Assert.Equal(ShelfItemKind.Playlist, extras.Shelves[0].Items[0].Kind);
    }

    [Theory]
    [InlineData("PLabc", "VLPLabc")]
    [InlineData("RDCLAK5abc", "VLRDCLAK5abc")]
    [InlineData("VLPLabc", "VLPLabc")]
    [InlineData("MPREb_abc", "MPREb_abc")]
    public async Task A_page_is_asked_for_under_the_id_it_answers_to(string id, string asked)
    {
        var (provider, youTube) = Create(_ => Json("{}"));

        await provider.GetCollectionExtrasAsync(id);

        Assert.Contains(youTube.Requests, r => Asks(r, "browseId", asked));
    }

    [Fact]
    public async Task An_album_reads_its_header_from_where_youtube_keeps_it_now()
    {
        var (provider, _) = Create(_ => Json(CollectionPage("", Tracks())));

        var album = await provider.GetAlbumAsync("MPREb_e");

        Assert.Equal("Evolve", album!.Title);
        Assert.Equal("Imagine Dragons", album.ArtistDisplay);
        Assert.Equal("2017", album.Year);
    }

    // ---- What is like a song ----

    private const string NextWithRelated =
        "{\"contents\":{\"singleColumnMusicWatchNextResultsRenderer\":{\"tabbedRenderer\":{\"watchNextTabbedResultsRenderer\":{\"tabs\":[" +
        "{\"tabRenderer\":{\"title\":\"Up next\"}},{\"tabRenderer\":{\"title\":\"Lyrics\",\"endpoint\":{\"browseEndpoint\":{\"browseId\":\"MPLYt_1\"}}}}," +
        "{\"tabRenderer\":{\"title\":\"Comments\"}},{\"tabRenderer\":{\"title\":\"Related\",\"endpoint\":{\"browseEndpoint\":{\"browseId\":\"MPTRt_1\"}}}}]}}}}}";

    [Fact]
    public async Task Related_opens_the_tab_of_that_name()
    {
        var related = "{\"contents\":{\"sectionListRenderer\":{\"contents\":[" +
                      Carousel("You might also like", null, SongRow("s2", "No New Friends", Text("LSD"), Text("Labrinth"))) + "," +
                      Carousel("Similar artists", null, BrowseCard("UCr", "OneRepublic", "11.3M subscribers")) + "," +
                      "{\"musicDescriptionShelfRenderer\":{\"description\":" + Text("A band from Las Vegas.") + "}}]}}}";
        var (provider, youTube) = Create(body => Json(body.Contains("MPTRt_1", StringComparison.Ordinal) ? related : NextWithRelated));

        var answer = await provider.GetSongRelatedAsync("v1");

        Assert.Contains(youTube.Requests, r => Asks(r, "browseId", "MPTRt_1"));
        Assert.Equal(new[] { "You might also like", "Similar artists" }, answer.Shelves.Select(s => s.Title));
        Assert.Equal("A band from Las Vegas.", answer.About);
    }

    [Fact]
    public async Task A_song_without_a_related_tab_has_nothing_related()
    {
        var (provider, _) = Create(_ => Json("{}"));

        Assert.Empty((await provider.GetSongRelatedAsync("v1")).Shelves);
    }

    // ---- The artist ----

    private static string ArtistResponse(string description = "\"subheader\":{\"runs\":[{\"text\":\"23,688,076,706 views\"}]},")
    {
        var header =
            "\"header\":{\"musicImmersiveHeaderRenderer\":{\"title\":" + Text("Imagine Dragons") +
            ",\"monthlyListenerCount\":" + Text("535M monthly audience") +
            ",\"subscriptionButton\":{\"subscribeButtonRenderer\":{\"longSubscriberCountText\":" + Text("33.4M subscribers") + "}}}},";
        var top = "{\"musicShelfRenderer\":{\"title\":" + Text("Top songs") +
                  ",\"bottomEndpoint\":{\"browseEndpoint\":{\"browseId\":\"VLOLAKtop\"}},\"contents\":[" +
                  SongRow("s1", "Believer", Text("Imagine Dragons"), Text("7B plays"), Text("Evolve")) + "]}}";
        return "{" + header + "\"contents\":{\"singleColumnBrowseResultsRenderer\":{\"tabs\":[{\"tabRenderer\":{\"content\":{\"sectionListRenderer\":{\"contents\":[" +
               top + "," +
               Carousel("Albums", "MPADx", BrowseCard("MPREb_a", "LOOM", "2024")) + "," +
               Carousel("Singles & EPs", "MPADx", BrowseCard("MPREb_s", "Monica", "Single • 2025")) + "," +
               Carousel("Videos", "VLOLAKvideos",
                   WatchCard("v1", "Believer", "Imagine Dragons", "3B views", "MUSIC_VIDEO_TYPE_OMV"),
                   WatchCard("v2", "Flute music", "A Stranger", "1K views", "MUSIC_VIDEO_TYPE_OMV")) + "," +
               Carousel("Live performances", "UCx", WatchCard("l1", "Thunder (Live)", "Imagine Dragons", "40M views", "MUSIC_VIDEO_TYPE_OMV")) + "," +
               Carousel("Fans might also like", null, BrowseCard("UCs", "The Score", "14M monthly audience")) + "," +
               "{\"musicDescriptionShelfRenderer\":{" + description + "\"description\":" + Text("A band.") + "}}" +
               "]}}}}]}}}";
    }

    [Fact]
    public async Task An_artist_says_how_big_they_are()
    {
        var (provider, _) = Create(_ => Json(ArtistResponse()));

        var page = await provider.GetArtistPreviewAsync("UCid");

        Assert.Equal("535M monthly audience", page.MonthlyAudienceText);
        Assert.Equal("33.4M subscribers", page.SubscribersText);
        Assert.Equal("23.7B views", page.ViewsText);
        Assert.Equal("VLOLAKtop", page.TopSongsListId);
    }

    [Fact]
    public async Task A_top_song_keeps_its_plays_from_its_own_column()
    {
        var (provider, _) = Create(_ => Json(ArtistResponse()));

        var song = (await provider.GetArtistPreviewAsync("UCid")).Songs[0];

        Assert.Equal(7_000_000_000, song.ViewCount);
        Assert.Equal("Evolve", song.AlbumTitle);
    }

    [Fact]
    public async Task A_row_ending_in_its_plays_has_no_album()
    {
        var (provider, _) = Create(_ => Json(SearchShelf(
            SongRow("s1", "Believer", Subtitle("Imagine Dragons", "3:25"), Text("7B plays")))));

        var song = (await provider.SearchAsync("believer", Domain.Enums.SearchFilter.Songs)).Songs[0];

        Assert.Null(song.AlbumTitle);
        Assert.Equal(7_000_000_000, song.ViewCount);
    }

    [Theory]
    [InlineData("https://imaginedragons.lnk.to/EyesClosed", null)]
    [InlineData("www.imaginedragons.com", null)]
    [InlineData("A band from Las Vegas.", "A band from Las Vegas.")]
    [InlineData("A band from Las Vegas.\\nhttps://imaginedragons.com", "A band from Las Vegas.")]
    [InlineData("Find them at https://imaginedragons.com today", "Find them at https://imaginedragons.com today")]
    public async Task A_bare_link_is_no_story(string written, string? expected)
    {
        var artist = ArtistResponse().Replace("\"description\":" + Text("A band."), "\"description\":" + Text(written), StringComparison.Ordinal);
        var (provider, _) = Create(_ => Json(artist));

        Assert.Equal(expected, (await provider.GetArtistPreviewAsync("UCid")).Description);
    }

    [Fact]
    public async Task An_artist_page_keeps_every_shelf_with_what_youtube_says_about_it()
    {
        var (provider, _) = Create(_ => Json(ArtistResponse()));

        var page = await provider.GetArtistPreviewAsync("UCid");

        Assert.Equal("2024", page.Albums[0].Year);
        Assert.Equal("Single • 2025", page.Singles[0].Subtitle);
        Assert.Equal("2025", page.Singles[0].Year);
        Assert.Equal("14M monthly audience", page.RelatedArtists[0].Subtitle);
        Assert.Equal("l1", page.LivePerformances[0].Id);
        Assert.Equal("l1", page.LivePerformances[0].VideoVersionId);
    }

    [Fact]
    public async Task A_stranger_on_the_videos_shelf_stays_out()
    {
        var (provider, _) = Create(_ => Json(ArtistResponse()));

        var page = await provider.GetArtistPreviewAsync("UCid");

        Assert.Equal(new[] { "v1" }, page.Videos.Select(v => v.Id));
    }

    [Fact]
    public async Task The_preview_costs_one_request_and_the_full_page_reads_more()
    {
        var (preview, previewYouTube) = Create(_ => Json(ArtistResponse()));
        await preview.GetArtistPreviewAsync("UCid");
        Assert.Single(previewYouTube.Requests);

        var (full, fullYouTube) = Create(body => Json(Asks(body, "browseId", "UCid") ? ArtistResponse() : "{}"));
        await full.GetArtistPageAsync("UCid");
        Assert.Contains(fullYouTube.Requests, r => Asks(r, "browseId", "MPADx"));
    }

    // ---- Search ----

    private const string VideosFilter = "EgWKAQIQAW";
    private const string ArtistsFilter = "EgWKAQIgAW";
    private const string AlbumsFilter = "EgWKAQIYAW";

    private static string SearchShelf(params string[] rows) =>
        "{\"contents\":{\"tabbedSearchResultsRenderer\":{\"tabs\":[{\"tabRenderer\":{\"content\":{\"sectionListRenderer\":{\"contents\":[" +
        "{\"musicShelfRenderer\":{\"contents\":[" + string.Join(",", rows) + "]}}]}}}}]}}}";

    private static string BestMatch(string id) =>
        "{\"contents\":{\"tabbedSearchResultsRenderer\":{\"tabs\":[{\"tabRenderer\":{\"content\":{\"sectionListRenderer\":{\"contents\":[" +
        "{\"musicCardShelfRenderer\":{\"title\":" + Text("Imagine Dragons") + ",\"subtitle\":" + Subtitle("Artist", "535M monthly audience") +
        ",\"thumbnail\":" + Thumb("https://img/best") + ",\"onTap\":{\"browseEndpoint\":{\"browseId\":\"" + id + "\"}}," +
        "\"contents\":[" + SongRow("s1", "Believer", Subtitle("Song", "3:25"), Text("7B plays")) + "]}}]}}}}]}}}";

    private static string BrowseRow(string id, string title, string subtitleJson) =>
        "{\"musicResponsiveListItemRenderer\":{\"navigationEndpoint\":{\"browseEndpoint\":{\"browseId\":\"" + id + "\"}}," +
        "\"flexColumns\":[{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":" + Text(title) + "}}," +
        "{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":" + subtitleJson + "}}]}}";

    [Fact]
    public async Task A_search_finds_the_best_match_and_a_shelf_of_every_kind()
    {
        var (provider, _) = Create(body =>
        {
            if (body.Contains(ArtistsFilter, StringComparison.Ordinal))
                return Json(SearchShelf(BrowseRow("UCa", "Imagine Dragons", Text("535M monthly audience"))));
            if (body.Contains(AlbumsFilter, StringComparison.Ordinal))
                return Json(SearchShelf(BrowseRow("MPREb_1", "LOOM", Subtitle("Album", "Imagine Dragons", "2024"))));
            if (body.Contains(VideosFilter, StringComparison.Ordinal))
                return Json(SearchShelf(SongRow("v1", "Believer", Subtitle("Imagine Dragons", "3B views", "3:37"))));
            if (body.Contains("\"params\":\"Eg", StringComparison.Ordinal))
                return Json(SearchShelf(SongRow("s1", "Believer", Subtitle("Imagine Dragons", "Evolve", "3:25"))));
            return Json(BestMatch("UCa"));
        });

        var found = await provider.SearchOverviewAsync("imagine dragons");

        Assert.Equal(ShelfItemKind.Artist, found.TopResult!.Kind);
        Assert.Equal("UCa", found.TopResult.Id);
        Assert.Equal("Artist • 535M monthly audience", found.TopResult.Subtitle);
        Assert.Equal("Believer", found.TopResultSongs[0].Title);
        Assert.Equal(ShelfLayout.Rows, found.Songs[0].Layout);
        Assert.Equal(ShelfItemKind.Video, found.Videos[0].Items[0].Kind);
        Assert.Equal("v1", found.Videos[0].Items[0].Song!.VideoVersionId);
        Assert.Equal("535M monthly audience", found.Artists[0].Items[0].Subtitle);
        Assert.Equal("Album • Imagine Dragons • 2024", found.Albums[0].Items[0].Subtitle);
    }

    [Fact]
    public async Task An_album_row_names_the_artist_not_its_type()
    {
        var (provider, _) = Create(_ => Json(SearchShelf(
            "{\"musicResponsiveListItemRenderer\":{\"navigationEndpoint\":{\"browseEndpoint\":{\"browseId\":\"MPREb_1\"}}," +
            "\"flexColumns\":[{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":" + Text("LOOM") + "}}," +
            "{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":{\"runs\":[{\"text\":\"Album\"},{\"text\":\" • \"}," +
            "{\"text\":\"Imagine Dragons\",\"navigationEndpoint\":{\"browseEndpoint\":{\"browseId\":\"UCa\"}}},{\"text\":\" • \"},{\"text\":\"2024\"}]}}}]}}")));

        var album = (await provider.SearchAsync("loom", Domain.Enums.SearchFilter.Albums)).Albums[0];

        Assert.Equal("Imagine Dragons", album.ArtistDisplay);
        Assert.Equal("2024", album.Year);
    }

    [Fact]
    public async Task A_search_where_one_kind_fails_still_shows_the_others()
    {
        var (provider, _) = Create(body => body.Contains(VideosFilter, StringComparison.Ordinal)
            ? Failure()
            : Json(body.Contains(ArtistsFilter, StringComparison.Ordinal)
                ? SearchShelf(BrowseRow("UCa", "Imagine Dragons", Text("535M monthly audience")))
                : "{}"));

        var found = await provider.SearchOverviewAsync("imagine dragons");

        Assert.Empty(found.Videos);
        Assert.NotEmpty(found.Artists);
    }

    [Fact]
    public async Task A_search_where_everything_fails_is_an_error_not_an_empty_answer()
    {
        var (provider, _) = Create(_ => Failure());

        await Assert.ThrowsAnyAsync<Exception>(() => provider.SearchOverviewAsync("imagine dragons"));
    }

    [Fact]
    public async Task Suggestions_are_searches_to_run_and_things_to_open()
    {
        var answer = "{\"contents\":[{\"searchSuggestionsSectionRenderer\":{\"contents\":[" +
                     "{\"searchSuggestionRenderer\":{\"suggestion\":" + Text("imagine dragons") + "}}]}}," +
                     "{\"searchSuggestionsSectionRenderer\":{\"contents\":[" +
                     BrowseRow("UCa", "Imagine Dragons", Text("535M monthly audience")) + "," +
                     SongRow("s1", "Believer", Subtitle("Song", "Imagine Dragons", "7B plays"), Text("Evolve")) + "]}}]}";
        var (provider, _) = Create(_ => Json(answer));

        var found = await provider.GetSuggestionsAsync("imagine");

        Assert.Equal(new[] { "imagine dragons" }, found.Texts);
        Assert.Equal(ShelfItemKind.Artist, found.Items[0].Kind);
        Assert.Equal(ShelfItemKind.Song, found.Items[1].Kind);
        Assert.Equal("Imagine Dragons", found.Items[1].Song!.ArtistDisplay);
    }
}
