using SoundLibert.Domain.Models;

namespace SoundLibert.Domain.Interfaces;

/// <summary>
/// SoundLibert-local like actions. YouTube Music account likes are imported
/// separately and are read-only; toggling here never writes to that account.
/// </summary>
public interface ILikeService
{
    /// <summary>Raised after every successful toggle (song id + new state).</summary>
    event EventHandler<LikeChangedEventArgs>? LikeChanged;

    /// <summary>Toggles the SoundLibert-local like and returns its new state.</summary>
    Task<bool> ToggleAsync(SongItem song, CancellationToken ct = default);
}
