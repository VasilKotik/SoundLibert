using SoundLibert.Domain.Enums;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;

namespace SoundLibert.App.Services;

/// <summary>
/// The play queue. <c>_queue</c> is the order tracks play in; <c>_originalOrder</c>
/// holds the same items in the order they were queued, which un-shuffling
/// restores. Every change that adds or removes a track updates both.
/// Used from the UI thread only.
/// </summary>
internal class QueueService : IQueueService
{
    private readonly List<SongItem> _queue = new();
    private readonly List<SongItem> _originalOrder = new();
    private int _currentIndex = -1;
    private bool _isShuffled;
    private RepeatMode _repeatMode = RepeatMode.Off;

    public IReadOnlyList<SongItem> Queue => _queue;
    public int CurrentIndex => _currentIndex;
    public SongItem? CurrentTrack => _currentIndex >= 0 && _currentIndex < _queue.Count ? _queue[_currentIndex] : null;
    public bool IsShuffled => _isShuffled;
    public RepeatMode RepeatMode
    {
        get => _repeatMode;
        set => _repeatMode = value;
    }

    public event EventHandler? QueueReplaced;
    public event EventHandler? QueueChanged;
    public event EventHandler? CurrentTrackChanged;

    public void SetQueue(IReadOnlyList<SongItem> tracks, int startIndex = 0)
    {
        _originalOrder.Clear();
        _originalOrder.AddRange(tracks);
        _queue.Clear();
        _queue.AddRange(tracks);
        _currentIndex = tracks.Count == 0 ? -1 : Math.Clamp(startIndex, 0, tracks.Count - 1);
        // Shuffle stays on across queues: the chosen track plays first, the rest
        // in a new random order.
        if (_isShuffled) ShuffleKeepingCurrentFirst();

        QueueReplaced?.Invoke(this, EventArgs.Empty);
        QueueChanged?.Invoke(this, EventArgs.Empty);
        CurrentTrackChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Enqueue(IReadOnlyList<SongItem> tracks)
    {
        if (tracks.Count == 0) return;
        _queue.AddRange(tracks);
        _originalOrder.AddRange(tracks);
        AfterTracksAdded();
    }

    public void InsertNext(IReadOnlyList<SongItem> tracks)
    {
        if (tracks.Count == 0) return;
        if (_currentIndex >= 0)
        {
            var current = _queue[_currentIndex];
            _queue.InsertRange(_currentIndex + 1, tracks);
            var inOriginal = IndexOfReference(_originalOrder, current);
            _originalOrder.InsertRange(inOriginal < 0 ? _originalOrder.Count : inOriginal + 1, tracks);
        }
        else
        {
            _queue.AddRange(tracks);
            _originalOrder.AddRange(tracks);
        }
        AfterTracksAdded();
    }

    private void AfterTracksAdded()
    {
        QueueChanged?.Invoke(this, EventArgs.Empty);
        // Nothing was queued before: otherwise the player has no current track
        // and its Play button can do nothing with the new tracks.
        if (_currentIndex < 0 && _queue.Count > 0)
        {
            _currentIndex = 0;
            CurrentTrackChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        var removedCurrent = index == _currentIndex;
        var removed = _queue[index];
        _queue.RemoveAt(index);
        var inOriginal = IndexOfReference(_originalOrder, removed);
        if (inOriginal >= 0) _originalOrder.RemoveAt(inOriginal);

        if (index < _currentIndex) _currentIndex--;
        if (_currentIndex >= _queue.Count) _currentIndex = _queue.Count - 1;
        QueueChanged?.Invoke(this, EventArgs.Empty);
        // Deleting the playing row must move playback to whatever took its
        // place (or to null when the queue emptied): otherwise the player
        // keeps referencing a detached track while the index points elsewhere.
        if (removedCurrent)
            CurrentTrackChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MoveItem(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= _queue.Count) return;
        if (toIndex < 0 || toIndex >= _queue.Count) return;
        if (fromIndex == toIndex) return;

        var item = _queue[fromIndex];
        _queue.RemoveAt(fromIndex);
        _queue.Insert(toIndex, item);

        // Keep the SAME track as the current one. Moving an item across the
        // current position shifts it, so _currentIndex must follow:
        //  - moving the current item itself → it lands at toIndex;
        //  - moving an item from before the current one to after it → current shifts left by 1;
        //  - moving an item from after the current one to before it → current shifts right by 1.
        if (_currentIndex == fromIndex)
            _currentIndex = toIndex;
        else if (fromIndex < _currentIndex && toIndex >= _currentIndex)
            _currentIndex--;
        else if (fromIndex > _currentIndex && toIndex <= _currentIndex)
            _currentIndex++;

        // Without shuffle the order the user arranges IS the queue's order, so
        // turning shuffle on and off later must bring it back.
        if (!_isShuffled)
        {
            _originalOrder.Clear();
            _originalOrder.AddRange(_queue);
        }

        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _queue.Clear();
        _originalOrder.Clear();
        _currentIndex = -1;
        QueueReplaced?.Invoke(this, EventArgs.Empty);
        QueueChanged?.Invoke(this, EventArgs.Empty);
        CurrentTrackChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Next()
    {
        if (_queue.Count == 0) return;

        if (_currentIndex + 1 < _queue.Count)
            _currentIndex++;
        else if (_repeatMode != RepeatMode.Off)
            _currentIndex = 0;
        else
            return;

        CurrentTrackChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ReplayCurrent()
    {
        if (CurrentTrack != null)
            CurrentTrackChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Previous()
    {
        if (_queue.Count == 0) return;
        _currentIndex--;
        if (_currentIndex < 0)
        {
            _currentIndex = _repeatMode == RepeatMode.All ? _queue.Count - 1 : 0;
        }
        CurrentTrackChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetCurrentIndex(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        _currentIndex = index;
        CurrentTrackChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Index of the SAME queue item (by reference, not id) so duplicate tracks
    /// (same video id added twice) never make the player jump to a different copy.
    /// </summary>
    private static int IndexOfReference(List<SongItem> list, SongItem item)
    {
        for (var i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], item)) return i;
        return -1;
    }

    /// <summary>
    /// The current track stays current and goes first, so every other track is
    /// still ahead of it. At a random position, everything shuffled in front of
    /// it would simply never play.
    /// </summary>
    private void ShuffleKeepingCurrentFirst()
    {
        var current = CurrentTrack;
        var others = new List<SongItem>(_queue);
        if (current != null) others.RemoveAt(_currentIndex);
        var shuffled = others.ToArray();
        Random.Shared.Shuffle(shuffled);

        _queue.Clear();
        if (current != null) _queue.Add(current);
        _queue.AddRange(shuffled);
        _currentIndex = current != null ? 0 : -1;
    }

    public void ToggleShuffle()
    {
        _isShuffled = !_isShuffled;
        if (_isShuffled)
        {
            ShuffleKeepingCurrentFirst();
        }
        else
        {
            var current = CurrentTrack;
            _queue.Clear();
            _queue.AddRange(_originalOrder);
            if (current != null)
            {
                var index = IndexOfReference(_queue, current);
                _currentIndex = index >= 0 ? index : Math.Min(_currentIndex, _queue.Count - 1);
            }
        }
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void EnableShuffleKeepingOrder() => _isShuffled = true;

    public void CycleRepeatMode()
    {
        _repeatMode = _repeatMode switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            RepeatMode.One => RepeatMode.Off,
            _ => RepeatMode.Off
        };
    }

    /// <summary>Reshuffles the queue while keeping the current track (Spotify-style).</summary>
    public void Reshuffle()
    {
        if (_queue.Count <= 1) return;
        ShuffleKeepingCurrentFirst();
        _isShuffled = true;
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }
}
