namespace SoundLibert.Domain.Interfaces;

/// <summary>
/// Resolves an artist's music genre from an external, keyless catalogue
/// (iTunes Search API), cached locally so a playlist's genres are computed
/// once. Used to build "genre mixes" out of the user's own playlist songs.
/// </summary>
public interface IGenreService
{
    /// <summary>
    /// The artist's canonical genre (e.g. "Rock", "Hip-Hop/Rap"), or null when
    /// it cannot be resolved. Results — including "unknown" — are cached.
    /// </summary>
    Task<string?> GetArtistGenreAsync(string artistName, CancellationToken ct = default);

    /// <summary>
    /// The genre of one specific track, resolved from a song-level lookup
    /// (<paramref name="title"/> + <paramref name="artistName"/>) so it reflects
    /// the track rather than the artist. Falls back to the artist genre when the
    /// song cannot be matched. Results — including "unknown" — are cached per
    /// <paramref name="songId"/>.
    /// </summary>
    Task<string?> GetSongGenreAsync(string songId, string title, string artistName, CancellationToken ct = default);
}
