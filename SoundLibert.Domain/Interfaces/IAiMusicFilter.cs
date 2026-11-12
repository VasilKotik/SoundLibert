using SoundLibert.Domain.Models;

namespace SoundLibert.Domain.Interfaces;

/// <summary>
/// Tells AI-generated acts apart, so that recommendations leave them out while search, the
/// library and the pages a listener opens still show them. Knows a community list of AI
/// artists, a few words that give a track away, and the listener's own word, which wins.
/// </summary>
public interface IAiMusicFilter
{
    /// <summary>The listener wants AI music out of recommendations.</summary>
    bool Enabled { get; }

    /// <summary>How many AI artists the community list holds (0 until it has loaded).</summary>
    int KnownCount { get; }

    /// <summary>How many artists the listener has called AI or not AI.</summary>
    int MarkedCount { get; }

    bool IsAi(SongItem song);

    bool IsAi(ShelfItem item);

    bool IsAi(ArtistItem artist);

    /// <summary>An artist by name; <paramref name="reach"/> is how widely their music is heard, when known.</summary>
    bool IsAiArtist(string? name, long? reach = null);

    /// <summary>What the listener said about an artist: AI (true), not AI (false), nothing (null).</summary>
    bool? MarkOf(string artistName);

    /// <summary>Records the listener's word about an artist (null forgets it).</summary>
    void Mark(string artistName, bool? isAi);

    /// <summary>Forgets everything the listener said.</summary>
    void ClearMarks();

    /// <summary>The list is loaded: from this device, or downloaded when there is none (briefly awaited).</summary>
    Task EnsureReadyAsync(CancellationToken ct = default);

    /// <summary>The list or the listener's word changed: what is recommended may change too.</summary>
    event Action? Changed;
}
