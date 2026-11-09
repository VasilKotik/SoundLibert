using SoundLibert.Domain.Models;
using SoundLibert.Domain.Video;

namespace SoundLibert.Domain.Tests;

public class VideoFeedTests
{
    private static SongItem V(string id) => new() { Id = id, Title = id };

    private static List<SongItem> Vs(string prefix, int count) =>
        Enumerable.Range(1, count).Select(i => V($"{prefix}{i}")).ToList();

    private static string Ids(IEnumerable<SongItem> videos) => string.Join(",", videos.Select(v => v.Id));

    /// <summary>A feed that answers from a script: a list is a page, an exception a failed read.</summary>
    private sealed class ScriptedFeed(params object[] script) : IVideoFeed
    {
        private readonly Queue<object> _script = new(script);

        public int Reads { get; private set; }

        public bool HasMore => _script.Count > 0;

        public Task<IReadOnlyList<SongItem>> NextPageAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Reads++;
            if (_script.Peek() is Exception failure)
            {
                _script.Dequeue();
                throw failure;
            }
            return Task.FromResult<IReadOnlyList<SongItem>>((List<SongItem>)_script.Dequeue());
        }
    }

    // ---- Reader ----

    [Fact]
    public async Task Reader_keeps_the_rest_of_a_big_page_in_hand()
    {
        var feed = new ScriptedFeed(Vs("a", 100));
        var reader = new VideoFeedReader(feed);

        var first = await reader.ReadAsync(36);
        var second = await reader.ReadAsync(24);

        Assert.Equal(36, first.Count);
        Assert.Equal("a37", second[0].Id);
        Assert.Equal(1, feed.Reads);
        Assert.True(reader.HasMore);
    }

    [Fact]
    public async Task Reader_goes_to_the_next_page_when_the_first_is_short()
    {
        var reader = new VideoFeedReader(new ScriptedFeed(Vs("a", 10), Vs("b", 100)));

        var batch = await reader.ReadAsync(36);

        Assert.Equal(36, batch.Count);
        Assert.Equal("a10", batch[9].Id);
        Assert.Equal("b1", batch[10].Id);
    }

    [Fact]
    public async Task Reader_counts_a_video_seen_twice_once()
    {
        var reader = new VideoFeedReader(new ScriptedFeed(Vs("a", 3), Vs("a", 5)));

        var batch = await reader.ReadAsync(50);

        Assert.Equal("a1,a2,a3,a4,a5", Ids(batch));
        Assert.False(reader.HasMore);
    }

    [Fact]
    public async Task Reader_knows_more_than_it_has_shown_so_play_all_covers_it()
    {
        var reader = new VideoFeedReader(new ScriptedFeed(Vs("a", 100)));

        await reader.ReadAsync(36);

        Assert.Equal(100, reader.Known.Count);
    }

    [Fact]
    public async Task Reader_ends_when_the_feed_ends()
    {
        var reader = new VideoFeedReader(new ScriptedFeed(Vs("a", 3)));

        Assert.Equal(3, (await reader.ReadAsync(24)).Count);
        Assert.False(reader.HasMore);
        Assert.Empty(await reader.ReadAsync(24));
    }

    [Fact]
    public async Task Reader_gives_up_on_a_feed_that_keeps_answering_with_nothing_new()
    {
        var feed = new ScriptedFeed(Enumerable.Repeat<object>(Vs("a", 1), 500).ToArray());
        var reader = new VideoFeedReader(feed);

        await reader.ReadAsync(24);

        Assert.False(reader.HasMore);
        Assert.True(feed.Reads < 100, $"{feed.Reads} reads");
    }

    [Fact]
    public async Task Reader_does_not_stop_while_a_mix_repeats_what_the_page_already_has()
    {
        // Home's page: the rail first, then the same artist's channel, whose first page
        // is what the rail was made of, handed out three at a time.
        var rail = new SinglePageVideoFeed(Vs("a", 20));
        var channel = new MixedVideoFeed(new IVideoFeed[] { new ScriptedFeed(Vs("a", 20), Vs("b", 100)) }, takeFromEach: 3);
        var reader = new VideoFeedReader(new ConcatVideoFeed(rail, channel));

        var batch = await reader.ReadAsync(36);

        Assert.Equal(36, batch.Count);
        Assert.Equal("a20", batch[19].Id);
        Assert.Equal("b1", batch[20].Id);
        Assert.True(reader.HasMore);
    }

    [Fact]
    public async Task Reader_failure_with_nothing_in_hand_is_reported_and_retried()
    {
        var reader = new VideoFeedReader(new ScriptedFeed(new HttpRequestException("offline"), Vs("a", 3)));

        await Assert.ThrowsAsync<HttpRequestException>(() => reader.ReadAsync(24));
        Assert.True(reader.HasMore);

        Assert.Equal(3, (await reader.ReadAsync(24)).Count);
    }

    [Fact]
    public async Task Reader_failure_gives_what_is_in_hand_first()
    {
        var reader = new VideoFeedReader(new ScriptedFeed(Vs("a", 3), new HttpRequestException("offline"), Vs("b", 2)));

        Assert.Equal("a1,a2,a3", Ids(await reader.ReadAsync(24)));
        Assert.Equal("b1,b2", Ids(await reader.ReadAsync(24)));
    }

    [Fact]
    public async Task Reader_does_not_swallow_a_cancellation()
    {
        var reader = new VideoFeedReader(new ScriptedFeed(Vs("a", 3), Vs("b", 3)));
        using var cts = new CancellationTokenSource();
        await reader.ReadAsync(3);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(24, cts.Token));
    }

    // ---- Mix ----

    [Fact]
    public async Task Mix_takes_a_few_from_each_in_turn()
    {
        var mix = new MixedVideoFeed(new IVideoFeed[] { new ScriptedFeed(Vs("a", 100)), new ScriptedFeed(Vs("b", 100)) }, takeFromEach: 2);

        Assert.Equal("a1,a2,b1,b2", Ids(await mix.NextPageAsync()));
        Assert.Equal("a3,a4,b3,b4", Ids(await mix.NextPageAsync()));
    }

    [Fact]
    public async Task Mix_reads_a_source_again_only_when_it_ran_dry()
    {
        var big = new ScriptedFeed(Vs("a", 100));
        var small = new ScriptedFeed(Vs("b", 2), Vs("c", 2));
        var mix = new MixedVideoFeed(new IVideoFeed[] { big, small }, takeFromEach: 2);

        await mix.NextPageAsync();
        var second = await mix.NextPageAsync();

        Assert.Equal("a3,a4,c1,c2", Ids(second));
        Assert.Equal(1, big.Reads);
        Assert.Equal(2, small.Reads);
    }

    [Fact]
    public async Task Mix_goes_on_with_the_others_when_one_runs_out()
    {
        var mix = new MixedVideoFeed(new IVideoFeed[] { new ScriptedFeed(Vs("a", 1)), new ScriptedFeed(Vs("b", 4)) }, takeFromEach: 2);

        Assert.Equal("a1,b1,b2", Ids(await mix.NextPageAsync()));
        Assert.Equal("b3,b4", Ids(await mix.NextPageAsync()));
        Assert.False(mix.HasMore);
    }

    [Fact]
    public async Task Mix_survives_one_failing_source_and_asks_it_again()
    {
        var flaky = new ScriptedFeed(new HttpRequestException("500"), Vs("a", 1));
        var mix = new MixedVideoFeed(new IVideoFeed[] { flaky, new ScriptedFeed(Vs("b", 100)) }, takeFromEach: 1);

        Assert.Equal("b1", Ids(await mix.NextPageAsync()));
        Assert.Equal("a1,b2", Ids(await mix.NextPageAsync()));
    }

    [Fact]
    public async Task Mix_lets_go_of_a_source_that_keeps_failing_while_others_work()
    {
        var broken = new ScriptedFeed(Enumerable.Repeat<object>(new HttpRequestException("500"), 10).ToArray());
        var mix = new MixedVideoFeed(new IVideoFeed[] { broken, new ScriptedFeed(Vs("b", 100)) }, takeFromEach: 1);

        for (var i = 0; i < 5; i++) await mix.NextPageAsync();

        Assert.Equal(2, broken.Reads);
    }

    [Fact]
    public async Task Mix_reports_a_round_where_nothing_works_and_drops_nobody()
    {
        var one = new ScriptedFeed(new HttpRequestException("offline"), new HttpRequestException("offline"), new HttpRequestException("offline"), Vs("a", 1));
        var two = new ScriptedFeed(new HttpRequestException("offline"), new HttpRequestException("offline"), new HttpRequestException("offline"), Vs("b", 1));
        var mix = new MixedVideoFeed(new IVideoFeed[] { one, two });

        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<HttpRequestException>(() => mix.NextPageAsync());

        Assert.Equal("a1,b1", Ids(await mix.NextPageAsync()));
    }

    [Fact]
    public async Task Mix_of_nothing_is_finished()
    {
        var mix = new MixedVideoFeed(Array.Empty<IVideoFeed>());

        Assert.False(mix.HasMore);
        Assert.Empty(await mix.NextPageAsync());
    }

    // ---- The small ones ----

    [Fact]
    public async Task Concat_reads_one_feed_after_another()
    {
        var feed = new ConcatVideoFeed(new ScriptedFeed(Vs("a", 1), Vs("b", 1)), new ScriptedFeed(Vs("c", 1)));

        Assert.Equal("a1", Ids(await feed.NextPageAsync()));
        Assert.Equal("b1", Ids(await feed.NextPageAsync()));
        Assert.Equal("c1", Ids(await feed.NextPageAsync()));
        Assert.False(feed.HasMore);
        Assert.Empty(await feed.NextPageAsync());
    }

    [Fact]
    public async Task Concat_retries_the_same_feed_after_a_failure()
    {
        var feed = new ConcatVideoFeed(new ScriptedFeed(new HttpRequestException("offline"), Vs("a", 1)), new ScriptedFeed(Vs("b", 1)));

        await Assert.ThrowsAsync<HttpRequestException>(() => feed.NextPageAsync());

        Assert.Equal("a1", Ids(await feed.NextPageAsync()));
    }

    [Fact]
    public async Task Single_page_is_one_read_long_and_can_be_retried()
    {
        var calls = 0;
        var feed = new SinglePageVideoFeed(_ =>
        {
            if (++calls == 1) throw new HttpRequestException("offline");
            return Task.FromResult<IReadOnlyList<SongItem>>(Vs("a", 2));
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => feed.NextPageAsync());
        Assert.True(feed.HasMore);

        Assert.Equal(2, (await feed.NextPageAsync()).Count);
        Assert.False(feed.HasMore);
        Assert.Empty(await feed.NextPageAsync());
    }

    [Fact]
    public async Task Filter_keeps_only_what_the_rule_accepts()
    {
        var feed = new FilteredVideoFeed(new ScriptedFeed(Vs("a", 3)), v => v.Id != "a2");

        Assert.Equal("a1,a3", Ids(await feed.NextPageAsync()));
        Assert.False(feed.HasMore);
    }
}
