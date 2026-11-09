using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NAudio.Wave;
using SoundLibert.Audio;
using SoundLibert.Domain.Models;

namespace SoundLibert.Domain.Tests;

public class AudioPipelineTests
{
    private sealed class FixedSamples(float[] samples, int channels) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, samples.Length - _position);
            Array.Copy(samples, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }

    private static float[] Tone(int frames, int channels, Func<int, int, float> sample)
    {
        var data = new float[frames * channels];
        for (var frame = 0; frame < frames; frame++)
            for (var channel = 0; channel < channels; channel++)
                data[frame * channels + channel] = sample(frame, channel);
        return data;
    }

    private static float[] Run(ISampleProvider provider, int count)
    {
        var output = new float[count];
        var read = provider.Read(output, 0, count);
        Assert.Equal(count, read);
        return output;
    }

    [Fact]
    public void Equalizer_DoesNotBleedOneChannelIntoTheOther()
    {
        var input = Tone(4096, 2, (frame, channel) => channel == 0 ? MathF.Sin(frame * 0.05f) * 0.5f : 0f);
        var eq = new BiquadEqualizerSampleProvider(new FixedSamples(input, 2)) { BassDb = 6, TrebleDb = 3 };

        var output = Run(eq, input.Length);

        for (var i = 1; i < output.Length; i += 2) Assert.Equal(0f, output[i]);
        Assert.Contains(output, s => Math.Abs(s) > 0.01f);
    }

    [Fact]
    public void Equalizer_TreatsBothChannelsTheSame()
    {
        var input = Tone(4096, 2, (frame, _) => MathF.Sin(frame * 0.05f) * 0.5f);
        var eq = new BiquadEqualizerSampleProvider(new FixedSamples(input, 2)) { BassDb = 6, MidDb = -3 };

        var output = Run(eq, input.Length);

        for (var i = 0; i < output.Length; i += 2) Assert.Equal(output[i], output[i + 1]);
    }

    [Fact]
    public void Equalizer_LeavesTheSoundUntouched_WhenFlat()
    {
        var input = Tone(1024, 2, (frame, _) => MathF.Sin(frame * 0.05f) * 0.5f);
        var eq = new BiquadEqualizerSampleProvider(new FixedSamples(input, 2));

        Assert.Equal(input, Run(eq, input.Length));
    }

    [Fact]
    public void Equalizer_BoostDoesNotClip()
    {
        var input = Tone(8192, 2, (frame, _) => MathF.Sin(frame * 0.02f));
        var eq = new BiquadEqualizerSampleProvider(new FixedSamples(input, 2)) { BassDb = 6 };

        var output = Run(eq, input.Length);

        Assert.All(output, s => Assert.InRange(s, -1.05f, 1.05f));
    }

    [Theory]
    [InlineData(-3, 1)]
    [InlineData(0, 1)]
    [InlineData(6.02, 0.5)]
    [InlineData(100, 0.25)]
    public void Loudness_OnlyEverAttenuates(double loudnessDb, double expectedGain)
        => Assert.Equal(expectedGain, LoudnessNormalization.GainFor(loudnessDb), precision: 2);

    [Fact]
    public void Loudness_ALouderTrackPlaysQuieter()
        => Assert.True(LoudnessNormalization.GainFor(3.6) < LoudnessNormalization.GainFor(1));

    // ---- downloads against a real local server ----

    private sealed class LocalServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private int _requests;

        public string Url { get; }
        public int Requests => Volatile.Read(ref _requests);

        public LocalServer(Func<HttpListenerContext, int, Task> handle)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            Url = $"http://127.0.0.1:{port}/file";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch { return; }
                    var number = Interlocked.Increment(ref _requests);
                    _ = Task.Run(async () =>
                    {
                        try { await handle(context, number); }
                        catch { /* the client went away */ }
                    });
                }
            });
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Abort(); } catch { }
        }
    }

    private static async Task ServeRange(HttpListenerContext context, byte[] data)
    {
        var parts = (context.Request.Headers["Range"] ?? $"bytes=0-{data.Length - 1}")["bytes=".Length..].Split('-');
        var from = long.Parse(parts[0]);
        var to = Math.Min(long.Parse(parts[1]), data.Length - 1);
        if (from >= data.Length)
        {
            context.Response.StatusCode = 416;
            context.Response.Close();
            return;
        }
        context.Response.StatusCode = 206;
        context.Response.AddHeader("Content-Range", $"bytes {from}-{to}/{data.Length}");
        context.Response.ContentLength64 = to - from + 1;
        await context.Response.OutputStream.WriteAsync(data.AsMemory((int)from, (int)(to - from + 1)));
        context.Response.Close();
    }

    private static byte[] RandomBytes(int length)
    {
        var data = new byte[length];
        new Random(42).NextBytes(data);
        return data;
    }

    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    private static async Task<byte[]> Download(string url, long knownTotal = 0)
    {
        var path = Path.Combine(Path.GetTempPath(), "sl-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
                await ChunkedHttpDownloader.DownloadAsync(url, NoHeaders, file, knownTotal, null, CancellationToken.None);
            return await File.ReadAllBytesAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Download_ReassemblesTheFileFromRanges()
    {
        var data = RandomBytes(2_500_000);
        using var server = new LocalServer((context, _) => ServeRange(context, data));

        Assert.Equal(data, await Download(server.Url));
        Assert.Equal(3, server.Requests);
    }

    [Fact]
    public async Task Download_RetriesADroppedChunk_AndLosesNothing()
    {
        var data = RandomBytes(2_500_000);
        using var server = new LocalServer((context, number) =>
        {
            if (number != 2) return ServeRange(context, data);
            context.Response.Abort();
            return Task.CompletedTask;
        });

        Assert.Equal(data, await Download(server.Url));
    }

    [Fact]
    public async Task Download_FailsAtOnce_OnAnErrorARetryCannotFix()
    {
        using var server = new LocalServer((context, _) =>
        {
            context.Response.StatusCode = 403;
            context.Response.Close();
            return Task.CompletedTask;
        });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Download(server.Url));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task Download_ShorterThanAdvertised_IsAFailure()
    {
        var data = RandomBytes(1_500_000);
        using var server = new LocalServer((context, _) => ServeRange(context, data));

        await Assert.ThrowsAsync<IOException>(() => Download(server.Url, knownTotal: 3_000_000));
    }

    [Fact]
    public async Task Download_FullBodyForALaterRange_IsAFailure()
    {
        var data = RandomBytes(2_500_000);
        using var server = new LocalServer(async (context, number) =>
        {
            if (number == 1)
            {
                await ServeRange(context, data);
                return;
            }
            context.Response.StatusCode = 200;
            await context.Response.OutputStream.WriteAsync(data);
            context.Response.Close();
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => Download(server.Url));
    }

    [Fact]
    public async Task Abort_ReleasesAReaderWaitingForBytes()
    {
        var data = RandomBytes(2_500_000);
        using var server = new LocalServer(async (context, number) =>
        {
            if (number <= 2)
            {
                await ServeRange(context, data);
                return;
            }
            // The server goes quiet: the reader waits for bytes that never come.
            await Task.Delay(Timeout.Infinite);
        });

        using var download = await ProgressiveHttpDownload.CreateAsync(server.Url, NoHeaders, CancellationToken.None);
        _ = download.RunAsync(CancellationToken.None);
        await download.WaitUntilAsync(1, CancellationToken.None);

        using var stream = new GrowingFileStream(download);
        stream.Position = download.Downloaded;
        var reader = Task.Run(() => stream.Read(new byte[1024], 0, 1024));
        await Task.Delay(100);
        Assert.False(reader.IsCompleted);

        var clock = Stopwatch.StartNew();
        download.Abort();

        Assert.Equal(0, await reader.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"released after {clock.Elapsed}");
        TryDelete(download.Path);
    }

    [Fact]
    public async Task BytesAlreadyOnDisk_StayPlayable_AfterALaterFailure()
    {
        var data = RandomBytes(2_500_000);
        using var server = new LocalServer(async (context, number) =>
        {
            if (number <= 2)
            {
                await ServeRange(context, data);
                return;
            }
            context.Response.StatusCode = 403;
            context.Response.Close();
        });

        using var download = await ProgressiveHttpDownload.CreateAsync(server.Url, NoHeaders, CancellationToken.None);
        await download.RunAsync(CancellationToken.None);
        Assert.NotNull(download.Error);

        using var stream = new GrowingFileStream(download);
        var buffer = new byte[1024];
        Assert.Equal(1024, stream.Read(buffer, 0, buffer.Length));
        Assert.Equal(data.Take(1024), buffer);

        stream.Position = download.Downloaded;
        Assert.Throws<IOException>(() => stream.Read(buffer, 0, buffer.Length));
        TryDelete(download.Path);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* still closing */ }
    }
}
