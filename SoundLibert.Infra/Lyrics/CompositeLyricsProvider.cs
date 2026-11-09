using System.Diagnostics;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Lyrics;
using SoundLibert.Domain.Models;

namespace SoundLibert.Infra.Lyrics;

/// <summary>
/// Lyrics in order of how well they stay in sync:
/// 1. YouTube Music timed lyrics for the exact video id that plays;
/// 2. LRCLIB synced lyrics matched by duration;
/// 3. plain words (YouTube Music first, then LRCLIB).
/// YouTube Music going first also keeps LRCLIB under its rate limit
/// (429/503 used to turn into "No lyrics found").
/// </summary>
public sealed class CompositeLyricsProvider : ILyricsProvider
{
    private readonly YouTubeMusicLyricsProvider _ytm;
    private readonly LrclibLyricsProvider _lrclib;

    public CompositeLyricsProvider() : this(new YouTubeMusicLyricsProvider(), new LrclibLyricsProvider()) { }

    public CompositeLyricsProvider(YouTubeMusicLyricsProvider ytm, LrclibLyricsProvider lrclib)
    {
        _ytm = ytm;
        _lrclib = lrclib;
    }

    public string Name => "YouTube Music + LRCLIB";
    public bool IsEnabled => true;

    public async Task<string?> GetExactTimedLyricsAsync(string videoId, CancellationToken ct = default)
        => (await _ytm.GetAsync(videoId, ct))?.Synced;

    public async Task<string?> GetLyricsAsync(
        string videoId, string title, string artist, string? album,
        int durationSeconds, CancellationToken ct = default)
    {
        var ytm = await _ytm.GetAsync(videoId, ct);
        if (!string.IsNullOrWhiteSpace(ytm?.Synced)) return ytm.Synced;

        var lrc = await _lrclib.GetLyricsAsync(videoId, title, artist, album, durationSeconds, ct);
        if (!string.IsNullOrWhiteSpace(lrc) && SyncedLyricsParser.Parse(lrc).SyncType != LyricsSyncType.None)
            return lrc;

        var plain = !string.IsNullOrWhiteSpace(ytm?.Plain) ? ytm.Plain : lrc;
        if (plain != null) Trace.WriteLine($"[Lyrics] plain only for {videoId} ({(plain == ytm?.Plain ? "YTM" : "LRCLIB")})");
        return plain;
    }
}
