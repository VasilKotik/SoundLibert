namespace SoundLibert.Domain.Interfaces;

/// <summary>
/// Imports local audio files (MP3/FLAC/WAV/M4A…) into the library so they sit
/// alongside streaming tracks in the same UI.
/// </summary>
public interface ILocalLibrary
{
    /// <summary>Scans a folder (recursively) and upserts the supported audio files. Returns how many were imported/updated.</summary>
    Task<int> ImportFolderAsync(string folder, CancellationToken ct = default);
}

/// <summary>Reads media metadata from a local file (duration).</summary>
public interface IMediaProbe
{
    /// <summary>Returns the track length in seconds, or 0 when it cannot be read.</summary>
    int GetDurationSeconds(string path);
}
