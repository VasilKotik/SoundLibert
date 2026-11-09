using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace SoundLibert.Audio;

/// <summary>
/// Leaves ordinary playback with NAudio. After a seek, uses native sample
/// timestamps to discard preroll and expose the time of the PCM actually read.
/// SetCurrentPosition alone does not promise an exact landing.
/// </summary>
public sealed class SeekAlignedMediaFoundationReader : WaveStream
{
    private readonly WaveStream _reader;
    private readonly Func<PcmSample?> _readSample;
    private readonly object _readLock = new();
    private bool _seeking;
    private bool _prepareSeek;
    private bool _endOfStream;
    private byte[] _pending = Array.Empty<byte>();
    private int _pendingOffset;
    private int _pendingCount;
    private long _positionBytes;
    private long _anchorTicks = long.MinValue;
    private TimeSpan _target;
    private TimeSpan? _firstSampleStart;
    private long _discardedFrames;

    public SeekAlignedMediaFoundationReader(string path) : this((INativeReader)new FileReader(path)) { }
    public SeekAlignedMediaFoundationReader(Stream stream) : this((INativeReader)new StreamReader(stream)) { }

    private SeekAlignedMediaFoundationReader(INativeReader reader)
        : this(reader.WaveReader, () => ReadNativeSample(reader.NativeReader, reader.WaveReader.WaveFormat)) { }

    internal SeekAlignedMediaFoundationReader(WaveStream reader, Func<PcmSample?> readSample)
    {
        _reader = reader;
        _readSample = readSample;
    }

    internal readonly record struct PcmSample(TimeSpan Start, byte[] Data, bool EndOfStream = false);

    public override WaveFormat WaveFormat => _reader.WaveFormat;
    public override long Length => _reader.Length;

