using System.Text;
using System.Text.RegularExpressions;

namespace SoundLibert.Domain.Models;

/// <summary>
/// Compares two catalogue items as the SAME underlying track. YouTube Music
/// returns one song under several ids (ATV audio, OMV video), so playlist
/// membership must not treat those as different tracks — otherwise a playlist
/// shows one song twice and deleting one copy looks like the deleted song
/// reappears.
/// </summary>
public static partial class SongIdentity
{
    public static bool SameTrack(SongItem? a, SongItem? b)
    {
        if (a is null || b is null) return false;
        if (IdsMatch(a.Id, b.Id)) return true;
        if (IdsMatch(a.VideoVersionId, b.VideoVersionId)) return true;
        if (IdsMatch(a.SongVersionId, b.SongVersionId)) return true;

        // Version ids may be unknown for catalogue rows: fall back to metadata.
        return !string.IsNullOrWhiteSpace(a.Title)
               && string.Equals(Normalize(a.Title), Normalize(b.Title), StringComparison.OrdinalIgnoreCase)
               && string.Equals(Normalize(a.ArtistDisplay), Normalize(b.ArtistDisplay), StringComparison.OrdinalIgnoreCase);
    }

    public static bool SameTrack(SongItem? song, SongEntity? entity)
        => song is not null && entity is not null && SameTrack(song, entity.ToSongItem());

    /// <summary>
    /// The same comparison against a whole list at once: building the set and
    /// asking it stays fast on a library of thousands of rows, where comparing
    /// every pair froze the window.
    /// </summary>
    public sealed class TrackSet
    {
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private readonly HashSet<string> _videoVersions = new(StringComparer.Ordinal);
        private readonly HashSet<string> _songVersions = new(StringComparer.Ordinal);
        private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

        public TrackSet(IEnumerable<SongItem> songs)
        {
            foreach (var song in songs)
            {
                if (!string.IsNullOrWhiteSpace(song.Id)) _ids.Add(song.Id);
                if (!string.IsNullOrWhiteSpace(song.VideoVersionId)) _videoVersions.Add(song.VideoVersionId);
                if (!string.IsNullOrWhiteSpace(song.SongVersionId)) _songVersions.Add(song.SongVersionId);
                if (!string.IsNullOrWhiteSpace(song.Title)) _names.Add(NameKey(song));
            }
        }

        public bool Contains(SongItem song)
            => (!string.IsNullOrWhiteSpace(song.Id) && _ids.Contains(song.Id))
               || (!string.IsNullOrWhiteSpace(song.VideoVersionId) && _videoVersions.Contains(song.VideoVersionId))
               || (!string.IsNullOrWhiteSpace(song.SongVersionId) && _songVersions.Contains(song.SongVersionId))
               || (!string.IsNullOrWhiteSpace(song.Title) && _names.Contains(NameKey(song)));

        private static string NameKey(SongItem song)
            => Normalize(song.Title) + "\0" + Normalize(song.ArtistDisplay);
    }

    private static bool IdsMatch(string? x, string? y)
        => !string.IsNullOrWhiteSpace(x)
           && !string.IsNullOrWhiteSpace(y)
           && string.Equals(x, y, StringComparison.Ordinal);

    private static string Normalize(string value) => value.Trim();

    // ---- One song across lists (likes, now playing) ----

    /// <summary>
    /// The song behind a row, whichever version the row carries: the album's audio, the
    /// deluxe edition's, the music video an album page lists. The title and the FIRST
    /// artist (a feature credit varies between editions), with case, curly quotes, spacing
    /// and a clip's "(Official Video)" set aside. Empty when the title or the artist is
    /// unknown, and for files on this device: then only the id counts.
    /// </summary>
    public static string Key(SongItem song)
    {
        if (song.IsLocal) return string.Empty;
        var title = song.Title;
        var artists = song.Artists;
        if (song.IdentityKey is { } kept
            && ReferenceEquals(song.IdentityKeyTitle, title)
            && ReferenceEquals(song.IdentityKeyArtists, artists))
            return kept;
        var key = Key(title, artists.Count > 0 ? artists[0].Name : null);
        song.IdentityKeyTitle = title;
        song.IdentityKeyArtists = artists;
        song.IdentityKey = key;
        return key;
    }

    /// <inheritdoc cref="Key(SongItem)"/>
    public static string Key(string? title, string? artist)
    {
        var who = Clean(artist);
        if (who.Length == 0) return string.Empty;
        var what = Clean(title);
        // A clip's title often repeats the artist: "Imagine Dragons - Believer".
        if (what.StartsWith(who + " - ", StringComparison.Ordinal)) what = what[(who.Length + 3)..];
        what = VideoSuffix().Replace(what, string.Empty).Trim();
        return what.Length == 0 ? string.Empty : what + "\u001f" + who;
    }

    /// <summary>The same song: one id, one of its known versions, or the same <see cref="Key(SongItem)"/>.</summary>
    public static bool SameSong(SongItem? a, SongItem? b)
    {
        if (a is null || b is null) return false;
        if (ReferenceEquals(a, b) || IdsMatch(a.Id, b.Id)) return true;
        if (IdsMatch(a.SongVersionId, b.Id) || IdsMatch(a.VideoVersionId, b.Id)
            || IdsMatch(b.SongVersionId, a.Id) || IdsMatch(b.VideoVersionId, a.Id)) return true;
        var key = Key(a);
        return key.Length > 0 && string.Equals(key, Key(b), StringComparison.Ordinal);
    }

    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var normal = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normal.Length);
        var space = false;
        foreach (var c in normal)
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }
            if (space) builder.Append(' ');
            space = false;
            builder.Append(c switch
            {
                '‘' or '’' or 'ʼ' or '`' or '´' => '\'',
                '“' or '”' or '„' => '"',
                '–' or '—' or '−' => '-',
                _ => c
            });
        }
        return builder.ToString();
    }

    /// <summary>"(Official Video)", "[Official Music Video]", "(Lyric Video)", "(Audio)", "(Visualizer)" at the end.</summary>
    [GeneratedRegex(@"\s*[(\[]\s*(?:official\s+)?(?:music\s+|lyrics?\s+|hd\s+)?(?:video|audio|visuali[sz]er|lyrics?)(?:\s+hd)?\s*[)\]]\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VideoSuffix();
}
