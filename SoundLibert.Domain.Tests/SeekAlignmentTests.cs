using NAudio.Wave;
using SoundLibert.Audio;

namespace SoundLibert.Domain.Tests;

public sealed class SeekAlignmentTests
{
    [Fact]
    public void PlaybackWithoutSeek_DelegatesToExistingReader()
    {
        using var source = new FakeReader();
        using var reader = new SeekAlignedMediaFoundationReader(source, () => throw new Exception("Unexpected native path"));
        var output = new byte[8];
        Assert.Equal(8, reader.Read(output, 0, output.Length));
        Assert.All(output, b => Assert.Equal((byte)7, b));
        Assert.Equal(source.Position, reader.Position);
        Assert.False(reader.TryGetSeekAnchor(out _));
    }

    [Theory]
    [InlineData(27.824)]
    [InlineData(47.422)]
    [InlineData(65.644)]
    [InlineData(120)]
    public void CoarseLanding_DiscardsWholePacketsAndPartialPrerollToWithinOneFrame(double targetSeconds)
    {
        using var source = new FakeReader();
        var target = TimeSpan.FromSeconds(targetSeconds);
        var packets = new Queue<SeekAlignedMediaFoundationReader.PcmSample>(new[]
        {
            Sample(target - TimeSpan.FromSeconds(2), 48000, 1),
            Sample(target - TimeSpan.FromSeconds(1), 48000, 2),
            Sample(target - TimeSpan.FromSeconds(.004), 480, 3)
        });
        using var reader = new SeekAlignedMediaFoundationReader(source, () => packets.Count > 0 ? packets.Dequeue() : null);
        reader.CurrentTime = target;
        var output = new byte[4];

        Assert.Equal(4, reader.Read(output, 0, output.Length));
        Assert.True(reader.TryGetSeekAnchor(out var anchor));
        Assert.InRange((anchor - target).TotalMilliseconds, 0, 1000.0 / 48000);
        Assert.Equal(192, BitConverter.ToInt32(output));
        Assert.Single(source.ZeroByteReads);
    }

    [Fact]
    public void LandingAfterTarget_UsesRealDeliveredTimeInsteadOfRequestedAnchor()
    {
        using var source = new FakeReader();
        var packets = new Queue<SeekAlignedMediaFoundationReader.PcmSample>(new[] { Sample(TimeSpan.FromSeconds(28), 480, 1) });
        using var reader = new SeekAlignedMediaFoundationReader(source, () => packets.Count > 0 ? packets.Dequeue() : null);
        reader.CurrentTime = TimeSpan.FromSeconds(27.824);
        var output = new byte[4];

        Assert.Equal(4, reader.Read(output, 0, output.Length));
        Assert.True(reader.TryGetSeekAnchor(out var anchor));
        Assert.Equal(TimeSpan.FromSeconds(28), anchor);
        Assert.InRange((reader.CurrentTime - anchor).TotalMilliseconds, .020, .022);
        // Output played frames use this anchor: the old target would lead the
        // audio timeline by 176 ms, while the delivered sample has zero error.
        Assert.Equal(176, (anchor - TimeSpan.FromSeconds(27.824)).TotalMilliseconds, 6);
    }

    [Fact]
    public void RepeatedBackwardSeek_DiscardsPendingPcmAndResetsResolvedAnchor()
    {
        using var source = new FakeReader();
        var packets = new Queue<SeekAlignedMediaFoundationReader.PcmSample>(new[]
        {
            Sample(TimeSpan.FromSeconds(20), 480, 1), Sample(TimeSpan.FromSeconds(10), 480, 2)
        });
        using var reader = new SeekAlignedMediaFoundationReader(source, () => packets.Dequeue());
        reader.CurrentTime = TimeSpan.FromSeconds(20);
        var output = new byte[4];
        Assert.Equal(4, reader.Read(output, 0, 4));
        reader.CurrentTime = TimeSpan.FromSeconds(10);
        Assert.False(reader.TryGetSeekAnchor(out _));

        Assert.Equal(4, reader.Read(output, 0, 4));
        Assert.Equal(2, output[0]);
        Assert.True(reader.TryGetSeekAnchor(out var anchor));
        Assert.Equal(TimeSpan.FromSeconds(10), anchor);
        Assert.Equal(2, source.ZeroByteReads.Count);
    }

    [Fact]
    public void NegativeOpusPreroll_IsTrimmedAtZero()
    {
        using var source = new FakeReader();
        using var reader = new SeekAlignedMediaFoundationReader(source, () => Sample(TimeSpan.FromMilliseconds(-6.5), 960, 1));
        reader.CurrentTime = TimeSpan.Zero;
        var output = new byte[4];

        Assert.Equal(4, reader.Read(output, 0, 4));
        Assert.Equal(312, BitConverter.ToInt32(output));
        Assert.True(reader.TryGetSeekAnchor(out var anchor));
        Assert.InRange(anchor.TotalMilliseconds, 0, 1000.0 / 48000);
    }

