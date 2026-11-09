using System.Net;
using System.Text;
using SoundLibert.Domain.Exceptions;
using SoundLibert.Infra;
using SoundLibert.Infra.InnerTube;
using SoundLibert.Infra.InnerTube.Models;

namespace SoundLibert.Domain.Tests;

/// <summary>
/// The account library is the source of truth for the sync: whatever its result
/// does not list is removed locally. So a partial or misread answer must fail,
/// and a failed request must not end the session or wipe anything.
/// </summary>
public class AccountLibraryTests
{
    private const string Session = "LOGIN_INFO=x; SAPISID=y";

    private sealed class FakeYouTube(Func<string, bool, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Body, bool HasCookie, bool HasAuthorization)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            var hasCookie = request.Headers.Contains("Cookie");
            Requests.Add((body, hasCookie, request.Headers.Contains("Authorization")));
            return respond(body, hasCookie);
        }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new StringContent("{}") };

    private static (InnerTubeMusicProvider Provider, InnerTubeClient Client, FakeYouTube YouTube) Create(
        Func<string, bool, HttpResponseMessage> respond)
    {
        var youTube = new FakeYouTube(respond);
        var client = new InnerTubeClient(new HttpClient(youTube)) { Cookie = Session };
        return (new InnerTubeMusicProvider(client), client, youTube);
    }

    /// <summary>Answers the first request with the section and each continuation by its token.</summary>
    private static Func<string, bool, HttpResponseMessage> Pages(string first, params (string Token, string Page)[] next) =>
        (body, _) =>
        {
            foreach (var (token, page) in next)
                if (body.Contains($"\"continuation\":\"{token}\"", StringComparison.Ordinal)) return Json(page);
            return Json(first);
        };

    private static string Card(string id, string title, string? subtitleJson = null) =>
        "{\"musicTwoRowItemRenderer\":{\"title\":{\"runs\":[{\"text\":\"" + title + "\"}]}," +
        (subtitleJson == null ? "" : "\"subtitle\":{\"runs\":[" + subtitleJson + "]},") +
        "\"navigationEndpoint\":{\"browseEndpoint\":{\"browseId\":\"" + id + "\"}}}}";

    private static string SongRow(string id, string title) =>
        "{\"musicResponsiveListItemRenderer\":{\"playlistItemData\":{\"videoId\":\"" + id + "\"},\"flexColumns\":[" +
        "{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":{\"runs\":[{\"text\":\"" + title + "\"}]}}}," +
        "{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":{\"runs\":[{\"text\":\"Artist\"}]}}}]}}";

    private static string Token(string token) =>
        "\"continuations\":[{\"nextContinuationData\":{\"continuation\":\"" + token + "\"}}]";

    private static string Section(string section) =>
        "{\"contents\":{\"singleColumnBrowseResultsRenderer\":{\"tabs\":[{\"tabRenderer\":{\"content\":" +
        "{\"sectionListRenderer\":{\"contents\":[" + section + "]}}}}]}}}";

    private static string Grid(string? token, params string[] cards) =>
        "{\"gridRenderer\":{\"items\":[" + string.Join(",", cards) + "]" + (token == null ? "" : "," + Token(token)) + "}}";

    private static string GridPage(string? token, params string[] cards) =>
        "{\"continuationContents\":{\"gridContinuation\":{\"items\":[" + string.Join(",", cards) + "]" +
        (token == null ? "" : "," + Token(token)) + "}}}";

    private static string Shelf(string? token, params string[] rows) =>
        "{\"musicShelfRenderer\":{\"contents\":[" + string.Join(",", rows) + "]" + (token == null ? "" : "," + Token(token)) + "}}";

    private static string ShelfPage(string? token, params string[] rows) =>
        "{\"continuationContents\":{\"musicShelfContinuation\":{\"contents\":[" + string.Join(",", rows) + "]" +
        (token == null ? "" : "," + Token(token)) + "}}}";

    private const string SignedOut =
        "{\"responseContext\":{\"serviceTrackingParams\":[{\"params\":[{\"key\":\"logged_in\",\"value\":\"0\"}]}]}}";

    [Fact]
    public async Task Playlists_AreReadAcrossEveryPageShape()
    {
        var appended = "{\"onResponseReceivedActions\":[{\"appendContinuationItemsAction\":{\"continuationItems\":[" +
                       Card("VLPL3", "Three") +
                       ",{\"continuationItemRenderer\":{\"continuationEndpoint\":{\"continuationCommand\":{\"token\":\"p3\"}}}}]}}]}";
        var (provider, _, _) = Create(Pages(
            Section(Grid("p1", Card("VLPL1", "One"))),
            ("p1", GridPage("p2", Card("VLPL2", "Two"))),
            ("p2", appended),
            ("p3", GridPage(null, Card("VLPL4", "Four")))));

        var playlists = await provider.GetAccountPlaylistsAsync();

        Assert.Equal(new[] { "VLPL1", "VLPL2", "VLPL3", "VLPL4" }, playlists.Select(p => p.Id));
    }

    [Fact]
    public async Task LikedSongs_AreReadAcrossPages_AndDeduplicated()
    {
        var (provider, _, _) = Create(Pages(
            Section(Shelf("p1", SongRow("a", "A"), SongRow("b", "B"))),
            ("p1", ShelfPage(null, SongRow("b", "B"), SongRow("c", "C")))));

        var songs = await provider.GetAccountLikedSongsAsync();

        Assert.Equal(new[] { "a", "b", "c" }, songs.Select(s => s.Id));
    }

    [Fact]
    public async Task NextPageToken_InTheNewerShape_IsFollowed()
    {
        var first = Section("{\"gridRenderer\":{\"items\":[" + Card("VLPL1", "One") +
                            ",{\"continuationItemRenderer\":{\"continuationEndpoint\":{\"continuationCommand\":{\"token\":\"p1\"}}}}]}}");
        var (provider, _, _) = Create(Pages(first, ("p1", GridPage(null, Card("VLPL2", "Two")))));

        Assert.Equal(2, (await provider.GetAccountPlaylistsAsync()).Count);
    }

    [Fact]
    public async Task ListWrappedInAnItemSection_IsFound()
    {
        var (provider, _, _) = Create(Pages(
            Section("{\"itemSectionRenderer\":{\"contents\":[" + Grid(null, Card("VLPL1", "One")) + "]}}")));

        Assert.Single(await provider.GetAccountPlaylistsAsync());
    }

    [Fact]
    public async Task ALaterPageFailing_FailsTheWholeRead()
    {
        var (provider, _, _) = Create((body, _) => body.Contains("\"continuation\":\"p1\"", StringComparison.Ordinal)
            ? Status(HttpStatusCode.InternalServerError)
            : Json(Section(Grid("p1", Card("VLPL1", "One")))));

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetAccountPlaylistsAsync());
    }

    [Fact]
    public async Task ALaterPageNotUnderstood_FailsTheWholeRead()
    {
        var (provider, _, _) = Create(Pages(Section(Grid("p1", Card("VLPL1", "One"))), ("p1", "{}")));

        await Assert.ThrowsAsync<AccountLibraryUnrecognizedException>(() => provider.GetAccountPlaylistsAsync());
    }

    [Fact]
    public async Task AnAnswerWithNoList_IsNotAnEmptyLibrary()
    {
        var (provider, _, _) = Create(Pages("{}"));

        await Assert.ThrowsAsync<AccountLibraryUnrecognizedException>(() => provider.GetAccountAlbumsAsync());
        await Assert.ThrowsAsync<AccountLibraryUnrecognizedException>(() => provider.GetAccountArtistsAsync());
        await Assert.ThrowsAsync<AccountLibraryUnrecognizedException>(() => provider.GetAccountLikedSongsAsync());
    }

    [Fact]
    public async Task AnEmptyListThatIsThere_IsAnEmptyLibrary()
    {
        var (provider, _, _) = Create(Pages(Section(Grid(null))));

        Assert.Empty(await provider.GetAccountPlaylistsAsync());
    }

    [Fact]
    public async Task ATokenThatRepeats_DoesNotLoopForever()
    {
        var (provider, _, _) = Create(Pages(
            Section(Grid("p1", Card("VLPL1", "One"))),
            ("p1", GridPage("p1", Card("VLPL2", "Two")))));

        await Assert.ThrowsAsync<AccountLibraryUnrecognizedException>(() => provider.GetAccountPlaylistsAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASignedOutAnswer_OnAnyPage_IsReportedAsSuch(bool onLaterPage)
    {
        var (provider, _, _) = onLaterPage
            ? Create(Pages(Section(Grid("p1", Card("VLPL1", "One"))), ("p1", SignedOut)))
            : Create(Pages(SignedOut));

        await Assert.ThrowsAsync<AccountSessionInvalidException>(() => provider.GetAccountPlaylistsAsync());
    }

    [Fact]
    public async Task OnlyYouTubesOwnLists_AreLeftOut()
    {
        var (provider, _, _) = Create(Pages(Section(Grid(null,
            Card("VLLM", "Liked Music"),
            Card("VLSE", "Episodes for later"),
            Card("FEmusic_library_privately_owned_tracks", "Uploads"),
            Card("VLPL1", "Liked Songs"),
            Card("VLPL2", "New playlist")))));

        var playlists = await provider.GetAccountPlaylistsAsync();

        Assert.Equal(new[] { "VLPL1", "VLPL2" }, playlists.Select(p => p.Id));
    }

    [Fact]
    public async Task SavedAlbum_TakesItsArtistFromTheArtistSegment()
    {
        var subtitle = "{\"text\":\"Album\"},{\"text\":\" • \"}," +
                       "{\"text\":\"Gorillaz\",\"navigationEndpoint\":{\"browseEndpoint\":{\"browseId\":\"UC1\"}}}," +
                       "{\"text\":\" • \"},{\"text\":\"2001\"}";
        var (provider, _, _) = Create(Pages(Section(Grid(null, Card("MPREb1", "Gorillaz", subtitle)))));

        var album = Assert.Single(await provider.GetAccountAlbumsAsync());

        Assert.Equal("Gorillaz", Assert.Single(album.Artists).Name);
    }

    // ---- session handling ----

    [Fact]
    public async Task ServerError_OnALibraryPage_DoesNotEndTheSession()
    {
        var (provider, client, youTube) = Create((_, _) => Status(HttpStatusCode.InternalServerError));
        var rejected = 0;
        client.AuthenticationFailed += (_, _) => rejected++;

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetAccountLikedSongsAsync());

        Assert.Equal(0, rejected);
        // No anonymous retry: its "sign in" answer could pass for an empty library.
        Assert.Single(youTube.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Rejection_OnALibraryPage_IsReportedOnce_AndNotRetried(HttpStatusCode code)
    {
        var (provider, client, youTube) = Create((_, _) => Status(code));
        var rejected = 0;
        client.AuthenticationFailed += (_, _) => rejected++;

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetAccountPlaylistsAsync());

        Assert.Equal(1, rejected);
        Assert.Single(youTube.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 1)]
    [InlineData(HttpStatusCode.InternalServerError, 0)]
    public async Task PublicPage_LoadsWithoutTheSession_WhenItFails(HttpStatusCode code, int expectedRejections)
    {
        var page = "{\"contents\":{\"sectionListRenderer\":{\"contents\":[]}}}";
        var (provider, client, youTube) = Create((_, hasCookie) => hasCookie ? Status(code) : Json(page));
        var rejected = 0;
        client.AuthenticationFailed += (_, _) => rejected++;

        var page2 = await provider.GetArtistPageAsync("UC1");

        Assert.NotNull(page2);
        Assert.Equal(expectedRejections, rejected);
        Assert.Equal(2, youTube.Requests.Count);
        Assert.True(youTube.Requests[0].HasCookie);
        Assert.False(youTube.Requests[1].HasCookie);
        Assert.False(youTube.Requests[1].HasAuthorization);
    }

    [Fact]
    public async Task WithoutASession_NothingIsRetried()
    {
        var youTube = new FakeYouTube((_, _) => Status(HttpStatusCode.InternalServerError));
        var provider = new InnerTubeMusicProvider(new InnerTubeClient(new HttpClient(youTube)));

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetArtistPageAsync("UC1"));

        Assert.Single(youTube.Requests);
    }

    // ---- reading other pages ----

    [Fact]
    public async Task VideoRow_ViewCountIsNotAnAlbum()
    {
        var row = "{\"musicResponsiveListItemRenderer\":{\"playlistItemData\":{\"videoId\":\"v\"},\"flexColumns\":[" +
                  "{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":{\"runs\":[{\"text\":\"Song\"}]}}}," +
                  "{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":{\"runs\":[" +
                  "{\"text\":\"Artist\"},{\"text\":\" • \"},{\"text\":\"209M views\"},{\"text\":\" • \"},{\"text\":\"3:43\"}]}}}]}}";
        var search = "{\"contents\":{\"tabbedSearchResultsRenderer\":{\"tabs\":[{\"tabRenderer\":{\"content\":" +
                     "{\"sectionListRenderer\":{\"contents\":[{\"musicShelfRenderer\":{\"contents\":[" + row + "]}}]}}}}]}}}";
        var (provider, _, _) = Create((_, _) => Json(search));

        var result = await provider.SearchAsync("song", Domain.Enums.SearchFilter.Videos);

        var song = Assert.Single(result.Songs);
        Assert.Null(song.AlbumTitle);
        Assert.Equal(209_000_000, song.ViewCount);
    }

    [Fact]
    public async Task PlaylistInTheSingleColumnLayout_IsRead()
    {
        var page = Section("{\"musicPlaylistShelfRenderer\":{\"contents\":[" + SongRow("a", "A") + "]}}");
        var (provider, _, _) = Create((_, _) => Json(page));

        Assert.Single(await provider.GetPlaylistSongsAsync("PL1"));
    }

    [Fact]
    public async Task ArtistPage_ExpandsTheAlbumsShelf_ButNotTheVideosShelf()
    {
        static string Carousel(string title, string moreId, string card) =>
            "{\"musicCarouselShelfRenderer\":{\"header\":{\"musicCarouselShelfBasicHeaderRenderer\":{\"title\":{\"runs\":[{\"text\":\"" + title + "\"}]}," +
            "\"moreContentButton\":{\"buttonRenderer\":{\"navigationEndpoint\":{\"browseEndpoint\":{\"browseId\":\"" + moreId + "\"}}}}}},\"contents\":[" + card + "]}}";

        var artist = "{\"contents\":{\"sectionListRenderer\":{\"contents\":[" +
                     Carousel("Videos", "MORE_VIDEOS", Card("MPREb0", "Not expanded")) + "," +
                     Carousel("Albums", "MORE_ALBUMS", Card("MPREb1", "One")) + "]}}}";
        var moreAlbums = Section(Grid("p1", Card("MPREb2", "Two")));
        var (provider, _, youTube) = Create((body, _) =>
            body.Contains("\"continuation\":\"p1\"", StringComparison.Ordinal) ? Json(GridPage(null, Card("MPREb3", "Three")))
            : body.Contains("MORE_ALBUMS", StringComparison.Ordinal) ? Json(moreAlbums)
            : Json(artist));

        var page = await provider.GetArtistPageAsync("UC1");

        Assert.Contains(page.Albums, a => a.Id == "MPREb2");
        Assert.Contains(page.Albums, a => a.Id == "MPREb3");
        Assert.DoesNotContain(youTube.Requests, r => r.Body.Contains("MORE_VIDEOS", StringComparison.Ordinal));
    }
}
