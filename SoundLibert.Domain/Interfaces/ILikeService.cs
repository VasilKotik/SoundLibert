using SoundLibert.Domain.Models;

namespace SoundLibert.Domain.Interfaces;

/// <summary>
/// SoundLibert-local like actions. YouTube Music account likes are imported
/// separately and are read-only; toggling here never writes to that account.
/// <para>
/// A like belongs to the song, not to one of its ids: YouTube Music lists the same
/// song as an album track, a deluxe-edition track and a music video, each with an id
/// of its own. <see cref="IsLiked"/> answers for every version (see
/// <see cref="SongIdentity.Key(SongItem)"/>), and unliking clears them all.
/// </para>
/// </summary>
public interface ILikeService
{
    /// <summary>Raised for every song id whose like changed (id + new state).</summary>
    event EventHandler<LikeChangedEventArgs>? LikeChanged;

    /// <summary>
    /// The likes were read again as a whole (start-up, an account sync, a sign-in or
    /// sign-out): every heart on screen should be asked again.
    /// </summary>
    event EventHandler? LikesReloaded;

    /// <summary>This song, in this version or any other, is liked (from what is loaded; false before).</summary>
    bool IsLiked(SongItem song);

    /// <summary>The likes are loaded (once; later calls return at once).</summary>
    Task EnsureLoadedAsync(CancellationToken ct = default);

    /// <summary>Reads the likes from the library again and raises <see cref="LikesReloaded"/>.</summary>
    Task ReloadAsync(CancellationToken ct = default);

    /// <summary>
    /// Likes the song, or unlikes it in every version it is liked in, and returns
    /// the new state.
    /// </summary>
    Task<bool> SetLikedAsync(SongItem song, bool liked, CancellationToken ct = default);

    /// <summary>Flips the like as the listener sees it (<see cref="IsLiked"/>) and returns its new state.</summary>
    Task<bool> ToggleAsync(SongItem song, CancellationToken ct = default);
}
