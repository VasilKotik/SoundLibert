using SoundLibert.Domain.Models;

namespace SoundLibert.Domain.Interfaces;

/// <summary>Lifecycle of one offline download.</summary>
public enum DownloadState
{
    None,
    Queued,
    Downloading,
    Downloaded,
    Failed
}

public sealed class DownloadChangedEventArgs : EventArgs
{
    public string SongId { get; init; } = string.Empty;
    public DownloadState State { get; init; }

    /// <summary>0..1 while downloading; -1 when the total size is unknown.</summary>
    public double Progress { get; init; }

    public string? Path { get; init; }
}

/// <summary>
/// Downloads catalogue songs for offline listening: resolves the audio stream,
/// saves it under the app data folder and records the path, so playback can
/// use the local file with no network. Downloads run one at a time, in a queue.
/// </summary>
public interface IDownloadService
{
    /// <summary>True when an offline copy exists and its file is still on disk.</summary>
    bool IsDownloaded(string songId);

    /// <summary>True while the song is queued or actively downloading.</summary>
    bool IsBusy(string songId);

    DownloadState GetState(string songId);

    /// <summary>0..1 while downloading; -1 when unknown / not downloading.</summary>
    double GetProgress(string songId);

    /// <summary>Loads recorded downloads and starts the worker (idempotent).</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Gets the offline file path for a song when one is available.</summary>
    bool TryGetPath(string songId, out string? path);

    /// <summary>
    /// The id an offline copy of this song is kept under: its own, one of its known
    /// versions', or another version's of the same song (an album page lists the
    /// music video's id; the copy was saved from the audio track). Null when none.
    /// </summary>
    string? CopyOf(SongItem song);

    /// <summary>An offline copy of this song exists, under whichever id (see <see cref="CopyOf"/>).</summary>
    bool HasCopy(SongItem song) => CopyOf(song) != null;

    /// <summary>Queues a catalogue song for offline download (idempotent).</summary>
    Task DownloadAsync(SongItem song, CancellationToken ct = default);

    /// <summary>Deletes the offline copy (the catalogue row itself is kept).</summary>
    Task RemoveAsync(string songId, CancellationToken ct = default);

    /// <summary>All downloaded songs, newest first.</summary>
    Task<IReadOnlyList<SongItem>> GetDownloadedAsync(CancellationToken ct = default);

    /// <summary>The folder offline copies are stored in.</summary>
    string FolderPath { get; }

    /// <summary>
    /// Moves every existing offline copy into the currently configured folder
    /// (used after the download folder setting changes, so old copies are not
    /// left behind). Files already in the folder are left alone.
    /// </summary>
    Task MoveExistingAsync(CancellationToken ct = default);

    /// <summary>Count and total size (bytes) of the offline copies on disk.</summary>
    Task<(int Count, long Bytes)> GetStatsAsync(CancellationToken ct = default);

    /// <summary>Deletes every offline copy (songs themselves are kept).</summary>
    Task ClearAllAsync(CancellationToken ct = default);

    /// <summary>Raised on queue/progress/completion/failure so the UI can update.</summary>
    event EventHandler<DownloadChangedEventArgs>? Changed;
}
