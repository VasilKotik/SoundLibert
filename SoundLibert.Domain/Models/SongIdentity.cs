namespace SoundLibert.Domain.Models;

/// <summary>
/// Compares two catalogue items as the SAME underlying track. YouTube Music
/// returns one song under several ids (ATV audio, OMV video), so playlist
/// membership must not treat those as different tracks — otherwise a playlist
/// shows one song twice and deleting one copy looks like the deleted song
/// reappears.
/// </summary>
public static class SongIdentity
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
}
