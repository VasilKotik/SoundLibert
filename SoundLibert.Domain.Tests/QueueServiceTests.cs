using SoundLibert.App.Services;
using SoundLibert.Domain.Enums;
using SoundLibert.Domain.Models;

namespace SoundLibert.Domain.Tests;

public class QueueServiceTests
{
    private static SongItem Song(string id) => new() { Id = id, Title = id };

    private static List<SongItem> Songs(params string[] ids) => ids.Select(Song).ToList();

    private static string[] Ids(QueueService queue) => queue.Queue.Select(s => s.Id).ToArray();

    [Fact]
    public void Next_AtTheEnd_StaysPut_WithoutRepeat()
    {
        var queue = new QueueService();
        queue.SetQueue(Songs("a", "b"), 1);
        var changes = 0;
        queue.CurrentTrackChanged += (_, _) => changes++;

        queue.Next();

        Assert.Equal(1, queue.CurrentIndex);
        Assert.Equal(0, changes);
    }

    [Theory]
    [InlineData(RepeatMode.All)]
    [InlineData(RepeatMode.One)]
    public void Next_AtTheEnd_Wraps_WithAnyRepeat(RepeatMode mode)
    {
        var queue = new QueueService { RepeatMode = mode };
        queue.SetQueue(Songs("a", "b"), 1);

        queue.Next();

        Assert.Equal(0, queue.CurrentIndex);
    }

    [Fact]
    public void Next_IsASkip_EvenInRepeatOne()
    {
        var queue = new QueueService { RepeatMode = RepeatMode.One };
        queue.SetQueue(Songs("a", "b"));

        queue.Next();

        Assert.Equal("b", queue.CurrentTrack?.Id);
    }

    [Fact]
    public void ReplayCurrent_RestartsTheSameTrack()
    {
        var queue = new QueueService();
        queue.SetQueue(Songs("a", "b"));
        var changes = 0;
        queue.CurrentTrackChanged += (_, _) => changes++;

        queue.ReplayCurrent();

        Assert.Equal(1, changes);
        Assert.Equal("a", queue.CurrentTrack?.Id);
    }

    [Fact]
    public void SetQueue_AnnouncesAReplacement_BeforeTheOtherEvents()
    {
        var queue = new QueueService();
        var order = new List<string>();
        queue.QueueReplaced += (_, _) => order.Add("replaced");
        queue.QueueChanged += (_, _) => order.Add("changed");
        queue.CurrentTrackChanged += (_, _) => order.Add("track");

        queue.SetQueue(Songs("a"));

        Assert.Equal(new[] { "replaced", "changed", "track" }, order);
    }

