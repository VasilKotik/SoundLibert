namespace SoundLibert.Domain.Models;

public class SongEntity
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ArtistsJson { get; set; } = "[]";
    public string? AlbumTitle { get; set; }
    public string? AlbumId { get; set; }
    public int DurationSeconds { get; set; }
    public string ThumbnailUrl { get; set; } = string.Empty;
    public bool IsExplicit { get; set; }
    public bool IsLiked { get; set; }
    public bool IsYouTubeLiked { get; set; }
    public bool IsSoundLibertLiked { get; set; }

    /// <summary>
    /// The user explicitly unliked a song that is liked on the YouTube account.
    /// While set, account sync keeps <c>IsLiked</c> off so a deleted song does
    /// not reappear after a restart or a re-login. Cleared when the user likes
    /// the song again, or when YouTube itself drops the like.
    /// </summary>
    public bool IsLocalUnliked { get; set; }
    public bool IsLibrary { get; set; }
    public string? LocalPath { get; set; }

    /// <summary>Offline copy path for a downloaded catalogue song (see SongItem.DownloadPath).</summary>
    public string? DownloadPath { get; set; }

    /// <summary>When the offline copy was saved (null = not downloaded).</summary>
    public DateTime? DownloadedAt { get; set; }

    /// <summary>
    /// Scope key for local-file rows (per-account files; '' = device-local).
    /// Streaming cache rows stay unscoped.
    /// </summary>
    public string? AccountKey { get; set; }

    /// <summary>YouTube Music video classification (ATV/OMV/UGC), if known.</summary>
    public string? VideoType { get; set; }

    /// <summary>Resolved official song (ATV) id, cached so discovery is skipped next time.</summary>
    public string? SongVersionId { get; set; }

    /// <summary>Resolved official music-video (OMV) id, cached so discovery is skipped next time.</summary>
    public string? VideoVersionId { get; set; }

    /// <summary>When discovery last found no clip (negative cache); re-check after TTL.</summary>
    public DateTime? ClipMissAt { get; set; }

    /// <summary>Global YouTube view count for this track's video (0 = unknown).</summary>
    public long ViewCount { get; set; }

    /// <summary>When <see cref="ViewCount"/> was last resolved (null = never).</summary>
    public DateTime? ViewCountAt { get; set; }

    public long TotalPlayTimeMs { get; set; }
    public int PlayCount { get; set; }
    public DateTime? LastPlayedAt { get; set; }
    public DateTime? BookmarkedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
