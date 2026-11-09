namespace SoundLibert.Domain.Models;

public class PlaylistEntity
{
    /// <summary>
    /// Id prefix for user-created (local, editable) playlists, so they can be
    /// told apart from YouTube Music playlists (which use VL… browse ids).
    /// </summary>
    public const string LocalIdPrefix = "sl:";

    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? BrowseId { get; set; }
    public string? ThumbnailUrl { get; set; }
    public bool IsEditable { get; set; }
    public DateTime? BookmarkedAt { get; set; }
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// Saved into the local library by the user (community playlist), as opposed
    /// to a YouTube-account-imported one. Never written to YouTube; protected
    /// from the account-playlist reconcile.
    /// </summary>
    public bool IsLocalSaved { get; set; }

    /// <summary>Manual position of the card in the library (lower = earlier).</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Owner account of a local playlist (per-account lists: each sign-in sees
    /// its own). Null or empty = device-local scope, visible only while signed
    /// out (the database stores the device-local scope as an empty string).
    /// </summary>
    public string? AccountKey { get; set; }

    /// <summary>Number of tracks in the playlist (0 when unknown / not loaded).</summary>
    public int TrackCount { get; set; }

    /// <summary>Clip rows inside <see cref="TrackCount"/> (same rule as the
    /// Songs/Videos tabs: OMV/UGC type or a distinct video version).</summary>
    public int VideoCount { get; set; }

    public string TrackCountDisplay => TrackCount == 1 ? "1 track" : $"{TrackCount} tracks";

    /// <summary>Card/header text like the Liked tile: clips never count as songs.</summary>
    public string ContentDisplay
    {
        get
        {
            var songs = TrackCount - VideoCount;
            if (VideoCount <= 0) return songs == 1 ? "1 song" : $"{songs} songs";
            if (songs <= 0) return VideoCount == 1 ? "1 video" : $"{VideoCount} videos";
            var s = songs == 1 ? "1 song" : $"{songs} songs";
            var v = VideoCount == 1 ? "1 video" : $"{VideoCount} videos";
            return $"{s} · {v}";
        }
    }

    /// <summary>True when the playlist has a real cover image to show.</summary>
    public bool HasCover => !string.IsNullOrWhiteSpace(ThumbnailUrl);

    /// <summary>
    /// True when a LOCAL playlist belongs to the current scope: each sign-in
    /// sees its own lists, signed-out sees only unscoped (device-local) ones.
    /// Account-imported rows are always in scope (the purge removes them on
    /// sign-out instead).
    /// </summary>
    public static bool InScope(PlaylistEntity? p, string? scopeKey)
    {
        if (p == null || !p.Id.StartsWith(LocalIdPrefix, StringComparison.Ordinal)) return true;
        return string.IsNullOrEmpty(scopeKey)
            ? string.IsNullOrEmpty(p.AccountKey)
            : string.Equals(p.AccountKey, scopeKey, StringComparison.Ordinal);
    }
}