    public override long Position
    {
        get => Volatile.Read(ref _seeking) ? Volatile.Read(ref _positionBytes) : _reader.Position;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_readLock)
            {
                _reader.Position = value;
                _target = TimeSpan.FromSeconds((double)value / WaveFormat.AverageBytesPerSecond);
                Volatile.Write(ref _anchorTicks, long.MinValue);
                Volatile.Write(ref _positionBytes, value);
                _pendingCount = _pendingOffset = 0;
                _endOfStream = false;
                _firstSampleStart = null;
                _discardedFrames = 0;
                _prepareSeek = true;
                Volatile.Write(ref _seeking, true);
            }
        }
    }

    public bool TryGetSeekAnchor(out TimeSpan anchor)
    {
        var ticks = Volatile.Read(ref _anchorTicks);
        anchor = ticks == long.MinValue ? TimeSpan.Zero : TimeSpan.FromTicks(ticks);
        return ticks != long.MinValue;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(count));
        lock (_readLock)
        {
            if (!_seeking) return _reader.Read(buffer, offset, count);
            if (count == 0) return 0;
            if (_prepareSeek)
            {
                // NAudio creates its apartment-local reader, performs its
                // deferred native seek, and clears its old PCM buffer here.
                // Zero bytes consume no sample. The captured native reader is
                // the very same COM object that NAudio owns and disposes.
                if (_reader.Read(Array.Empty<byte>(), 0, 0) != 0)
                    throw new InvalidDataException("Zero-byte seek preparation consumed PCM.");
                _prepareSeek = false;
            }

            var written = 0;
            while (written < count)
            {
                if (_pendingCount == 0)
                {
                    if (_endOfStream) break;
                    var sample = _readSample();
                    if (sample == null) { _endOfStream = true; break; }
                    var pcm = sample.Value;
                    _endOfStream = pcm.EndOfStream;
                    if (pcm.Data.Length % WaveFormat.BlockAlign != 0)
                        throw new InvalidDataException("Decoded PCM is not frame aligned.");
                    _firstSampleStart ??= pcm.Start;
                    var skipFrames = 0;
                    if (!TryGetSeekAnchor(out _))
                    {
                        var frames = pcm.Data.Length / WaveFormat.BlockAlign;
                        var beforeTarget = (decimal)(_target.Ticks - pcm.Start.Ticks)
                            * WaveFormat.SampleRate / TimeSpan.TicksPerSecond;
                        skipFrames = (int)Math.Clamp(decimal.Ceiling(beforeTarget), 0, frames);
                        _discardedFrames += skipFrames;
                        if (skipFrames == frames) continue;
                        var start = pcm.Start + TimeSpan.FromTicks((long)((decimal)skipFrames
                            * TimeSpan.TicksPerSecond / WaveFormat.SampleRate));
                        Volatile.Write(ref _positionBytes, (long)((decimal)start.Ticks
                            * WaveFormat.AverageBytesPerSecond / TimeSpan.TicksPerSecond));
                        Volatile.Write(ref _anchorTicks, start.Ticks);
                        Trace.WriteLine(FormattableString.Invariant(
                            $"[Audio] Seek decoder targetMs={_target.TotalMilliseconds:F3} firstSampleMs={_firstSampleStart.Value.TotalMilliseconds:F3} deliveredStartMs={start.TotalMilliseconds:F3} discardedFrames={_discardedFrames}"));
                    }
                    _pending = pcm.Data;
                    _pendingOffset = skipFrames * WaveFormat.BlockAlign;
                    _pendingCount = pcm.Data.Length - _pendingOffset;
                }
                var copied = Math.Min(count - written, _pendingCount);
                Array.Copy(_pending, _pendingOffset, buffer, offset + written, copied);
                _pendingOffset += copied;
                _pendingCount -= copied;
                written += copied;
                Interlocked.Add(ref _positionBytes, copied);
            }
            return written;
        }
    }

    private static PcmSample? ReadNativeSample(IMFSourceReader reader, WaveFormat format)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native Media Foundation samples require Windows.");
        for (var attempt = 0; attempt < 100; attempt++)
        {
            reader.ReadSample(MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0,
                out _, out var flags, out var timestamp, out var sample);
            try
            {
                if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR) != 0)
                    throw new InvalidDataException($"Media Foundation read failed: {flags}");
                if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0)
                {
                    reader.GetCurrentMediaType(MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM, out var type);
                    try
                    {
                        var media = new MediaType(type);
                        var expectedSubtype = format.Encoding == WaveFormatEncoding.IeeeFloat
                            ? AudioSubtypes.MFAudioFormat_Float : AudioSubtypes.MFAudioFormat_PCM;
                        if (media.SampleRate != format.SampleRate || media.ChannelCount != format.Channels
                            || media.BitsPerSample != format.BitsPerSample || media.SubType != expectedSubtype)
                            throw new InvalidDataException("Decoded format changed after seek.");
                    }
                    finally { Marshal.ReleaseComObject(type); }
                }
                var end = (flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0;
                if (sample == null) { if (end) return null; continue; }
                sample.ConvertToContiguousBuffer(out var mediaBuffer);
                try
                {
                    mediaBuffer.Lock(out var data, out _, out var length);
                    try
                    {
                        var bytes = new byte[length];
                        Marshal.Copy(data, bytes, 0, length);
                        return new PcmSample(TimeSpan.FromTicks(unchecked((long)timestamp)), bytes, end);
                    }
                    finally { mediaBuffer.Unlock(); }
                }
                finally { Marshal.ReleaseComObject(mediaBuffer); }
            }
            finally { if (sample != null) Marshal.ReleaseComObject(sample); }
        }
        throw new InvalidDataException("Media Foundation returned no PCM sample after seek.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) lock (_readLock) _reader.Dispose();
        base.Dispose(disposing);
    }

    private interface INativeReader
    {
        WaveStream WaveReader { get; }
        IMFSourceReader NativeReader { get; }
    }

    // One native reader per track. By default NAudio opens a second one on the
    // first Read and never releases the first until the GC runs, which keeps the
    // file locked (deleting or moving a download failed) and probes a stream
    // twice. Readers are created on a thread-pool thread and read on WaveOut's,
    // both in the MTA, so a single object is valid for both.
    private static MediaFoundationReader.MediaFoundationReaderSettings SingleReader()
        => new() { SingleReaderObject = true };

    private sealed class FileReader(string path) : MediaFoundationReader(path, SingleReader()), INativeReader
    {
        private IMFSourceReader? _native;
        public WaveStream WaveReader => this;
        public IMFSourceReader NativeReader => _native ?? throw new InvalidOperationException("Reader not initialized.");
        protected override IMFSourceReader CreateReader(MediaFoundationReaderSettings settings)
            => _native = base.CreateReader(settings);
    }

    private sealed class StreamReader(Stream stream) : StreamMediaFoundationReader(stream, SingleReader()), INativeReader
    {
        private IMFSourceReader? _native;
        public WaveStream WaveReader => this;
        public IMFSourceReader NativeReader => _native ?? throw new InvalidOperationException("Reader not initialized.");
        protected override IMFSourceReader CreateReader(MediaFoundationReaderSettings settings)
            => _native = base.CreateReader(settings);
    }
}
