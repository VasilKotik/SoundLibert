using SoundLibert.Domain.Models;

namespace SoundLibert.Domain.Lyrics;

/// <summary>
/// Pure playback-position → active-line/word lookup. Stateless and thread-safe;
/// operates on the immutable <see cref="SyncedLyrics"/> snapshot, so callers on
/// audio threads never touch UI collections. The rule (same as Spotify/YouTube
/// cue lookup): the active line is the LAST line whose start is at or before
/// the position — but a line older than <see cref="StaleLineAfter"/> stops
/// highlighting, so a long instrumental gap without an explicit marker does
/// not leave the highlight stuck (Spotify client behaviour).
/// </summary>
public static class LyricsSyncEngine
{
    /// <summary>
    /// A highlighted line expires after this long without a next line taking
    /// over. Matches the Spotify-client rule (8 s) for unmarked gaps.
    /// </summary>
    public static readonly TimeSpan StaleLineAfter = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Index of the active line, or −1 when there is nothing to highlight
    /// (no lyrics, plain lyrics, position before the first line, or the
    /// current line went stale). Binary search — O(log n) regardless of
    /// song length.
    /// </summary>
    public static int FindLineIndex(SyncedLyrics? lyrics, TimeSpan position, TimeSpan? staleAfter = null)
    {
        if (lyrics is not { SyncType: not LyricsSyncType.None } || !lyrics.HasLines)
            return -1;

        var lines = lyrics.Lines;
        int lo = 0, hi = lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (lines[mid].Start <= position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        if (found < 0)
            return -1;
        if (position - lines[found].Start > (staleAfter ?? StaleLineAfter))
            return -1;
        return found;
    }

    /// <summary>
    /// Index of the active word inside an already-resolved line, or −1 when
    /// the line carries no word timings. Never invents timing.
    /// </summary>
    public static int FindWordIndex(SyncedLyricLine? line, TimeSpan position)
    {
        if (line == null || !line.HasWords)
            return -1;

        var words = line.Words;
        int lo = 0, hi = words.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (words[mid].Start <= position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found;
    }
}