    [Fact]
    public void FinalPacket_IsDrainedAcrossPartialReadsWithoutReadingPastEof()
    {
        using var source = new FakeReader();
        var calls = 0;
        using var reader = new SeekAlignedMediaFoundationReader(source, () =>
        {
            calls++;
            return Sample(TimeSpan.FromSeconds(1), 2, 1) with { EndOfStream = true };
        });
        reader.CurrentTime = TimeSpan.FromSeconds(1);
        var output = new byte[4];
        Assert.Equal(4, reader.Read(output, 0, 4));
        Assert.Equal(4, reader.Read(output, 0, 4));
        Assert.Equal(0, reader.Read(output, 0, 4));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void SeekPastLastSample_ReturnsEofWithoutPublishingFalseAnchor()
    {
        using var source = new FakeReader();
        using var reader = new SeekAlignedMediaFoundationReader(source,
            () => Sample(TimeSpan.FromSeconds(1), 2, 1) with { EndOfStream = true });
        reader.CurrentTime = TimeSpan.FromSeconds(2);
        Assert.Equal(0, reader.Read(new byte[4], 0, 4));
        Assert.False(reader.TryGetSeekAnchor(out _));
    }

    [Fact]
    public void ZeroByteRead_DoesNotPrepareOrConsumeSeekSamples()
    {
        using var source = new FakeReader();
        using var reader = new SeekAlignedMediaFoundationReader(source, () => throw new Exception("Unexpected read"));
        reader.CurrentTime = TimeSpan.FromSeconds(27.824);
        Assert.Equal(0, reader.Read(Array.Empty<byte>(), 0, 0));
        Assert.Empty(source.ZeroByteReads);
        Assert.False(reader.TryGetSeekAnchor(out _));
    }

    [Fact]
    public async Task RetiredOutputCallback_CannotConsumeTheNewSeeksFirstPcmOrAnchor()
    {
        using var source = new FakeReader();
        var packets = new Queue<SeekAlignedMediaFoundationReader.PcmSample>(new[] { Sample(TimeSpan.FromSeconds(20), 480, 1) });
        using var reader = new SeekAlignedMediaFoundationReader(source, () => packets.Dequeue());
        var pcm = reader.ToSampleProvider();
        var oldOutput = new RetirableSampleProvider(pcm);
        using var callbackStarted = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        var lateRead = Task.Run(() =>
        {
            callbackStarted.Set();
            Assert.True(releaseCallback.Wait(TimeSpan.FromSeconds(5)));
            return oldOutput.Read(new float[2], 0, 2);
        });
        try
        {
            Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(5)));
            oldOutput.Retire();
            reader.CurrentTime = TimeSpan.FromSeconds(20);
            var newOutput = new RetirableSampleProvider(pcm);
            releaseCallback.Set();
            Assert.Equal(0, await lateRead.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(reader.TryGetSeekAnchor(out _));

            var firstFrame = new float[2];
            Assert.Equal(2, newOutput.Read(firstFrame, 0, 2));
            Assert.True(reader.TryGetSeekAnchor(out var anchor));
            Assert.Equal(TimeSpan.FromSeconds(20), anchor);
            Assert.Equal(257f / 32768f, firstFrame[0]);
        }
        finally { releaseCallback.Set(); await lateRead; }
    }

    [Fact]
    public async Task Retire_WaitsForTheCurrentReadAndBlocksSubsequentReads()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var retiring = new ManualResetEventSlim();
        using var retired = new ManualResetEventSlim();
        var source = new BlockingProvider(entered, release);
        var output = new RetirableSampleProvider(source);
        var read = Task.Run(() => output.Read(new float[2], 0, 2));
        Task? retirement = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            retirement = Task.Run(() => { retiring.Set(); output.Retire(); retired.Set(); });
            Assert.True(retiring.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(retired.IsSet);
            release.Set();
            Assert.Equal(2, await read.WaitAsync(TimeSpan.FromSeconds(5)));
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, output.Read(new float[2], 0, 2));
            Assert.Equal(1, source.Reads);
        }
        finally { release.Set(); await read; if (retirement != null) await retirement; }
    }

    private sealed class BlockingProvider(ManualResetEventSlim entered, ManualResetEventSlim release) : ISampleProvider
    {
        public int Reads { get; private set; }
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            Reads++;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return count;
        }
    }

    private static SeekAlignedMediaFoundationReader.PcmSample Sample(TimeSpan start, int frames, byte marker)
    {
        var data = new byte[frames * 4];
        for (var frame = 0; frame < frames; frame++)
        {
            if (marker == 3 || start < TimeSpan.Zero) BitConverter.GetBytes(frame).CopyTo(data, frame * 4);
            else Array.Fill(data, marker, frame * 4, 4);
        }
        return new(start, data);
    }

    private sealed class FakeReader : WaveStream
    {
        public List<long> ZeroByteReads { get; } = new();
        public override WaveFormat WaveFormat { get; } = new(48000, 16, 2);
        public override long Length => WaveFormat.AverageBytesPerSecond * 147L;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) ZeroByteReads.Add(Position);
            Array.Fill(buffer, (byte)7, offset, count);
            Position += count;
            return count;
        }
    }
}
