using System.Diagnostics;

namespace SoundLibert.Audio;

/// <summary>
/// A remote media URL downloading into a temp file that a reader may already
/// consume, so playback starts after the first chunk instead of waiting for the
/// whole track. The transfer itself is <see cref="ChunkedHttpDownloader"/>.
/// </summary>
internal sealed class ProgressiveHttpDownload : IDisposable
{
    private readonly string _url;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly object _sync = new();
    // Stopping the transfer must not depend on the owner's token: a reader is
    // released by Abort() no matter who decides to stop this track.
    private readonly CancellationTokenSource _abort = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _downloaded;
    private long _total;
    private bool _completed;
    private bool _aborted;
    private Exception? _error;

    public string Path { get; }

    private ProgressiveHttpDownload(string url, IReadOnlyDictionary<string, string> headers, string path, long total)
    {
        _url = url;
        _headers = headers;
        Path = path;
        _total = total;
    }

    public long Total { get { lock (_sync) return _total; } }
    public long Downloaded { get { lock (_sync) return _downloaded; } }
    public bool Completed { get { lock (_sync) return _completed; } }
    public Exception? Error { get { lock (_sync) return _error; } }

    /// <summary>True once nobody will wait for more bytes (see <see cref="Abort"/>).</summary>
    public bool IsAborted { get { lock (_sync) return _aborted; } }

    /// <summary>Probes the stream and picks a container extension MediaFoundation understands.</summary>
    public static async Task<ProgressiveHttpDownload> CreateAsync(
        string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        var (mediaType, total) = await ChunkedHttpDownloader.ProbeAsync(url, headers, ct);
        var extension = mediaType.Contains("webm", StringComparison.OrdinalIgnoreCase) ? ".webm"
            : mediaType.Contains("mp4", StringComparison.OrdinalIgnoreCase) ? ".m4a"
            : mediaType.Contains("ogg", StringComparison.OrdinalIgnoreCase) ? ".ogg"
            : ".webm";
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sl_{Guid.NewGuid():N}{extension}");
        return new ProgressiveHttpDownload(url, headers, path, total);
    }

    private void Signal()
    {
        TaskCompletionSource old;
        lock (_sync)
        {
            old = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        old.TrySetResult();
    }

    /// <summary>
    /// Waits until at least <paramref name="bytes"/> are downloaded (or it
    /// finished). Throws on download failure or when the download was aborted.
    /// </summary>
    public async Task WaitUntilAsync(long bytes, CancellationToken ct)
    {
        while (true)
        {
            Task wait;
            lock (_sync)
            {
                if (_aborted) throw new OperationCanceledException("The download was aborted.");
                if (_error != null) throw new IOException("[Audio] progressive download failed", _error);
                if (_completed || _downloaded >= bytes) return;
                wait = _changed.Task;
            }
            await Task.WhenAny(wait, Task.Delay(500, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
    }

    public Task WaitUntilCompleteAsync(CancellationToken ct) => WaitUntilAsync(long.MaxValue, ct);

    /// <summary>Runs the whole download; failures land in <see cref="Error"/> (never thrown here).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _abort.Token);
            await using var file = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            await ChunkedHttpDownloader.DownloadAsync(_url, _headers, file, Total, (downloaded, total) =>
            {
                lock (_sync)
                {
                    _downloaded = downloaded;
                    if (total > 0) _total = total;
                }
                Signal();
            }, linked.Token);

            lock (_sync) _completed = true;
            Signal();
            Debug.WriteLine($"[Audio] progressive download complete: {Downloaded} bytes");
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer track or a stop: not an error, but a reader
            // still waiting for bytes must not wait for them forever.
            Abort();
        }
        catch (Exception ex)
        {
            lock (_sync) _error = ex;
            Signal();
            Trace.WriteLine($"[Audio] download failed after {Downloaded} bytes: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops the transfer and releases any reader waiting for bytes. Everything
    /// that retires a playback path calls this FIRST, because retiring waits for
    /// the read in progress.
    /// </summary>
    public void Abort()
    {
        lock (_sync)
        {
            if (_aborted) return;
            _aborted = true;
        }
        try { _abort.Cancel(); } catch (ObjectDisposedException) { }
        Signal();
    }

    public void Dispose()
    {
        Abort();
        _abort.Dispose();
    }
}

/// <summary>
/// Seekable read-only view over a <see cref="ProgressiveHttpDownload"/>'s growing
/// file. Reads block until the requested bytes have been written, so a Media
/// Foundation reader can decode a track that is still downloading. Length is the
/// advertised total, so seeking and the duration stay correct. A read returns
/// end-of-file as soon as the download is aborted: the decoder thread must never
/// outlive the track it decodes.
/// </summary>
internal sealed class GrowingFileStream : Stream
{
    private const int PollMilliseconds = 20;

    private readonly ProgressiveHttpDownload _download;
    private readonly FileStream _file;
    private readonly object _sync = new();
    private long _position;
    private bool _disposed;

    public GrowingFileStream(ProgressiveHttpDownload download)
    {
        _download = download;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                _file = new FileStream(download.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                break;
            }
            catch (IOException) when (clock.Elapsed < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(PollMilliseconds);
            }
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _download.Total;

    public override long Position
    {
        get { lock (_sync) return _position; }
        set { lock (_sync) _position = value; }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (true)
        {
            lock (_sync)
            {
                if (_disposed) return 0;
            }
            if (_download.IsAborted) return 0;

            // Bytes already on disk stay playable even after a later chunk
            // failed; the failure only surfaces when the reader reaches the gap.
            var available = _download.Downloaded;
            lock (_sync)
            {
                if (_disposed) return 0;
                if (_position < available)
                {
                    _file.Seek(_position, SeekOrigin.Begin);
                    var toRead = (int)Math.Min(count, available - _position);
                    var n = _file.Read(buffer, offset, toRead);
                    _position += n;
                    return n;
                }
            }

            var error = _download.Error;
            if (error != null) throw new IOException("[Audio] source download failed", error);
            if (_download.Completed) return 0; // real end of file
            Thread.Sleep(PollMilliseconds);    // wait for the next chunk
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (_sync)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => _position
            };
            return _position;
        }
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_sync)
            {
                _disposed = true;
                _file.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
