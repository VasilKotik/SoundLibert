using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using SoundLibert.Domain.Interfaces;

namespace SoundLibert.Infra.Genres;

/// <summary>
/// Genres from the iTunes Search API (free, no key). A song lookup or a
/// <c>musicArtist</c> lookup returns <c>primaryGenreName</c> ("Rock",
/// "Hip-Hop/Rap", "Dance", ...). Answers are cached in SQLite, including "no
/// genre", so a playlist is resolved once. A failed request says nothing about
/// the genre and is never cached: it used to store "no genre" for good, so one
/// offline moment or one rate limit left a playlist without chips forever.
/// </summary>
public sealed class ITunesGenreService : IGenreService
{
    /// <summary>Apple asks for about 20 calls a minute; beyond that it answers 403.</summary>
    private static readonly TimeSpan MinRequestGap = TimeSpan.FromSeconds(3);

    /// <summary>After a rate limit nothing is asked for a while: more calls only prolong it.</summary>
    private static readonly TimeSpan RateLimitPause = TimeSpan.FromMinutes(5);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly IMusicDatabase _database;
    private readonly HttpClient _http;
    private readonly TimeSpan _minRequestGap;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, string?> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string?> _songMemory = new(StringComparer.Ordinal);
    private DateTime _nextRequestAt = DateTime.MinValue;
    private DateTime _pausedUntil = DateTime.MinValue;

    static ITunesGenreService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(AppIdentity.UserAgent);
    }

    public ITunesGenreService(IMusicDatabase database) : this(database, Http, MinRequestGap) { }

    internal ITunesGenreService(IMusicDatabase database, HttpClient http, TimeSpan minRequestGap)
    {
        _database = database;
        _http = http;
        _minRequestGap = minRequestGap;
    }

    public async Task<string?> GetArtistGenreAsync(string artistName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artistName)) return null;
        var key = artistName.Trim();

        if (_memory.TryGetValue(key, out var cached)) return cached;

        // DB cache: null = never looked up; "" = looked up, no genre.
        try
        {
            var stored = await _database.GetArtistGenreAsync(key, ct);
            if (stored != null)
            {
                var value = string.IsNullOrEmpty(stored) ? null : stored;
                _memory[key] = value;
                return value;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Genre] cache read failed: {ex.Message}");
        }

        var (answered, genre) = await LookupArtistAsync(key, ct);
        if (!answered) return null;

        _memory[key] = genre;
        try { await _database.SetArtistGenreAsync(key, genre, ct); }
        catch (Exception ex) { Debug.WriteLine($"[Genre] cache write failed: {ex.Message}"); }
        return genre;
    }

    public async Task<string?> GetSongGenreAsync(string songId, string title, string artistName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(songId)) return null;
        var key = songId.Trim();

        if (_songMemory.TryGetValue(key, out var cached)) return cached;

        // DB cache: null = never looked up; "" = looked up, no genre.
        try
        {
            var stored = await _database.GetSongGenreAsync(key, ct);
            if (stored != null)
            {
                var value = string.IsNullOrEmpty(stored) ? null : stored;
                _songMemory[key] = value;
                return value;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Genre] song cache read failed: {ex.Message}");
        }

        // Song-level first (reflects the track), artist-level as the fallback.
        var (answered, genre) = await LookupSongAsync(title, artistName, ct);
        if (!answered) return null;
        genre ??= await GetArtistGenreAsync(artistName, ct);

        // The artist step may have failed too: a blank is only final if Apple
        // actually said so, which the artist cache knows.
        if (genre == null && !string.IsNullOrWhiteSpace(artistName)
            && !_memory.ContainsKey(artistName.Trim()))
            return null;

        _songMemory[key] = genre;
        try { await _database.SetSongGenreAsync(key, genre, ct); }
        catch (Exception ex) { Debug.WriteLine($"[Genre] song cache write failed: {ex.Message}"); }
        return genre;
    }

    /// <summary>Looks up one track by title + artist via the iTunes song entity.</summary>
    private async Task<(bool Answered, string? Genre)> LookupSongAsync(string title, string artist, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) return (true, null);

        var term = Uri.EscapeDataString($"{title} {artist}".Trim());
        var results = await SearchAsync("https://itunes.apple.com/search?entity=song&limit=5&term=" + term, ct);
        if (results == null) return (false, null);

        // Only a result that really is this song: the first hit of a search
        // for an unknown track is some other song with another genre.
        var wantTitle = Norm(title);
        var wantArtist = Norm(artist);
        var best = results.FirstOrDefault(r =>
            r.TrackName is { Length: > 0 } track
            && SameTitle(Norm(track), wantTitle)
            && (wantArtist.Length < 2 || string.IsNullOrEmpty(r.ArtistName) || Norm(r.ArtistName).Contains(wantArtist)));
        var genre = string.IsNullOrWhiteSpace(best?.PrimaryGenreName) ? null : best!.PrimaryGenreName;
        Debug.WriteLine($"[Genre] song \"{title}\" / \"{artist}\" -> {genre ?? "unknown"}");
        return (true, genre);
    }

    private async Task<(bool Answered, string? Genre)> LookupArtistAsync(string artist, CancellationToken ct)
    {
        var results = await SearchAsync(
            "https://itunes.apple.com/search?entity=musicArtist&limit=5&term=" + Uri.EscapeDataString(artist), ct);
        if (results == null) return (false, null);

        var want = Norm(artist);
        var best = results.FirstOrDefault(r => Norm(r.ArtistName) == want);
        var genre = string.IsNullOrWhiteSpace(best?.PrimaryGenreName) ? null : best!.PrimaryGenreName;
        Debug.WriteLine($"[Genre] \"{artist}\" -> {genre ?? "unknown"}");
        return (true, genre);
    }

    /// <summary>One paced request. Null means no answer, so nothing can be concluded.</summary>
    private async Task<List<ITunesResult>?> SearchAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow < _pausedUntil) return null;

            var wait = _nextRequestAt - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _nextRequestAt = DateTime.UtcNow + _minRequestGap;

            using var response = await _http.GetAsync(url, ct);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                _pausedUntil = DateTime.UtcNow + RateLimitPause;
                Trace.WriteLine($"[Genre] iTunes rate limit ({(int)response.StatusCode}): no lookups for {RateLimitPause.TotalMinutes:0} minutes.");
                return null;
            }
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<ITunesResponse>(ct);
            return result?.Results ?? new List<ITunesResult>();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Genre] lookup failed: {ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool SameTitle(string got, string want)
    {
        if (want.Length == 0 || got.Length == 0) return false;
        if (got == want) return true;
        return want.Length >= 3 && got.Length >= 3 && (got.Contains(want) || want.Contains(got));
    }

    private static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    private sealed class ITunesResponse
    {
        [JsonPropertyName("results")]
        public List<ITunesResult>? Results { get; set; }
    }

    private sealed class ITunesResult
    {
        [JsonPropertyName("primaryGenreName")]
        public string? PrimaryGenreName { get; set; }

        [JsonPropertyName("trackName")]
        public string? TrackName { get; set; }

        [JsonPropertyName("artistName")]
        public string? ArtistName { get; set; }
    }
}
