namespace SoundLibert.Domain.Models;

/// <summary>
/// The official YouTube Music versions of a track — the song (an audio track,
/// <c>MUSIC_VIDEO_TYPE_ATV</c>) and the music video (<c>MUSIC_VIDEO_TYPE_OMV</c>).
/// Mirrors YouTube Music's own Song / Video switch. Every id here comes from the
/// official <c>next</c> payload; nothing is guessed from search results.
/// </summary>
public sealed class TrackVersions
{
    /// <summary>Official song / audio-track version id (ATV).</summary>
    public string? SongId { get; init; }

    /// <summary>Official music-video version id (OMV), when one exists.</summary>
    public string? VideoId { get; init; }

    /// <summary>True when the queried id is itself the music video.</summary>
    public bool CurrentIsVideo { get; init; }

    /// <summary>True when an official music video exists for this track.</summary>
    public bool HasVideo => !string.IsNullOrEmpty(VideoId);
}
