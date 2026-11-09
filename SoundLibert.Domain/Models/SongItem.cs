using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SoundLibert.Domain.Models;

public class SongItem : YTItem, INotifyPropertyChanged
{
    public new string Id { get; set; } = string.Empty;
    public new string Title { get; set; } = string.Empty;
    public IReadOnlyList<Artist> Artists { get; set; } = Array.Empty<Artist>();
    public string? AlbumTitle { get; set; }
    public string? AlbumId { get; set; }
    private int _durationSeconds;

    /// <summary>Length in seconds (0 = unknown). Notifies, so a late lookup updates rows.</summary>
    public int DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (_durationSeconds == value) return;
            _durationSeconds = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DurationSeconds)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DurationDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DurationBadge)));
        }
    }
    public new string ThumbnailUrl { get; set; } = string.Empty;
    public bool IsExplicit { get; set; }
    public bool IsLibrary { get; set; }

    /// <summary>True for imported local files (MP3/FLAC/WAV/M4A) rather than YouTube items.</summary>
    public bool IsLocal { get; set; }

    /// <summary>Absolute path of the local file when <see cref="IsLocal"/> is true.</summary>
    public string? LocalPath { get; set; }

    /// <summary>
    /// Absolute path of the offline copy when this catalogue song has been
    /// downloaded for offline listening. Unlike <see cref="LocalPath"/> (an
    /// imported local file), a downloaded song stays a catalogue song: online
    /// actions, likes and version discovery keep working.
    /// </summary>
    public string? DownloadPath { get; set; }

    /// <summary>When the offline copy was saved (null when not downloaded).</summary>
    public DateTime? DownloadedAt { get; set; }

    private bool _isDownloaded;

    /// <summary>
    /// True when an offline copy exists. Set from the DB row or hydrated from
    /// the download service for catalogue rows, so list rows can show a badge.
    /// </summary>
    public bool IsDownloaded
    {
        get => _isDownloaded;
        set => SetField(ref _isDownloaded, value);
    }

    /// <summary>
    /// True when this song is shown inside a user-created local playlist,
    /// where reorder/remove actions are available.
    /// </summary>
    public bool IsLocalPlaylist { get; set; }

    /// <summary>
    /// YouTube Music's own classification of this item's video, as returned on
    /// search/browse rows: <c>MUSIC_VIDEO_TYPE_ATV</c> (audio track / song),
    /// <c>MUSIC_VIDEO_TYPE_OMV</c> (official music video) or
    /// <c>MUSIC_VIDEO_TYPE_UGC</c> (user-generated). Null when unknown.
    /// </summary>
    public string? VideoType { get; set; }

    private long _viewCount;

    /// <summary>
    /// Global YouTube view count for this track's video (Spotify-style play
    /// count). Parsed from a Video search row or resolved through the player
    /// endpoint and cached; 0 while unknown.
    /// </summary>
    public long ViewCount
    {
        get => _viewCount;
        set
        {
            if (_viewCount == value) return;
            _viewCount = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ViewCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ViewCountText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasViewCount)));
        }
    }

    /// <summary>True when a global view count is known for this row.</summary>
    public bool HasViewCount => _viewCount > 0;

    /// <summary>"1.2B views", or "7B plays" for an audio track; empty while unknown.</summary>
    public string ViewCountText
    {
        get
        {
            if (_viewCount <= 0) return string.Empty;
            return string.Equals(VideoType, "MUSIC_VIDEO_TYPE_ATV", StringComparison.OrdinalIgnoreCase)
                ? ViewCountFormatting.FormatPlays(_viewCount)
                : ViewCountFormatting.FormatViews(_viewCount);
        }
    }

    /// <summary>
    /// Official song version (ATV audio track) id from YouTube Music. Used by the
    /// Song/Video toggle and as the playback id in Song mode.
    /// </summary>
    public string? SongVersionId { get; set; }

    /// <summary>
    /// Official music-video version (OMV) id from YouTube Music. Drives the
    /// fullscreen Canvas and the Video mode; null when the track has no clip.
    /// </summary>
    public string? VideoVersionId { get; set; }

    private bool _isLiked;
    public bool IsLiked
    {
        get => _isLiked;
        set => SetField(ref _isLiked, value);
    }

    /// <summary>Like imported from the read-only YouTube Music account library.</summary>
    public bool IsYouTubeLiked { get; set; }

    /// <summary>Like saved locally by SoundLibert.</summary>
    public bool IsSoundLibertLiked { get; set; }

    private bool _isCurrent;
    /// <summary>True while this exact item is the one playing (drives list highlight).</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetField(ref _isCurrent, value);
    }

    /// <summary>
    /// True when this row IS a video clip — official/UGC type, or pinned to
    /// its own video id — as opposed to a song that merely has a known clip.
    /// Clips render with a badge and open as video; a song never migrates here
    /// just because its clip was resolved. Single rule for every list.
    /// </summary>
    public bool IsVideoClip
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(VideoType))
                return VideoType.StartsWith("MUSIC_VIDEO_TYPE_OMV", StringComparison.OrdinalIgnoreCase)
                    || VideoType.StartsWith("MUSIC_VIDEO_TYPE_UGC", StringComparison.OrdinalIgnoreCase);
            var songId = string.IsNullOrEmpty(SongVersionId) ? Id : SongVersionId;
            return !string.IsNullOrEmpty(VideoVersionId)
                && string.Equals(VideoVersionId, songId, StringComparison.Ordinal);
        }
    }

    public string ArtistDisplay => string.Join(", ", Artists.Select(a => a.Name));
    // "m:ss" alone wrapped at an hour (a 2:05:00 mix read "5:00").
    public string DurationDisplay => DurationSeconds <= 0
        ? "--:--"
        : DurationSeconds >= 3600
            ? TimeSpan.FromSeconds(DurationSeconds).ToString(@"h\:mm\:ss")
            : TimeSpan.FromSeconds(DurationSeconds).ToString(@"m\:ss");

    /// <summary>The length for the corner of a video card: nothing while unknown.</summary>
    public string DurationBadge => DurationSeconds > 0 ? DurationDisplay : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
