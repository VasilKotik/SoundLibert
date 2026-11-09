namespace SoundLibert.Domain.Models;

/// <summary>One track in a persisted session (lightweight — no full SongItem).</summary>
public class SessionTrack
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    /// <summary>The artists as one display string (sessions saved before <see cref="Artists"/>).</summary>
    public string? Artist { get; set; }

    /// <summary>The artists, separately: a joined string written back to the
    /// database would turn "A, B" into one artist in the stats and on Home.</summary>
    public List<Artist>? Artists { get; set; }
    public string? AlbumTitle { get; set; }
    public string? AlbumId { get; set; }
    public bool IsExplicit { get; set; }
    public string? ThumbnailUrl { get; set; }
    public bool IsLocal { get; set; }
    public string? LocalPath { get; set; }
    public int DurationSeconds { get; set; }

    /// <summary>Resolved official versions + classification, so a restored
    /// video queue still knows its clips (otherwise everything replays as songs).</summary>
    public string? SongVersionId { get; set; }
    public string? VideoVersionId { get; set; }
    public string? VideoType { get; set; }

    /// <summary>True when the pins above were explicitly chosen (video queue)
    /// and must survive a restore; guessed pins are dropped on load.</summary>
    public bool KeepPins { get; set; }
}

/// <summary>The previous playback session (queue, current index, position).</summary>
public class SessionState
{
    public List<SessionTrack> Queue { get; set; } = new();
    public int Index { get; set; }
    public double PositionSeconds { get; set; }

    /// <summary>True when the saved queue was watched as video clips.</summary>
    public bool IsVideoQueue { get; set; }

    public static SessionTrack FromSong(SongItem song, bool keepPins = false) => new()
    {
        Id = song.Id,
        Title = song.Title,
        Artist = song.ArtistDisplay,
        Artists = song.Artists.ToList(),
        AlbumTitle = song.AlbumTitle,
        AlbumId = song.AlbumId,
        IsExplicit = song.IsExplicit,
        ThumbnailUrl = song.ThumbnailUrl,
        IsLocal = song.IsLocal,
        LocalPath = song.LocalPath,
        DurationSeconds = song.DurationSeconds,
        SongVersionId = song.SongVersionId,
        VideoVersionId = song.VideoVersionId,
        VideoType = song.VideoType,
        KeepPins = keepPins
    };

    public static SongItem ToSong(SessionTrack t) => ToSong(t, t.KeepPins);

    public static SongItem ToSong(SessionTrack t, bool keepPins) => new()
    {
        Id = t.Id,
        Title = t.Title,
        Artists = t.Artists is { Count: > 0 }
            ? t.Artists.ToArray()
            : string.IsNullOrWhiteSpace(t.Artist)
                ? Array.Empty<Artist>()
                : new[] { new Artist { Name = t.Artist! } },
        AlbumTitle = t.AlbumTitle,
        AlbumId = t.AlbumId,
        IsExplicit = t.IsExplicit,
        ThumbnailUrl = t.ThumbnailUrl ?? string.Empty,
        IsLocal = t.IsLocal,
        LocalPath = t.LocalPath,
        DurationSeconds = t.DurationSeconds,
        // Video queues keep their pins: every pin there was explicitly chosen
        // (PlayAsVideo / video card) or hardened-verified, so dropping them
        // replays the queue as songs. Song queues keep only trivially-correct
        // pins — a guessed cross-id pin may be stale-wrong and would replay a
        // random video; dropping it forces a fresh hardened discovery on play.
        SongVersionId = keepPins || t.SongVersionId == t.Id ? t.SongVersionId : null,
        VideoVersionId = keepPins || t.VideoVersionId == t.Id ? t.VideoVersionId : null,
        VideoType = t.VideoType
    };
}
