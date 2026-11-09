using System.Text;
using System.Text.RegularExpressions;

namespace SoundLibert.Infra;

/// <summary>
/// Judges an upload by its title alone: is it the song's music video, is it
/// junk, is it the same song at all. Every check works on whole words, so
/// "Alive" is no live take, "Discover" no cover and "Oliver Tree" no live take
/// either. An ordinary word such as "live" or "cover" only counts in the
/// decoration around a song's name: inside brackets, after " - " or " | ", or
/// as the last word. "Live Forever" and "Cover Me Up" therefore stay real clips.
/// </summary>
public static class ClipTitleRules
{
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex Brackets = new(@"[\(\[\{（【][^\)\]\}）】]*[\)\]\}）】]", Options);
    private static readonly Regex Separators = new(@"\s+[-–—|~]\s+|\s+//\s+", Options);

    /// <summary>Words that say nothing about the version ("Live Version", "Remix 2019").</summary>
    private static readonly Regex TrailingFiller = new(
        @"(?:[\s\W]*\b(?:version|edit|mix|take|video|performance|recording|\d{2,4}))+[\s\W]*$", Options);

    /// <summary>No song is named after these, so they count anywhere in the title.</summary>
    private static readonly Regex NonClipPhrases = new(
        @"\b(?:behind the scenes|live lounge|tiny desk|vevo presents|in concert"
        + @"|live (?:at|from|in|on|version|performance|session)"
        + @"|acoustic (?:version|session|performance)"
        + @"|lyrics? video|official (?:lyrics?|audio|visuali[sz]er)|music visuali[sz]er|audio only"
        + @"|cover (?:by|of)|karaoke|nightcore|8d|slowed|reverb)\b", Options);

    private static readonly Regex NonClipWords = new(
        @"\b(?:lyrics?|visuali[sz]ers?|audio|covers?|live|remix(?:es|ed)?|reactions?|instrumentals?"
        + @"|tutorials?|interviews?|trailers?|teasers?|unplugged|acoustic|concerts?|tours?"
        + @"|festivals?|sessions?|stripped)\b", Options);

    private static readonly Regex JunkPhrases = new(
        @"\b(?:behind the scenes|fan[- ]?made|bass boosted|no ads|(?:1|8|10) hours?"
        + @"|cover (?:by|of)|karaoke|nightcore|8d|slowed|reverb)\b", Options);

    private static readonly Regex JunkWords = new(
        @"\b(?:covers?|reactions?|remix(?:es|ed)?|instrumentals?|tutorials?|interviews?"
        + @"|trailers?|teasers?|tributes?|compilations?)\b", Options);

    /// <summary>
    /// False for uploads that are not the actual music video (lyric videos,
    /// visualisers, audio uploads, covers, live takes, ...). Words that belong
    /// to <paramref name="songTitle"/> itself are ignored: "Long Live" is not a
    /// live take of the song "Long Live", but "Long Live (Live)" is.
    /// </summary>
    public static bool IsRealMusicVideoTitle(string? title, string? songTitle = null)
        => !HasMarker(title, songTitle, NonClipPhrases, NonClipWords);

    /// <summary>
    /// True for hard junk that is never a watchable official upload (karaoke,
    /// covers, reactions, remixes, slowed edits, interviews, trailers, ...).
    /// Lyric, audio, visualiser and live uploads are not junk: they can be the
    /// official channel's own upload.
    /// </summary>
    public static bool IsRejectedUploadTitle(string? title, string? songTitle = null)
        => HasMarker(title, songTitle, JunkPhrases, JunkWords);

    /// <summary>
    /// True when an upload's title names the song. Long titles may sit inside
    /// each other ("Artist - Song"). A short one ("22", "Hey", "青と夏") proves
    /// little as a substring ("they" holds "hey"), so it must stand as whole
    /// words of the upload's title; a single character only matches exactly.
    /// </summary>
    public static bool TitlesMatch(string? songTitle, string? uploadTitle)
    {
        var cleanUpload = Clean(uploadTitle);
        var want = Norm(Clean(songTitle));
        var got = Norm(cleanUpload);
        if (want.Length == 0 || got.Length == 0) return false;
        if (want == got) return true;
        if (want.Length >= 4 && got.Contains(want, StringComparison.Ordinal)) return true;
        if (got.Length >= 4 && want.Contains(got, StringComparison.Ordinal)) return true;
        return want.Length >= 2 && HasWholeWords(cleanUpload, want);
    }

    /// <summary>Lowercase letters and digits only: "ImagineDragonsVEVO" matches "Imagine Dragons".</summary>
    public static string Norm(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>The title without bracketed parts ("Song (Official Video)" gives "Song").</summary>
    public static string Clean(string? title)
    {
        if (string.IsNullOrEmpty(title)) return string.Empty;
        var cleaned = Regex.Replace(Regex.Replace(title, @"[\(\[][^\)\]]*[\)\]]", " "), @"\s+", " ").Trim();
        return cleaned.Length > 0 ? cleaned : title;
    }

    private static bool HasMarker(string? title, string? songTitle, Regex phrases, Regex words)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var rest = WithoutSongName(title, songTitle, out var nameRemoved);
        if (phrases.IsMatch(rest)) return true;

        foreach (Match bracket in Brackets.Matches(rest))
            if (words.IsMatch(bracket.Value)) return true;

        var parts = Separators.Split(Brackets.Replace(rest, " "));
        if (parts.Length > 1)
        {
            // What precedes the first separator is the artist ("Live - Lightning Crashes").
            for (var i = 1; i < parts.Length; i++)
                if (EndsWithWord(parts[i], words)) return true;
            return false;
        }

        // Whatever stands next to the song's own name can only be decoration;
        // without a known name, the last word of a longer title can be.
        var name = parts[0].Trim();
        return nameRemoved
            ? words.IsMatch(name)
            : name.Any(char.IsWhiteSpace) && EndsWithWord(name, words);
    }

    private static string WithoutSongName(string title, string? songTitle, out bool removed)
    {
        removed = false;
        if (string.IsNullOrWhiteSpace(songTitle)) return title;
        foreach (var name in new[] { songTitle.Trim(), Clean(songTitle) })
        {
            var at = title.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            removed = true;
            return string.Concat(title.AsSpan(0, at), " ", title.AsSpan(at + name.Length));
        }
        return title;
    }

    private static bool EndsWithWord(string text, Regex words)
    {
        var rest = TrailingFiller.Replace(text, string.Empty).TrimEnd();
        while (rest.Length > 0 && !char.IsLetterOrDigit(rest[^1])) rest = rest[..^1];
        var matches = words.Matches(rest);
        if (matches.Count == 0) return false;
        var last = matches[^1];
        return last.Index + last.Length == rest.Length;
    }

    private static bool HasWholeWords(string title, string normalizedWords)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in title.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) tokens.Add(current.ToString());

        // Neighbouring tokens may spell it together ("A.M." is "am").
        for (var i = 0; i < tokens.Count; i++)
        {
            var joined = string.Empty;
            for (var j = i; j < tokens.Count && joined.Length < normalizedWords.Length; j++)
            {
                joined += tokens[j];
                if (joined == normalizedWords) return true;
            }
        }
        return false;
    }
}
