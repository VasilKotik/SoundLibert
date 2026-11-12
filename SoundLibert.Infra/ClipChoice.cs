using System.Text.RegularExpressions;

namespace SoundLibert.Infra;

/// <summary>
/// What makes an upload fit to stand behind a song: whose channel it comes from,
/// its shape and its length. Pure rules, no network, so every decision of the clip
/// search can be tested on its own. Titles are judged by <see cref="ClipTitleRules"/>.
/// </summary>
public static class ClipChoice
{
    /// <summary>Who uploaded a video, as far as the song's artists are concerned.</summary>
    public enum ChannelKind
    {
        /// <summary>The artist's own channel: "Imagine Dragons", "ImagineDragonsVEVO", "Queen Official".</summary>
        Artist,
        /// <summary>A channel named after the artist by someone else: "Imagine Dragons Fan", "ImagineDragons Team".</summary>
        Fan,
        /// <summary>YouTube's auto-generated "Artist - Topic" channel: still pictures, never a clip.</summary>
        Topic,
        /// <summary>Anyone else: a label (Atlantic Records, HYBE LABELS) or a stranger.</summary>
        Other
    }

    /// <summary>What official channels add to the artist's name: "ImagineDragonsVEVO", "Queen Official".</summary>
    private static readonly string[] OfficialWords = { "vevo", "official", "music", "band", "the" };

    /// <summary>
    /// What fan pages, "radio" and "lyrics" re-uploaders and edit accounts add to it:
    /// "Imagine Dragons Fan", "ImagineDragons Team", "Imagine Dragons Radio".
    /// </summary>
    private static readonly string[] FanWords =
    {
        "fanpage", "fanchannel", "fanclub", "fans", "fan", "team", "radio", "club", "lyrics", "lyric",
        "edits", "edit", "army", "archives", "archive", "vault", "daily", "updates", "nation", "family",
        "best", "top", "hits", "mixes", "mix", "playlists", "playlist", "covers", "cover", "stans", "stan",
        "forever", "zone", "world", "hd", "4k", "tv", "clips", "videos", "channel", "unofficial", "tribute",
        "karaoke", "nightcore", "slowed", "reverb", "remixes", "remix", "brasil", "brazil", "español", "espanol"
    };

    /// <summary>
    /// Classifies <paramref name="author"/> against the song's artist names. A channel
    /// that starts or ends with an artist's name is the artist's own when the rest of
    /// its name is only "VEVO", "Official" and the like, and a fan account when the rest
    /// says so ("Imagine Dragons Radio"). Anything else is <see cref="ChannelKind.Other"/>:
    /// "Kalush Orchestra" or a label, whose uploads must prove themselves another way.
    /// Whole names only: "Asian Music" is not Sia's.
    /// </summary>
    public static ChannelKind ClassifyChannel(string? author, IEnumerable<string?> artistNames)
    {
        var raw = (author ?? string.Empty).Trim();
        if (raw.EndsWith("- Topic", StringComparison.OrdinalIgnoreCase)) return ChannelKind.Topic;
        var channel = ClipTitleRules.Norm(raw);
        if (channel.Length == 0) return ChannelKind.Other;

        var kind = ChannelKind.Other;
        foreach (var name in artistNames)
        {
            var artist = ClipTitleRules.Norm(name);
            if (artist.Length < 2) continue;
            if (channel == artist) return ChannelKind.Artist;

            string? rest = null;
            if (channel.StartsWith(artist, StringComparison.Ordinal)) rest = channel[artist.Length..];
            else if (channel.EndsWith(artist, StringComparison.Ordinal)) rest = channel[..^artist.Length];
            else if (artist.Length >= 4 && channel.Contains(artist, StringComparison.Ordinal))
            {
                // Named after the artist with words on both sides: somebody's tribute channel.
                kind = ChannelKind.Fan;
                continue;
            }
            if (rest == null) continue;

            switch (ReadWords(rest))
            {
                case Words.Official: return ChannelKind.Artist;
                case Words.Fan: kind = ChannelKind.Fan; break;
            }
        }
        return kind;
    }

    private enum Words { Official, Fan, Unknown }

    /// <summary>Reads glued words ("vevo", "officialmusic", "fanclub") off <paramref name="rest"/>.</summary>
    private static Words ReadWords(string rest)
    {
        var sawFan = false;
        var i = 0;
        while (i < rest.Length)
        {
            var official = OfficialWords.FirstOrDefault(w => string.CompareOrdinal(rest, i, w, 0, w.Length) == 0);
            if (official != null) { i += official.Length; continue; }
            var fan = FanWords.FirstOrDefault(w => string.CompareOrdinal(rest, i, w, 0, w.Length) == 0);
            if (fan != null) { i += fan.Length; sawFan = true; continue; }
            // An unknown word: a fan word anywhere still marks a fan account.
            return FanWords.Any(w => w.Length >= 3 && rest.Contains(w, StringComparison.Ordinal)) ? Words.Fan : Words.Unknown;
        }
        return sawFan ? Words.Fan : Words.Official;
    }

