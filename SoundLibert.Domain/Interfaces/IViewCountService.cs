using SoundLibert.Domain.Models;

namespace SoundLibert.Domain.Interfaces;

/// <summary>
/// Shows Spotify-style global play counts on catalogue rows. Cached counts are
/// hydrated from SQLite instantly; unknown ones are resolved through the
/// provider (bounded + concurrent) and written back to the rows and the cache.
/// </summary>
public interface IViewCountService
{
    /// <summary>
    /// Fills cached view counts on the rows from SQLite (no network). Fast, so
    /// it can be awaited while a list is built.
    /// </summary>
    Task HydrateAsync(IReadOnlyList<SongItem> songs, CancellationToken ct = default);

    /// <summary>
    /// Resolves the still-unknown view counts (bounded concurrency, capped per
    /// call) and updates both the rows and the SQLite cache. Safe to run in the
    /// background after a list is shown.
    /// </summary>
    Task ResolveMissingAsync(IReadOnlyList<SongItem> songs, CancellationToken ct = default);
}
