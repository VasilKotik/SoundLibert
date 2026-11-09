using System.Text.RegularExpressions;
using SoundLibert.Domain.Models;

namespace SoundLibert.Domain.Lyrics;

/// <summary>
/// Normalizes any lyrics payload into <see cref="SyncedLyrics"/>.
/// LRC rules (same contract as the former UI-layer parser, kept for behavior
/// parity): leading <c>[..]</c> tags are read one by one; timestamp tags
/// (<c>[mm:ss]</c>, <c>[mm:ss.xx]</c>, <c>[mm:ss.xxx]</c>, <c>[mm:ss:xx]</c>,
/// several per line) create lines; metadata tags (<c>[ti:]</c>, <c>[ar:]</c>,
/// <c>[by:]</c>, …) are ignored; a leading <c>[offset:±ms]</c> is subtracted;
/// timed lines are sorted; timestamp-only lines are kept (they clear the
/// highlight during instrumental breaks); text without timestamps becomes
/// <see cref="LyricsSyncType.None"/> lines. Never throws on malformed input.
/// </summary>
public static class SyncedLyricsParser
{
    private static readonly Regex TimeTagRegex = new(
        @"^\[(?<m>\d{1,3}):(?<s>\d{1,2})(?:[.:](?<f>\d{1,3}))?\]$",
        RegexOptions.Compiled);

    private static readonly Regex OffsetRegex = new(
        @"\[offset:(?<v>[+-]?\d+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses <paramref name="text"/> into normalized lyrics.
    /// <paramref name="totalDuration"/> (when known) becomes the last line's
    /// end time; otherwise the last line ends at its own start.
    /// </summary>
    public static SyncedLyrics Parse(string? text, TimeSpan? totalDuration = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return SyncedLyrics.Empty;

        double offsetSeconds = 0;
        try
        {
            var offsetMatch = OffsetRegex.Match(text);
            if (offsetMatch.Success &&
                double.TryParse(offsetMatch.Groups["v"].Value, out var offsetMs))
            {
                offsetSeconds = offsetMs / 1000.0;
            }
        }
        catch
        {
            offsetSeconds = 0;
        }

        var timed = new List<(TimeSpan Start, string Text)>();
        var plain = new List<string>();

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                plain.Add(string.Empty);
                continue;
            }

            var times = new List<TimeSpan>();
            int pos = 0;
            while (pos < line.Length && line[pos] == '[')
            {
                int end = line.IndexOf(']', pos);
                if (end < 0) break;

                var tag = line.Substring(pos, end - pos + 1);
                var tm = TimeTagRegex.Match(tag);
                if (tm.Success &&
                    int.TryParse(tm.Groups["m"].Value, out var min) &&
                    int.TryParse(tm.Groups["s"].Value, out var sec))
                {
                    double frac = 0;
                    if (tm.Groups["f"].Success)
                    {
                        var digits = tm.Groups["f"].Value;
                        if (digits.Length > 3) digits = digits[..3];
                        while (digits.Length < 3) digits += "0";
                        if (int.TryParse(digits, out var ms))
                            frac = ms / 1000.0;
                    }

                    var t = min * 60 + sec + frac - offsetSeconds;
                    if (t < 0) t = 0;
                    if (!double.IsNaN(t) && !double.IsInfinity(t))
                        times.Add(TimeSpan.FromSeconds(t));
                }
                // Non-timestamp tags are metadata — ignored.

                pos = end + 1;
                while (pos < line.Length && line[pos] == ' ') pos++;
            }

            var content = line[pos..].Trim();

            if (times.Count > 0)
            {
                foreach (var t in times)
                    timed.Add((t, content));
            }
            else if (content.Length > 0)
            {
                plain.Add(content);
            }
        }

        if (timed.Count == 0)
        {
            if (plain.Count == 0)
                return SyncedLyrics.Empty;

            return new SyncedLyrics
            {
                SyncType = LyricsSyncType.None,
                Lines = plain.Select(p => new SyncedLyricLine
                {
                    Start = TimeSpan.Zero,
                    End = TimeSpan.Zero,
                    Text = p
                }).ToList()
            };
        }

        timed.Sort((a, b) => a.Start.CompareTo(b.Start));

        var lines = new List<SyncedLyricLine>(timed.Count);
        for (int i = 0; i < timed.Count; i++)
        {
            var end = i + 1 < timed.Count
                ? timed[i + 1].Start
                : totalDuration.HasValue && totalDuration.Value > timed[i].Start
                    ? totalDuration.Value
                    : timed[i].Start;
            lines.Add(new SyncedLyricLine
            {
                Start = timed[i].Start,
                End = end,
                Text = timed[i].Text
            });
        }

        return new SyncedLyrics
        {
            SyncType = lines.Any(l => l.HasWords) ? LyricsSyncType.Word : LyricsSyncType.Line,
            Lines = lines
        };
    }
}
