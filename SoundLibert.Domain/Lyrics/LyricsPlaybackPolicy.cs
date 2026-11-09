namespace SoundLibert.Domain.Lyrics;

/// <summary>Lyrics must describe the decoded recording, not a stale catalogue duration.</summary>
public static class LyricsPlaybackPolicy
{
    public static int DurationSeconds(int catalogueSeconds, TimeSpan decodedDuration)
        => decodedDuration.TotalSeconds >= 1 && decodedDuration.TotalSeconds <= int.MaxValue
            ? (int)Math.Round(decodedDuration.TotalSeconds)
            : Math.Max(0, catalogueSeconds);

    // A logical queue row can play an ATV, an OMV or an offline copy with a
    // different length. Raw legacy lyrics contain no recording provenance.
    // Include the catalogue owner so cleanup can retain its version caches even
    // when the played counterpart has no row. Hex also makes SQL GLOB safe for
    // local-file IDs containing wildcard characters or non-ASCII text.
    public static string CacheKey(string rowId, string playbackId, int durationSeconds)
        => $"lyrics:v1:{Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(rowId))}:{Uri.EscapeDataString(playbackId)}:{durationSeconds}";

    /// <summary>
    /// LRC metadata tag (ignored by the parser) marking lyrics timed for the
    /// exact video id by YouTube Music. Cached lyrics WITHOUT it (LRCLIB,
    /// matched only by duration) may drift and are re-checked once a session.
    /// </summary>
    public const string ExactSourceTag = "[src:ytm]";

    public static bool IsExactSource(string? text)
        => text != null && text.StartsWith(ExactSourceTag, StringComparison.Ordinal);

    public static bool CanUseLegacyTiming(string rowId, string playbackId, int catalogueSeconds, TimeSpan decodedDuration)
        => string.Equals(rowId, playbackId, StringComparison.Ordinal)
           && catalogueSeconds > 0 && decodedDuration > TimeSpan.Zero
           && Math.Abs(decodedDuration.TotalSeconds - catalogueSeconds) <= 2;
}
