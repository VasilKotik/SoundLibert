namespace SoundLibert.Domain.Models;

/// <summary>
/// A like toggle finished: every list showing this song id must refresh its
/// heart. Song rows across screens are separate objects, so without this the
/// row you toggled updates but all its twins keep a stale heart (and the next
/// tap on a stale twin toggles "backwards").
///
/// Implementation note: only MainWindowViewModel subscribes (it can reach every
/// list); transient page VMs are deliberately left out so navigation cannot
/// leak subscriptions.
/// </summary>
public sealed class LikeChangedEventArgs : EventArgs
{
    public LikeChangedEventArgs(string songId, bool isLiked)
    {
        SongId = songId;
        IsLiked = isLiked;
    }

    public string SongId { get; }
    public bool IsLiked { get; }
}
