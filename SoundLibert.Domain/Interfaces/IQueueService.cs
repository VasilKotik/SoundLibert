using SoundLibert.Domain.Enums;
using SoundLibert.Domain.Models;

namespace SoundLibert.Domain.Interfaces;

public interface IQueueService
{
    IReadOnlyList<SongItem> Queue { get; }
    int CurrentIndex { get; }
    SongItem? CurrentTrack { get; }
    bool IsShuffled { get; }
    RepeatMode RepeatMode { get; set; }

    /// <summary>
    /// A whole new queue was set or the queue was cleared. Raised before
    /// <see cref="QueueChanged"/> and <see cref="CurrentTrackChanged"/>. Edits to
    /// the existing queue (add, remove, move, shuffle) do not raise it.
    /// </summary>
    event EventHandler? QueueReplaced;

    /// <summary>The list of tracks or their order changed in any way.</summary>
    event EventHandler? QueueChanged;

    event EventHandler? CurrentTrackChanged;

    void SetQueue(IReadOnlyList<SongItem> tracks, int startIndex = 0);

    /// <summary>Adds tracks to the end. On an empty queue the first one becomes current.</summary>
    void Enqueue(IReadOnlyList<SongItem> tracks);

    /// <summary>Inserts tracks right after the current one. On an empty queue the first one becomes current.</summary>
    void InsertNext(IReadOnlyList<SongItem> tracks);

    void RemoveAt(int index);
    void MoveItem(int fromIndex, int toIndex);
    void Clear();

    /// <summary>
    /// Moves to the next track; past the last one it wraps with any repeat mode
    /// on, and otherwise stays put without raising an event. This is a skip: it
    /// leaves the track even in Repeat One (see <see cref="ReplayCurrent"/>).
    /// </summary>
    void Next();

    /// <summary>Starts the current track over (Repeat One at the end of a track).</summary>
    void ReplayCurrent();

    void Previous();
    void SetCurrentIndex(int index);
    void ToggleShuffle();

    /// <summary>Turns shuffle on for a queue that is already in its playing order (a restored one).</summary>
    void EnableShuffleKeepingOrder();

    void Reshuffle();
    void CycleRepeatMode();
}