    [Fact]
    public void EditingTheQueue_IsNoReplacement()
    {
        var queue = new QueueService();
        queue.SetQueue(Songs("a", "b", "c"));
        var replaced = 0;
        queue.QueueReplaced += (_, _) => replaced++;

        queue.Enqueue(Songs("d"));
        queue.InsertNext(Songs("e"));
        queue.MoveItem(1, 2);
        queue.RemoveAt(1);
        queue.ToggleShuffle();
        queue.Reshuffle();

        Assert.Equal(0, replaced);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddingToAnEmptyQueue_MakesTheFirstTrackCurrent(bool insertNext)
    {
        var queue = new QueueService();
        var changes = 0;
        queue.CurrentTrackChanged += (_, _) => changes++;

        if (insertNext) queue.InsertNext(Songs("a", "b"));
        else queue.Enqueue(Songs("a", "b"));

        Assert.Equal("a", queue.CurrentTrack?.Id);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Shuffle_PutsTheCurrentTrackFirst_SoNothingIsSkipped()
    {
        var queue = new QueueService();
        queue.SetQueue(Songs("a", "b", "c", "d", "e"), 2);

        queue.ToggleShuffle();

        Assert.Equal(0, queue.CurrentIndex);
        Assert.Equal("c", queue.CurrentTrack?.Id);
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, Ids(queue).OrderBy(i => i));
    }

    [Fact]
    public void Unshuffle_RestoresTheOrder_WithTracksAddedMeanwhile()
    {
        var queue = new QueueService();
        queue.SetQueue(Songs("a", "b", "c"), 1);
        queue.ToggleShuffle();
        queue.Enqueue(Songs("d"));
        queue.InsertNext(Songs("e"));

        queue.ToggleShuffle();

        Assert.Equal(new[] { "a", "b", "e", "c", "d" }, Ids(queue));
        Assert.Equal("b", queue.CurrentTrack?.Id);
    }

    [Fact]
    public void Unshuffle_LeavesOutTracksRemovedMeanwhile()
    {
        var queue = new QueueService();
        queue.SetQueue(Songs("a", "b", "c"));
        queue.ToggleShuffle();
        var other = queue.Queue[1];

        queue.RemoveAt(1);
        queue.ToggleShuffle();

        Assert.DoesNotContain(other, queue.Queue);
        Assert.Equal(2, queue.Queue.Count);
    }

    [Fact]
    public void SetQueue_WhileShuffled_PlaysTheChosenTrackFirst()
    {
        var queue = new QueueService();
        queue.ToggleShuffle();

        queue.SetQueue(Songs("a", "b", "c", "d"), 2);

        Assert.Equal("c", queue.CurrentTrack?.Id);
        Assert.Equal(0, queue.CurrentIndex);
    }

    [Fact]
    public void EnableShuffleKeepingOrder_DoesNotTouchARestoredQueue()
    {
        var queue = new QueueService();
        queue.SetQueue(Songs("a", "b", "c"), 1);

        queue.EnableShuffleKeepingOrder();

        Assert.True(queue.IsShuffled);
        Assert.Equal(new[] { "a", "b", "c" }, Ids(queue));
        Assert.Equal(1, queue.CurrentIndex);
    }

    [Theory]
    [InlineData(0, 3, 0)] // before the current one to after it: it shifts left
    [InlineData(3, 0, 2)] // after the current one to before it: it shifts right
    [InlineData(1, 3, 3)] // the current one itself
    [InlineData(2, 3, 1)] // both after it: untouched
    public void MoveItem_KeepsTheSameTrackCurrent(int from, int to, int expectedIndex)
    {
        var queue = new QueueService();
        queue.SetQueue(Songs("a", "b", "c", "d"), 1);

        queue.MoveItem(from, to);

        Assert.Equal("b", queue.CurrentTrack?.Id);
        Assert.Equal(expectedIndex, queue.CurrentIndex);
    }

    [Fact]
    public void RemovingTheCurrentTrack_MovesToWhatTookItsPlace()
    {
        var queue = new QueueService();
        queue.SetQueue(Songs("a", "b", "c"), 1);
        var changes = 0;
        queue.CurrentTrackChanged += (_, _) => changes++;

        queue.RemoveAt(1);

        Assert.Equal("c", queue.CurrentTrack?.Id);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void RemovingAnotherTrack_KeepsTheCurrentOne_WithoutRestartingIt()
    {
        var queue = new QueueService();
        queue.SetQueue(Songs("a", "b", "c"), 1);
        var changes = 0;
        queue.CurrentTrackChanged += (_, _) => changes++;

        queue.RemoveAt(0);

        Assert.Equal("b", queue.CurrentTrack?.Id);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void RepeatModeChange_IsNoQueueChange()
    {
        var queue = new QueueService();
        queue.SetQueue(Songs("a"));
        var changes = 0;
        queue.QueueChanged += (_, _) => changes++;

        queue.CycleRepeatMode();

        Assert.Equal(RepeatMode.All, queue.RepeatMode);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Previous_AtTheStart_StaysOnTheFirstTrack_UnlessRepeatingAll()
    {
        var queue = new QueueService();
        queue.SetQueue(Songs("a", "b"));
        queue.Previous();
        Assert.Equal(0, queue.CurrentIndex);

        queue.RepeatMode = RepeatMode.All;
        queue.Previous();
        Assert.Equal(1, queue.CurrentIndex);
    }

    [Fact]
    public void DuplicateTracks_AreToldApartByReference()
    {
        var queue = new QueueService();
        var twin = Song("a");
        queue.SetQueue(new[] { Song("a"), Song("b"), twin }, 2);
        queue.ToggleShuffle();

        queue.ToggleShuffle();

        Assert.Same(twin, queue.CurrentTrack);
        Assert.Equal(2, queue.CurrentIndex);
    }
}