    /// <summary>
    /// True for a frame that fills a wide background: landscape, at least 5:4.
    /// Vertical and square uploads (shorts, reels, phone edits) do not; neither does
    /// a frame of unknown size, which only the caller can decide to give the benefit.
    /// </summary>
    public static bool IsWideFrame(int width, int height)
        => width > 0 && height > 0 && width >= height * 1.25;

    /// <summary>
    /// True when even the richest of a video's streams spends almost nothing on its
    /// picture (bits per pixel per frame below 0.02): a cover image held for the length
    /// of the song. Moving clips measured 0.039 and up, still covers 0.0165.
    /// </summary>
    public static bool IsStillPicture(double maxBitsPerPixel) => maxBitsPerPixel > 0 && maxBitsPerPixel < 0.02;

    /// <summary>
    /// True when a video of <paramref name="clipSeconds"/> can be the music video of a
    /// song of <paramref name="songSeconds"/> (0 = unknown): never under a minute
    /// (teasers, shorts) or over 15 minutes (concerts, mixes), and within 0.7–2.2x
    /// of the song when its length is known (intros and outros stretch a clip).
    /// </summary>
    public static bool IsPlausibleLength(int clipSeconds, int songSeconds)
    {
        if (clipSeconds <= 0) return true;   // unknown here; the other checks decide
        if (clipSeconds < 60 || clipSeconds > 900) return false;
        if (songSeconds <= 0) return true;
        var ratio = (double)clipSeconds / songSeconds;
        return ratio >= 0.7 && ratio <= 2.2;
    }

    private static readonly Regex ShortsMarkers = new(
        @"#shorts?\b|\bshorts?\s*$|\bvertical\b|\b(?:snippet|preview|sneak peek|teaser)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// True for teasers and phone-format uploads: "#shorts", "(Vertical Video)", "snippet".
    /// Words of the song's own name do not count: a song called "Vertical" stays itself.
    /// </summary>
    public static bool IsShortFormTitle(string? title, string? songTitle = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var rest = string.IsNullOrWhiteSpace(songTitle) ? title : RemoveOnce(title, songTitle);
        return ShortsMarkers.IsMatch(rest);
    }

    private static readonly Regex LyricsAbroad = new(
        @"\b(?:letra|paroles|testo|legendad[oa]|tradu[cç][aã]o|traducci[oó]n|traduzione|subtitulad[oa]|sub(?:s)? (?:español|espanol|eng|indo)"
        + @"|lirik|şarkı sözleri|перевод|текст|переклад|субтитр\w*|歌詞|가사|with lyrics)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// True for lyric and translation uploads in other languages ("[Letra en español]",
    /// "перевод", "legendado"): fans' text videos, not clips.
    /// </summary>
    public static bool IsLyricsUploadTitle(string? title, string? songTitle = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var rest = string.IsNullOrWhiteSpace(songTitle) ? title : RemoveOnce(title, songTitle);
        return LyricsAbroad.IsMatch(rest);
    }

    private static readonly Regex VisualizerMarker = new(
        @"\bvisuali[sz]er\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>True for an official visualiser: moving pictures made for the song, the closest thing to a Canvas.</summary>
    public static bool IsVisualizerTitle(string? title)
        => !string.IsNullOrWhiteSpace(title) && VisualizerMarker.IsMatch(title);

    private static readonly Regex VersionMarker = new(
        @"\b(?:piano|acoustic|orchestral|stripped|sped up|alternate|alternative|extended|demo|a cappella|acapella)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// True when the upload is another version of the song ("Lose Control (Piano Version)"),
    /// which only stands in when the plain clip cannot be found. Words of the song's own
    /// name do not count: "Piano Man" is no piano version.
    /// </summary>
    public static bool IsOtherVersionTitle(string? title, string? songTitle)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var rest = string.IsNullOrWhiteSpace(songTitle) ? title : RemoveOnce(title, songTitle);
        return VersionMarker.IsMatch(rest);
    }

    /// <summary>True when <paramref name="text"/> names one of the artists (letters and digits only).</summary>
    public static bool NamesAnyArtist(string? text, IEnumerable<string?> artistNames)
    {
        var norm = ClipTitleRules.Norm(text);
        if (norm.Length == 0) return false;
        return artistNames.Select(ClipTitleRules.Norm).Any(a => a.Length >= 2 && norm.Contains(a, StringComparison.Ordinal));
    }

    private static string RemoveOnce(string text, string part)
    {
        var at = text.IndexOf(part, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? text : string.Concat(text.AsSpan(0, at), " ", text.AsSpan(at + part.Length));
    }
}
