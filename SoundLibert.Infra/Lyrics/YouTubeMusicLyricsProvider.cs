using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoundLibert.Infra.InnerTube.Models;

namespace SoundLibert.Infra.Lyrics;

/// <summary>
/// YouTube Music's own lyrics (LyricFind / Musixmatch), read anonymously the
/// way the Android app does: <c>next</c> → the lyrics tab's <c>MPLY…</c>
/// browse id → <c>browse</c>. The mobile client returns
/// <c>timedLyricsData</c> keyed to THIS video id, so the timing matches the
/// recording that is actually playing (no duration guessing like LRCLIB).
/// Read-only: no cookie is ever sent.
/// </summary>
public sealed class YouTubeMusicLyricsProvider
{
    private static readonly YouTubeClient Client = YouTubeClient.ANDROID_MUSIC;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public sealed record Result(string? Synced, string? Plain, string? Source);

    public async Task<Result?> GetAsync(string videoId, CancellationToken ct = default)
    {
        // Only real YouTube ids (11 chars of [A-Za-z0-9_-]); local files and
        // other providers fall straight through to LRCLIB.
        if (string.IsNullOrWhiteSpace(videoId) || videoId.Length != 11
            || !videoId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) return null;
        try
        {
            var next = await PostAsync("next", new JsonObject { ["videoId"] = videoId }, ct).ConfigureAwait(false);
            var browseId = FindAll(next, "browseEndpoint")
                .Select(e => e?["browseId"]?.GetValue<string>())
                .FirstOrDefault(id => id != null && id.StartsWith("MPLY", StringComparison.Ordinal));
            if (browseId == null)
            {
                Trace.WriteLine($"[Lyrics] YTM: no lyrics tab for {videoId}");
                return null;
            }

            var browse = await PostAsync("browse", new JsonObject { ["browseId"] = browseId }, ct).ConfigureAwait(false);
            var source = FindAll(browse, "sourceMessage").Select(TextOf).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

            var timed = FindAll(browse, "timedLyricsData").OfType<JsonArray>().FirstOrDefault();
            string? synced = timed is { Count: > 0 } ? ToLrc(timed) : null;

            string? plain = null;
            if (synced == null)
            {
                plain = FindAll(browse, "musicDescriptionShelfRenderer")
                    .Select(s => TextOf(s?["description"]))
                    .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            }

            Trace.WriteLine($"[Lyrics] YTM {videoId}: synced={synced != null} plain={plain != null} ({source})");
            return synced == null && plain == null ? null : new Result(synced, plain, source);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Lyrics] YTM failed for {videoId}: {ex.Message}");
            return null;
        }
    }

    private static string? ToLrc(JsonArray lines)
    {
        var sb = new StringBuilder();
        sb.Append(SoundLibert.Domain.Lyrics.LyricsPlaybackPolicy.ExactSourceTag).Append('\n');
        var timedLines = 0;
        foreach (var line in lines)
        {
            var text = line?["lyricLine"]?.GetValue<string>() ?? string.Empty;
            var startNode = line?["cueRange"]?["startTimeMilliseconds"];
            if (startNode == null) continue;
            var ms = startNode.GetValueKind() == JsonValueKind.String
                ? long.Parse(startNode.GetValue<string>(), CultureInfo.InvariantCulture)
                : startNode.GetValue<long>();
            // "♪" marks an instrumental gap: keep the timestamp (clears the
            // highlight between verses) but no visible glyph row.
            if (text.Trim() == "♪") text = string.Empty;
            var t = TimeSpan.FromMilliseconds(ms);
            sb.Append(CultureInfo.InvariantCulture, $"[{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}]{text}\n");
            timedLines++;
        }
        return timedLines == 0 ? null : sb.ToString();
    }

    private static async Task<JsonNode?> PostAsync(string endpoint, JsonObject body, CancellationToken ct)
    {
        body["context"] = new JsonObject
        {
            ["client"] = new JsonObject
            {
                ["clientName"] = Client.ClientName,
                ["clientVersion"] = Client.ClientVersion,
                ["hl"] = "en",
                ["gl"] = "US",
                ["osName"] = Client.OsName,
                ["osVersion"] = Client.OsVersion,
                ["androidSdkVersion"] = Client.AndroidSdkVersion
            }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post,
            YouTubeClient.ApiUrlYouTubeMusic + endpoint + "?prettyPrint=false")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.UserAgent.ParseAdd(Client.UserAgent);
        request.Headers.Add("X-YouTube-Client-Name", Client.ClientId);
        request.Headers.Add("X-YouTube-Client-Version", Client.ClientVersion);

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private static string? TextOf(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj when obj["simpleText"] is { } simple:
                return simple.GetValue<string>();
            case JsonObject obj when obj["runs"] is JsonArray runs:
                return string.Concat(runs.Select(r => r?["text"]?.GetValue<string>() ?? string.Empty));
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return value.GetValue<string>();
            default:
                return null;
        }
    }

    private static IEnumerable<JsonNode?> FindAll(JsonNode? node, string key)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (k, v) in obj)
                {
                    if (k == key) yield return v;
                    foreach (var child in FindAll(v, key)) yield return child;
                }
                break;
            case JsonArray arr:
                foreach (var v in arr)
                    foreach (var child in FindAll(v, key)) yield return child;
                break;
        }
    }
}
