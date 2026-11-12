using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoundLibert.Domain.Interfaces;

namespace SoundLibert.Infra.Lyrics;

/// <summary>
/// Lyrics provider backed by LRCLIB (https://lrclib.net), a free community
/// lyrics API. Ported conceptually from Velune's `lrclib` module: query
/// /api/search by track + artist (+album), then pick the best candidate by
/// duration (±2s, as Velune does) preferring synced (LRC) over plain text.
/// </summary>
public sealed class LrclibLyricsProvider : ILyricsProvider
{
    private readonly HttpClient _http;
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://lrclib.net"),
        Timeout = TimeSpan.FromSeconds(15)
    };

    // LRCLIB responses are inconsistent: some records have `duration: null`
    // and numbers occasionally arrive as strings. Default System.Text.Json
    // would throw on the whole array and we'd silently return no lyrics —
    // so all value fields are nullable and numbers may be read from strings.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    static LrclibLyricsProvider()
    {
        // LRCLIB asks clients to identify themselves.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(AppIdentity.UserAgent);
    }

    public LrclibLyricsProvider() : this(Http) { }

    /// <summary>The caller owns the supplied client; useful for deterministic API tests.</summary>
    public LrclibLyricsProvider(HttpClient http) => _http = http;

    public string Name => "LRCLIB";
    public bool IsEnabled => true;

    public async Task<string?> GetLyricsAsync(
        string videoId, string title, string artist, string? album,
        int durationSeconds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        LrcTrack? best = null;
        var via = "none";
        try
        {
            var tracks = await SearchAsync(title, artist ?? string.Empty, album, ct).ConfigureAwait(false);
            var effectiveTitle = CleanVideoTitle(title);
            (best, via) = PickBest(tracks ?? new(), durationSeconds, effectiveTitle, artist ?? string.Empty);

            // A plain hit must not prevent a clean-title query from finding
            // timed lyrics for the same recording.
            if ((best == null || via == "plain") &&
                !string.Equals(effectiveTitle, title, StringComparison.Ordinal))
            {
                Trace.WriteLine($"[Lyrics] LRCLIB retry with cleaned title: \"{effectiveTitle}\"");
                var cleanedTracks = await SearchAsync(effectiveTitle, artist ?? string.Empty, album, ct).ConfigureAwait(false);
                if (cleanedTracks != null)
                {
                    tracks ??= new();
                    tracks.AddRange(cleanedTracks);
                    (best, via) = PickBest(tracks, durationSeconds, effectiveTitle, artist ?? string.Empty);
                }
            }

            // YouTube album labels can differ from LRCLIB releases. Broaden
            // only the existing search filter; keep the duration/identity gate.
            if ((best == null || via == "plain") && !string.IsNullOrWhiteSpace(album))
            {
                var albumlessTracks = await SearchAsync(effectiveTitle, artist ?? string.Empty, null, ct).ConfigureAwait(false);
                if (albumlessTracks != null)
                {
                    tracks ??= new();
                    tracks.AddRange(albumlessTracks);
                    (best, via) = PickBest(tracks, durationSeconds, effectiveTitle, artist ?? string.Empty);
                }
            }

            // Catalogue credits can combine several artists while LRCLIB credits
            // only the main artist. Broaden retrieval, not acceptance: the same
            // duration and original title/artist gates still select the result.
            if ((best == null || via == "plain") && durationSeconds > 0
                && !string.IsNullOrWhiteSpace(artist))
            {
                Trace.WriteLine("[Lyrics] LRCLIB retry without artist filter; retaining identity/duration checks");
                var titleTracks = await SearchAsync(effectiveTitle, string.Empty, null, ct).ConfigureAwait(false);
                if (titleTracks != null)
                {
                    tracks ??= new();
                    tracks.AddRange(titleTracks);
                    (best, via) = PickBest(tracks, durationSeconds, effectiveTitle, artist);
                }
            }

            tracks ??= new();
            if (best == null)
            {
                var preview = string.Join(" | ", tracks.Take(5).Select(t =>
                    $"\"{t.TrackName}\" — {t.ArtistName} ({(t.Duration.HasValue ? $"{t.Duration.Value:F0}s" : "n/a")}, synced={t.SyncedLyrics != null})"));
                Trace.WriteLine($"[Lyrics] LRCLIB miss: {tracks.Count} candidates, none accepted for " +
                                $"\"{effectiveTitle}\" — {artist} ({durationSeconds}s): {preview}");
                return null;
            }

            return SelectedLyrics(best, via, durationSeconds);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Lyrics] LRCLIB failed: {ex.Message}");
            // Optional retrieval must not discard a candidate already accepted
            // by the identity/duration rules. Stop querying and keep its words.
            return best == null ? null : SelectedLyrics(best, via, durationSeconds);
        }
    }

    private static string? SelectedLyrics(LrcTrack best, string via, int durationSeconds)
    {
        var timed = via != "plain" && !string.IsNullOrWhiteSpace(best.SyncedLyrics);
        var delta = best.Duration.HasValue ? $"{best.Duration.Value - durationSeconds:+0;-0;0}s" : "n/a";
        Trace.WriteLine($"[Lyrics] LRCLIB match: {best.TrackName} — {best.ArtistName} " +
                        $"(id={best.Id}, {best.Duration:F3}s, synced={timed}, via={via}, Δ={delta})");
        return timed ? best.SyncedLyrics : best.PlainLyrics;
    }

    private async Task<List<LrcTrack>?> SearchAsync(
        string title, string artist, string? album, CancellationToken ct)
    {
        var url = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}";
        if (!string.IsNullOrWhiteSpace(artist))
            url += $"&artist_name={Uri.EscapeDataString(artist)}";
        if (!string.IsNullOrWhiteSpace(album))
            url += $"&album_name={Uri.EscapeDataString(album)}";

        for (var attempt = 0; ; attempt++)
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Trace.WriteLine($"[Lyrics] LRCLIB query failed: {(int)response.StatusCode}");
                if (response.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests)
                {
                    var retry = response.Headers.RetryAfter;
                    var wait = retry?.Delta ?? (retry?.Date is { } date
                        ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(1));
                    // Respect the server's backoff. A longer outage should keep
                    // cached words rather than hold the lyrics load indefinitely.
                    if (wait > TimeSpan.FromSeconds(5)) response.EnsureSuccessStatusCode();
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
                    // One retry after the server's backoff: before, the wait was
                    // honoured but the query was dropped → "No lyrics found".
                    if (attempt == 0) continue;
                }
                return null;
            }
            return await response.Content.ReadFromJsonAsync<List<LrcTrack>>(JsonOptions, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Strips upload/video markers ("(Official Video)", "[Official Audio]",
    /// "…" ) so clip titles match the catalogue record. Conservative: only
    /// parenthetical/bracket segments containing a video/audio/official/lyric
    /// marker are removed, the rest of the title is untouched.
    /// </summary>
    private static string CleanVideoTitle(string title)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            title,
            @"[\(\[][^\)\]]*(official\s*(music\s*)?(video|audio|lyric[s]?|visualizer)|music\s*video|lyric[s]?\s*video|audio|visualizer)[^\)\]]*[\)\]]",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(cleaned.Trim(), @"\s{2,}", " ");
    }

    private static (LrcTrack? Track, string Via) PickBest(
        List<LrcTrack> tracks, int duration, string title, string artist)
    {
        // 1. Same rule as Velune: closest duration match (±2s), preferring a
        //    synced record. Only a record timed for THIS recording keeps the
        //    highlight in sync — a similar text with a different timing drifts
        //    for the whole song, so it must never be accepted as synced.
        if (duration > 0)
        {
            var byDuration = tracks
                .Where(t => t.Duration.HasValue &&
                            Math.Abs(t.Duration.Value - duration) <= 2
                            && (Similarity(title, t.TrackName) + Similarity(artist, t.ArtistName)) / 2.0 >= 0.85
                            && !string.IsNullOrWhiteSpace(t.SyncedLyrics))
                .OrderBy(t => Math.Abs(t.Duration!.Value - duration))
                .FirstOrDefault();

            if (byDuration != null) return (byDuration, "duration");
        }

        // With a known duration, never import another recording's timing.
        if (duration <= 0)
        {
            var scored = tracks
                .Select(t => new
                {
                    Track = t,
                    Sim = (Similarity(title, t.TrackName) + Similarity(artist, t.ArtistName)) / 2.0,
                })
                .Where(x => x.Sim > 0.6)
                .OrderByDescending(x => !string.IsNullOrWhiteSpace(x.Track.SyncedLyrics))
                .ThenByDescending(x => x.Sim)
                .ToList();

            var bestSynced = scored.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Track.SyncedLyrics));
            if (bestSynced != null) return (bestSynced.Track, "similarity");
        }

        // Wrong-duration records may still supply words, but never timestamps.
        var plain = tracks
            .Select(t => new
            {
                Track = t,
                Sim = (Similarity(title, t.TrackName) + Similarity(artist, t.ArtistName)) / 2.0,
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Track.PlainLyrics)
                        && x.Sim >= 0.85)
            .OrderByDescending(x => x.Sim)
            .FirstOrDefault();
        return (plain?.Track, plain != null ? "plain" : "none");
    }

    private static double Similarity(string? a, string? b)
    {
        a = (a ?? string.Empty).Trim().ToLowerInvariant();
        b = (b ?? string.Empty).Trim().ToLowerInvariant();
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        if (a.Contains(b) || b.Contains(a)) return 0.8;

        int max = Math.Max(a.Length, b.Length);
        return 1.0 - (double)Levenshtein(a, b) / max;
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    private sealed class LrcTrack
    {
        [JsonPropertyName("id")] public int? Id { get; set; }
        [JsonPropertyName("trackName")] public string? TrackName { get; set; }
        [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
        [JsonPropertyName("albumName")] public string? AlbumName { get; set; }
        [JsonPropertyName("duration")] public double? Duration { get; set; }
        [JsonPropertyName("instrumental")] public bool? Instrumental { get; set; }
        [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; set; }
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }
    }
}
