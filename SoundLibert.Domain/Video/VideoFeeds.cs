using SoundLibert.Domain.Models;

namespace SoundLibert.Domain.Video;

/// <summary>
/// A list of videos read a page at a time: an artist's channel, what is new, a mix of
/// those. A failed read leaves the feed where it was, so asking again retries that page.
/// </summary>
public interface IVideoFeed
{
    /// <summary>False once there is nothing more to read.</summary>
    bool HasMore { get; }

    /// <summary>The next videos. Can be empty while <see cref="HasMore"/> stays true.</summary>
    Task<IReadOnlyList<SongItem>> NextPageAsync(CancellationToken ct = default);
}

/// <summary>A feed one read long: a list already at hand, or a single request.</summary>
public sealed class SinglePageVideoFeed : IVideoFeed
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<SongItem>>> _read;

    public SinglePageVideoFeed(IReadOnlyList<SongItem> videos) : this(_ => Task.FromResult(videos)) { }

    public SinglePageVideoFeed(Func<CancellationToken, Task<IReadOnlyList<SongItem>>> read) => _read = read;

    public bool HasMore { get; private set; } = true;

    public async Task<IReadOnlyList<SongItem>> NextPageAsync(CancellationToken ct = default)
    {
        if (!HasMore) return Array.Empty<SongItem>();
        var videos = await _read(ct);
        HasMore = false;
        return videos;
    }
}

/// <summary>One feed after another.</summary>
public sealed class ConcatVideoFeed : IVideoFeed
{
    private readonly Queue<IVideoFeed> _feeds;

    public ConcatVideoFeed(params IVideoFeed[] feeds) => _feeds = new Queue<IVideoFeed>(feeds);

    public bool HasMore
    {
        get
        {
            while (_feeds.Count > 0 && !_feeds.Peek().HasMore) _feeds.Dequeue();
            return _feeds.Count > 0;
        }
    }

    public Task<IReadOnlyList<SongItem>> NextPageAsync(CancellationToken ct = default)
        => HasMore ? _feeds.Peek().NextPageAsync(ct) : Task.FromResult<IReadOnlyList<SongItem>>(Array.Empty<SongItem>());
}

/// <summary>Keeps only the videos a rule accepts.</summary>
public sealed class FilteredVideoFeed : IVideoFeed
{
    private readonly IVideoFeed _feed;
    private readonly Func<SongItem, bool> _keep;

    public FilteredVideoFeed(IVideoFeed feed, Func<SongItem, bool> keep)
    {
        _feed = feed;
        _keep = keep;
    }

    public bool HasMore => _feed.HasMore;

    public async Task<IReadOnlyList<SongItem>> NextPageAsync(CancellationToken ct = default)
        => (await _feed.NextPageAsync(ct)).Where(_keep).ToList();
}

/// <summary>
/// A few videos from each feed in turn, so a page of a hundred clips from one artist does
/// not bury the others. One feed failing does not stop the rest: it sits the round out and
/// is asked again on the next, and it is let go once it fails twice while others work.
/// Only a round where nothing works at all (no network) is reported as a failure.
/// </summary>
public sealed class MixedVideoFeed : IVideoFeed
{
    private const int FailuresToDrop = 2;

    private sealed class Source(IVideoFeed feed)
    {
        public IVideoFeed Feed { get; } = feed;
        public Queue<SongItem> Ready { get; } = new();
        public int Failures { get; set; }
    }

    private readonly List<Source> _sources;
    private readonly int _takeFromEach;

    public MixedVideoFeed(IEnumerable<IVideoFeed> feeds, int takeFromEach = 3)
    {
        _sources = feeds.Select(f => new Source(f)).ToList();
        _takeFromEach = Math.Max(1, takeFromEach);
    }

    public bool HasMore => _sources.Any(s => s.Ready.Count > 0 || s.Feed.HasMore);

    public async Task<IReadOnlyList<SongItem>> NextPageAsync(CancellationToken ct = default)
    {
        // Every source that ran dry refills side by side.
        var dry = _sources.Where(s => s.Ready.Count == 0 && s.Feed.HasMore).ToList();
        var outcomes = await Task.WhenAll(dry.Select(async source =>
        {
            try
            {
                foreach (var video in await source.Feed.NextPageAsync(ct)) source.Ready.Enqueue(video);
                source.Failures = 0;
                return (Source: source, Error: (Exception?)null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (Source: source, Error: ex);
            }
        }));

        var page = new List<SongItem>();
        foreach (var source in _sources)
            for (var i = 0; i < _takeFromEach && source.Ready.Count > 0; i++)
                page.Add(source.Ready.Dequeue());

        var failed = outcomes.Where(o => o.Error != null).ToList();
        if (failed.Count == 0) return page;
        if (page.Count == 0 && failed.Count == dry.Count) throw failed[0].Error!;

        foreach (var (source, _) in failed)
            if (++source.Failures >= FailuresToDrop)
                _sources.Remove(source);
        return page;
    }
}

/// <summary>
/// What a video page reads from. A feed can deliver a hundred clips at once while the page
/// grows a couple of dozen cards at a time as you scroll, so this keeps the difference in
/// hand and goes back to the network only when it runs out. A video seen twice counts once.
/// </summary>
public sealed class VideoFeedReader
{
    /// <summary>
    /// Pages in a row with nothing new before the feed is taken as finished. Generous: a
    /// mix hands out a few videos at a time, and it can start with a long run of what the
    /// page already shows. Every feed ends on its own; this only guards against one that
    /// would not.
    /// </summary>
    private const int MaxFruitlessPages = 64;

    private readonly IVideoFeed _feed;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly List<SongItem> _known = new();
    private int _shown;
    private int _fruitlessPages;

    public VideoFeedReader(IVideoFeed feed) => _feed = feed;

    /// <summary>Everything read so far, in order, on the page yet or not: what "Play all" plays.</summary>
    public IReadOnlyList<SongItem> Known => _known;

    public bool HasMore => _shown < _known.Count || CanRead;

    private bool CanRead => _feed.HasMore && _fruitlessPages < MaxFruitlessPages;

    /// <summary>
    /// Up to <paramref name="count"/> more videos. Throws a read failure only when there is
    /// nothing in hand to give; otherwise the next call meets the problem again.
    /// </summary>
    public async Task<IReadOnlyList<SongItem>> ReadAsync(int count, CancellationToken ct = default)
    {
        while (_known.Count - _shown < count && CanRead)
        {
            IReadOnlyList<SongItem> page;
            try
            {
                page = await _feed.NextPageAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && _shown < _known.Count)
            {
                break;
            }

            var added = 0;
            foreach (var video in page)
            {
                if (string.IsNullOrEmpty(video.Id) || !_seen.Add(video.Id)) continue;
                _known.Add(video);
                added++;
            }
            _fruitlessPages = added == 0 ? _fruitlessPages + 1 : 0;
        }

        var batch = _known.GetRange(_shown, Math.Min(count, _known.Count - _shown));
        _shown += batch.Count;
        return batch;
    }
}
