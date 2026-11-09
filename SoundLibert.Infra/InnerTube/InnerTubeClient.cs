using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using SoundLibert.Infra.InnerTube.Models;

namespace SoundLibert.Infra.InnerTube;

public class InnerTubeClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private string? _signatureTimestampJsUrl;
    private int? _signatureTimestamp;

    private static readonly JsonSerializerOptions ResponseJson = new() { PropertyNameCaseInsensitive = true };

    public string? VisitorData { get; set; }
    public string? Cookie { get; set; }
    public string? DataSyncId { get; set; }

    /// <summary>
    /// Raised when YouTube rejects the stored session outright (HTTP 401/403).
    /// A server error (5xx) says nothing about the session and does not raise it.
    /// </summary>
    public event EventHandler? AuthenticationFailed;

    public InnerTubeClient()
    {
        // Bounded: the default 100s per request made the UI look frozen
        // when the network stalled (14 clients x 100s worst case).
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttpClient = true;
    }

    public InnerTubeClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Bootstraps anonymous auth state: fetches real visitorData from sw.js_data
    /// (port of Velune's YouTube.visitorData()). Must be called before player
    /// requests, otherwise YouTube rejects them or withholds stream URLs.
    /// Result is cached for the lifetime of the client.
    /// </summary>
    public async Task<string?> EnsureVisitorDataAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(VisitorData)) return VisitorData;

        await _authLock.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(VisitorData)) return VisitorData;

            using var response = await _httpClient.GetAsync(
                "https://music.youtube.com/sw.js_data", ct);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(ct);
            // Body is prefixed with ")]}'\n" (Velune strips first 5 chars).
            var json = text.Length > 5 ? text.Substring(5) : text;
            using var doc = JsonDocument.Parse(json);
            var found = FindVisitorData(doc.RootElement);
            if (found != null)
            {
                VisitorData = found;
                System.Diagnostics.Debug.WriteLine($"[InnerTube] visitorData bootstrapped (len={found.Length})");
            }
            return VisitorData;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[InnerTube] visitorData fetch failed: {ex.Message}");
            return null;
        }
        finally
        {
            _authLock.Release();
        }
    }

    private static string? FindVisitorData(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var s = element.GetString();
            if (s != null && (s.StartsWith("Cgt", StringComparison.Ordinal) || s.StartsWith("Cgs", StringComparison.Ordinal)))
                return s;
            return null;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                var found = FindVisitorData(child);
                if (found != null) return found;
            }
        }
        return null;
    }

    /// <summary>
    /// Fetches the player signature timestamp (port of NewPipe's
    /// YoutubeJavaScriptPlayerManager.getSignatureTimestamp): watch page HTML
    /// contains the player base.js URL, which embeds "signatureTimestamp:NNNNN".
    /// Cached per base.js URL. Returns null on failure (callers then omit
    /// playbackContext, same as Velune's getSignatureTimestampOrNull).
    /// </summary>
    public async Task<int?> GetSignatureTimestampAsync(string videoId, CancellationToken ct = default)
    {
        try
        {
            using var watchRequest = new HttpRequestMessage(
                HttpMethod.Get, $"https://www.youtube.com/watch?v={videoId}&hl=en");
            watchRequest.Headers.UserAgent.ParseAdd(YouTubeClient.WEB.UserAgent);
            using var watchResponse = await _httpClient.SendAsync(watchRequest, ct);
            watchResponse.EnsureSuccessStatusCode();
            var watchHtml = await watchResponse.Content.ReadAsStringAsync(ct);

            var jsMatch = Regex.Match(watchHtml, "\"jsUrl\"\\s*:\\s*\"([^\"]+base\\.js)\"");
            if (!jsMatch.Success)
                jsMatch = Regex.Match(watchHtml, "PLAYER_JS_URL\"\\s*:\\s*\"([^\"]+)\"");
            if (!jsMatch.Success) return null;

            var jsUrl = jsMatch.Groups[1].Value.Replace("\\/", "/");
            if (jsUrl.StartsWith("/")) jsUrl = "https://www.youtube.com" + jsUrl;

            if (_signatureTimestamp.HasValue && _signatureTimestampJsUrl == jsUrl)
                return _signatureTimestamp;

            using var jsResponse = await _httpClient.GetAsync(jsUrl, ct);
            jsResponse.EnsureSuccessStatusCode();
            var js = await jsResponse.Content.ReadAsStringAsync(ct);
            var stsMatch = Regex.Match(js, "signatureTimestamp\\s*:\\s*(\\d+)");
            if (!stsMatch.Success) return null;

            _signatureTimestampJsUrl = jsUrl;
            _signatureTimestamp = int.Parse(stsMatch.Groups[1].Value);
            System.Diagnostics.Debug.WriteLine($"[InnerTube] signatureTimestamp={_signatureTimestamp}");
            return _signatureTimestamp;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[InnerTube] signatureTimestamp fetch failed: {ex.Message}");
            return null;
        }
    }

    public async Task<SearchResponse> SearchAsync(
        YouTubeClient client,
        string? query = null,
        string? filter = null,
        string? continuation = null,
        CancellationToken ct = default)
    {
        var body = new SearchBody
        {
            Context = BuildContext(client),
            Query = query,
            Params = filter
        };

        return await PostAsync<SearchResponse>("search", client, body, continuation, ct);
    }

    /// <param name="accountOnly">
    /// The page only exists for the signed-in user (the account's library). A
    /// failure then surfaces as an exception: asked anonymously, YouTube answers
    /// with a sign-in prompt or an empty list, and reading that as "the library
    /// is empty" would remove the user's mirrored library.
    /// </param>
    public async Task<BrowseResponse> BrowseAsync(
        YouTubeClient client,
        string? browseId = null,
        string? @params = null,
        string? continuation = null,
        bool setLogin = false,
        bool accountOnly = false,
        string? country = null,
        CancellationToken ct = default)
    {
        var body = new BrowseBody
        {
            Context = BuildContext(client, setLogin),
            BrowseId = browseId,
            Params = @params,
            Continuation = continuation,
            FormData = country == null ? null : new BrowseFormData { SelectedValues = new List<string> { country } }
        };

        try
        {
            return await PostAsync<BrowseResponse>("browse", client, body, null, ct);
        }
        catch (HttpRequestException ex) when (HasSession && IsSessionRelevant(ex))
        {
            // Only an explicit rejection says anything about the session. YouTube
            // also answers 500 for reasons of its own, and treating that as an
            // expiry ended the sign-in and emptied the mirrored library.
            if (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                System.Diagnostics.Trace.WriteLine($"[InnerTube] YouTube rejected the session ({(int)ex.StatusCode}).");
                AuthenticationFailed?.Invoke(this, EventArgs.Empty);
            }

            if (accountOnly) throw;

            // Public pages (playlists, albums, artists) still load without the session.
            System.Diagnostics.Debug.WriteLine(
                $"[InnerTube] Authenticated browse failed ({ex.StatusCode}); retrying anonymously.");
            return await PostAsync<BrowseResponse>("browse", client, body, null, ct, anonymous: true);
        }
    }

    private static bool IsSessionRelevant(HttpRequestException ex)
        => ex.StatusCode is System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.Forbidden
            or System.Net.HttpStatusCode.InternalServerError;

    public async Task<PlayerResponse> PlayerAsync(
        YouTubeClient client,
        string videoId,
        string? playlistId = null,
        int? signatureTimestamp = null,
        string? poToken = null,
        CancellationToken ct = default)
    {
        var needsServiceIntegrity = StreamClientUtils.IsWebClient(client.ClientName);
        var effectivePoToken = poToken;
        if (needsServiceIntegrity && string.IsNullOrEmpty(effectivePoToken))
        {
            effectivePoToken = PoTokenGenerator.GenerateContentToken(
                VisitorData ?? "visitor_data",
                videoId
            );
        }

        var context = BuildContext(client);
        if (client.IsEmbedded)
        {
            // Port of Velune InnerTube.player(): embedded clients must send
            // thirdParty.embedUrl, otherwise YouTube answers ERROR
            // ("no longer supported in this application or device").
            context.ThirdParty = new ContextThirdParty
            {
                EmbedUrl = $"https://www.youtube.com/watch?v={videoId}"
            };
        }

        var body = new PlayerBody
        {
            Context = context,
            VideoId = videoId,
            PlaylistId = playlistId,
            PlaybackContext = client.UseSignatureTimestamp && signatureTimestamp.HasValue
                ? new PlaybackContextBody
                {
                    ContentPlaybackContext = new ContentPlaybackContext
                    {
                        SignatureTimestamp = signatureTimestamp.Value
                    }
                }
                : null,
            ServiceIntegrityDimensions = !string.IsNullOrEmpty(effectivePoToken)
                ? new ServiceIntegrityDimensions { PoToken = effectivePoToken }
                : null,
            ContentCheckOk = true,
            RacyCheckOk = true
        };

        return await PostAsync<PlayerResponse>("player", client, body, null, ct);
    }

    public async Task<GetSearchSuggestionsResponse> GetSearchSuggestionsAsync(
        YouTubeClient client,
        string input,
        CancellationToken ct = default)
    {
        var body = new GetSearchSuggestionsBody
        {
            Context = BuildContext(client),
            Input = input
        };

        return await PostAsync<GetSearchSuggestionsResponse>("music/get_search_suggestions", client, body, null, ct);
    }

    /// <summary>
    /// Calls the InnerTube `next` endpoint. With only a videoId it returns the
    /// YouTube Music radio / "up next" queue (port of Velune InnerTube.next).
    /// </summary>
    public async Task<NextResponse> NextAsync(
        YouTubeClient client,
        string? videoId,
        string? playlistId = null,
        string? playlistSetVideoId = null,
        int? index = null,
        string? @params = null,
        string? continuation = null,
        bool? enablePersistentPlaylistPanel = null,
        bool? isAudioOnly = null,
        string? tunerSettingValue = null,
        WatchEndpointMusicSupportedConfigs? watchEndpointMusicSupportedConfigs = null,
        CancellationToken ct = default)
    {
        var body = new NextBody
        {
            Context = BuildContext(client),
            VideoId = videoId,
            PlaylistId = playlistId,
            PlaylistSetVideoId = playlistSetVideoId,
            Index = index,
            Params = @params,
            Continuation = continuation,
            EnablePersistentPlaylistPanel = enablePersistentPlaylistPanel,
            IsAudioOnly = isAudioOnly,
            TunerSettingValue = tunerSettingValue,
            WatchEndpointMusicSupportedConfigs = watchEndpointMusicSupportedConfigs
        };

        return await PostAsync<NextResponse>("next", client, body, null, ct);
    }

    // ---------------------------------------------------------- account operations

    /// <summary>
    /// Fetches the signed-in account header (name, email, handle, avatar) via
    /// <c>account/account_menu</c> (port of Velune's YouTube.accountInfo).
    /// </summary>
    public async Task<AccountMenuResponse> AccountMenuAsync(YouTubeClient client, CancellationToken ct = default)
    {
        var body = new AccountMenuBody { Context = BuildContext(client, setLogin: true) };
        return await PostAsync<AccountMenuResponse>("account/account_menu", client, body, null, ct);
    }

    /// <summary>
    /// Resolves a queue of songs for the given video ids or playlist id
    /// (<c>music/get_queue</c>, port of Velune's InnerTube.getQueue).
    /// </summary>
    public async Task<GetQueueResponse> GetQueueAsync(
        YouTubeClient client, IReadOnlyList<string>? videoIds = null, string? playlistId = null, CancellationToken ct = default)
    {
        var body = new GetQueueBody
        {
            Context = BuildContext(client),
            VideoIds = videoIds is { Count: > 0 } ? videoIds.ToList() : null,
            PlaylistId = string.IsNullOrWhiteSpace(playlistId) ? null : playlistId
        };
        return await PostAsync<GetQueueResponse>("music/get_queue", client, body, null, ct);
    }

    private Context BuildContext(YouTubeClient client, bool setLogin = false)
    {
        // NOTE: we deliberately do NOT set context.user.onBehalfOfUser. Sending
        // the DATASYNC_ID there makes authenticated browse (the account library)
        // return HTTP 500; with the SAPISIDHASH Authorization header alone the
        // same request returns 200 and the full liked-songs shelf. (Verified
        // directly against music.youtube.com.)
        _ = setLogin;
        return new Context
        {
            Client = new ContextClient
            {
                ClientName = client.ClientName,
                ClientVersion = client.ClientVersion,
                Gl = "US",
                Hl = "en",
                VisitorData = VisitorData,
                OsName = client.OsName,
                OsVersion = client.OsVersion,
                DeviceMake = client.DeviceMake,
                DeviceModel = client.DeviceModel,
                AndroidSdkVersion = client.AndroidSdkVersion
            }
        };
    }

    private async Task<T> PostAsync<T>(
        string endpoint,
        YouTubeClient client,
        object body,
        string? continuation = null,
        CancellationToken ct = default,
        bool anonymous = false)
    {
        var url = endpoint.StartsWith("http")
            ? endpoint
            : YouTubeClient.ApiUrlYouTubeMusic + endpoint + "?prettyPrint=false";

        if (!string.IsNullOrEmpty(continuation))
        {
            url += $"&continuation={Uri.EscapeDataString(continuation)}&ctoken={Uri.EscapeDataString(continuation)}";
        }

        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content
        };

        AddHeaders(request, client, anonymous);

        using var response = await _httpClient.SendAsync(request, ct);
        await ThrowIfFailedAsync(response, ct);

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(responseJson, ResponseJson)!;
    }

    /// <summary>Logs the response body on failure (helps diagnose YouTube API 4xx/5xx).</summary>
    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        if (body.Length > 1200) body = body[..1200];
        System.Diagnostics.Debug.WriteLine(
            $"[InnerTube] HTTP {(int)response.StatusCode} {response.ReasonPhrase} -> {body}");
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// A real signed-in session. Anonymous music.youtube.com also sets a SAPISID
    /// cookie, so the LOGIN_INFO marker is what counts; sending anything less
    /// makes authenticated requests fail with 500.
    /// </summary>
    private bool HasSession =>
        !string.IsNullOrEmpty(Cookie) && Cookie.Contains("LOGIN_INFO", StringComparison.Ordinal);

    private void AddHeaders(HttpRequestMessage request, YouTubeClient client, bool anonymous = false)
    {
        request.Headers.Add("X-Goog-Api-Format-Version", "1");
        request.Headers.Add("X-YouTube-Client-Name", client.ClientId);
        request.Headers.Add("X-YouTube-Client-Version", client.ClientVersion);
        request.Headers.Add("X-Origin", YouTubeClient.OriginYouTubeMusic);
        request.Headers.Add("Referer", YouTubeClient.RefererYouTubeMusic);
        request.Headers.UserAgent.ParseAdd(client.UserAgent);

        if (!string.IsNullOrEmpty(VisitorData))
        {
            request.Headers.Add("X-Goog-Visitor-Id", VisitorData);
        }

        if (!anonymous && HasSession)
        {
            request.Headers.Add("Cookie", Cookie);

            // Authenticated (YouTube Music) clients also require the SAPISIDHASH
            // Authorization header; sending only the raw cookie yields 401.
            // Port of Velune InnerTube: sha1("<time> <SAPISID> <origin>").
            if (client.LoginSupported)
            {
                var sapisid = ExtractSapisid(Cookie!);
                if (!string.IsNullOrEmpty(sapisid))
                {
                    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var hash = Sha1Hex($"{timestamp} {sapisid} {YouTubeClient.OriginYouTubeMusic}");
                    request.Headers.Add("Authorization", $"SAPISIDHASH {timestamp}_{hash}");
                }
            }
        }
    }

    private static string? ExtractSapisid(string cookie)
    {
        foreach (var part in cookie.Split(';'))
        {
            var entry = part.Trim();
            var eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            if (entry[..eq].Trim().Equals("SAPISID", StringComparison.OrdinalIgnoreCase))
                return entry[(eq + 1)..];
        }
        return null;
    }

    private static string Sha1Hex(string value)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public void Dispose()
    {
        // The injected HttpClient belongs to its creator (DI/test host) —
        // disposing it here would kill all networking for the whole app.
        if (_ownsHttpClient)
        {
            try { _httpClient.Dispose(); } catch { }
        }
    }
}
