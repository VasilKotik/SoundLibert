using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundLibert.Domain.Enums;
using SoundLibert.Domain.Interfaces;

namespace SoundLibert.Audio;

/// <summary>
/// Audio playback: Media Foundation decodes, WaveOut plays.
/// <para>
/// Threading rule: <see cref="_lock"/> only guards the pointers below and is
/// never held while waiting for a read or the network. A decoder may block for a
/// long time on a stream that is still downloading, so everything that has to
/// wait for it (retiring an output, closing a reader) runs on a background task.
/// That is what keeps the caller, usually the UI thread, responsive.
/// </para>
/// </summary>
public sealed class NAudioPlayer : IAudioPlayer
{
    private const int SystemDefaultDevice = -1;
    private const int OutputLatencyMs = 100;
    private static readonly TimeSpan EarlyEndTolerance = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly System.Timers.Timer _positionTimer;

    private MediaSource? _media;
    private WaveOutEvent? _output;
    private RetirableSampleProvider? _outputProvider;
    private VolumeSampleProvider? _volumeProvider;
    private BiquadEqualizerSampleProvider? _eqProvider;

    // A newer play/stop invalidates older opens; a newer seek or stop invalidates
    // older seeks. Each one checks its generation before touching the state.
    private CancellationTokenSource? _openCts;
    private int _playGeneration;
    private int _seekGeneration;
    private Task _retiringOutputs = Task.CompletedTask;
    private Task _lastTeardown = Task.CompletedTask;
    // A seek takes the output away while it waits; if a newer seek supersedes it,
    // that one still has to bring an output back.
    private bool _seekOwesOutput;

    private bool _isMuted;
    private double _normalizationGain = 1.0;
    private double _bassDb, _midDb, _trebleDb;
    private string _outputDeviceName = string.Empty;

    // Crossfade state: a gain multiplier (0..1) multiplied into the volume for
    // the fade-in / fade-out ramps, animated by the position timer.
    private double _crossfadeGain = 1.0;
    private double _crossfadeSeconds;
    private TimeSpan _fadeInDuration = TimeSpan.Zero;

    // Audible-position tracking. MediaFoundationReader.CurrentTime is the DECODER
    // pointer, which runs AHEAD of what is actually heard by the whole output
    // buffer (WaveOutEvent pre-buffers). Using it made lyrics/video lead the
    // audio. We instead anchor to the output device's played-byte counter.
    private TimeSpan _audibleBaseTime;
    private long _audibleBaseBytes;
#if DEBUG
    private int _seekDiagnosticTicks;
    private long _seekDiagnosticStartTimestamp;
    private int _seekDiagnosticGeneration;
#endif

    public event EventHandler<MediaPlayerState>? StateChanged;
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler? TrackEnded;
    public event EventHandler<Exception>? PlaybackFailed;

    public MediaPlayerState State { get; private set; }

    public TimeSpan Position
    {
        get { lock (_lock) return _media?.Reader.CurrentTime ?? TimeSpan.Zero; }
    }

    public TimeSpan Duration
    {
        get { lock (_lock) return _media?.Reader.TotalTime ?? TimeSpan.Zero; }
    }

    public double Volume { get; set; } = 1.0;

    /// <summary>Loudness-normalisation gain (0..1) folded into the volume.</summary>
    public double NormalizationGain
    {
        get => _normalizationGain;
        set { _normalizationGain = Math.Clamp(value, 0, 2); lock (_lock) ApplyVolume(); }
    }

    public double EqualizerBassDb
    {
        get => _bassDb;
        set { _bassDb = Math.Clamp(value, -12, 12); lock (_lock) ApplyEqualizer(); }
    }

    public double EqualizerMidDb
    {
        get => _midDb;
        set { _midDb = Math.Clamp(value, -12, 12); lock (_lock) ApplyEqualizer(); }
    }

    public double EqualizerTrebleDb
    {
        get => _trebleDb;
        set { _trebleDb = Math.Clamp(value, -12, 12); lock (_lock) ApplyEqualizer(); }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            lock (_lock) ApplyVolume();
        }
    }

    public double CrossfadeSeconds
    {
        get => _crossfadeSeconds;
        set => _crossfadeSeconds = Math.Clamp(value, 0, 12);
    }

    /// <summary>
    /// Output device by name (empty = the system default). Device numbers shift
    /// when devices are plugged in or removed, so the name is what is kept; it
    /// resolves anew for every output and falls back to the default when absent.
    /// Applies immediately to the track that is playing.
    /// </summary>
    public string OutputDeviceName
    {
        get => _outputDeviceName;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_outputDeviceName, value, StringComparison.Ordinal)) return;
            _outputDeviceName = value;

            TimeSpan? reopenAt;
            lock (_lock) reopenAt = _output != null && _media != null ? CurrentAudiblePosition() : null;
            if (reopenAt is { } position) _ = SeekAsync(position);
        }
    }

    public IReadOnlyList<string> GetOutputDevices()
    {
        var devices = new List<string>();
        try
        {
            var count = (int)waveOutGetNumDevs();
            for (var i = 0; i < count; i++)
            {
                var caps = new WaveOutCaps();
                var ok = waveOutGetDevCaps((UIntPtr)i, ref caps, (uint)Marshal.SizeOf<WaveOutCaps>()) == 0;
                devices.Add(ok && !string.IsNullOrWhiteSpace(caps.szPname) ? caps.szPname : $"Device {i}");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Audio] could not list output devices: {ex.Message}");
        }
        return devices;
    }

    private int ResolveDeviceNumber()
    {
        var wanted = _outputDeviceName;
        if (string.IsNullOrWhiteSpace(wanted)) return SystemDefaultDevice;
        var devices = GetOutputDevices();
        for (var i = 0; i < devices.Count; i++)
            if (string.Equals(devices[i], wanted, StringComparison.Ordinal))
                return i;
        Trace.WriteLine($"[Audio] output device \"{wanted}\" is not available, using the system default");
        return SystemDefaultDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveOutCaps
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public uint dwSupport;
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint waveOutGetDevCaps(UIntPtr uDeviceID, ref WaveOutCaps pwoc, uint cbwoc);

    private void ApplyVolume()
    {
        if (_volumeProvider == null) return;
        var gain = _isMuted ? 0.0 : Math.Clamp(Volume * _normalizationGain, 0, 1) * _crossfadeGain;
        _volumeProvider.Volume = (float)Math.Clamp(gain, 0, 1);
    }

    private void ApplyEqualizer()
    {
        if (_eqProvider == null) return;
        _eqProvider.BassDb = (float)_bassDb;
        _eqProvider.MidDb = (float)_midDb;
        _eqProvider.TrebleDb = (float)_trebleDb;
    }

    private static int _tempSweepDone;

    /// <summary>
    /// Deletes stream temp files left by earlier runs. A track's temp copy is
    /// removed when playback stops, but closing the app mid-track (or a crash)
    /// left it behind: tens of MB each, piling up in %TEMP% forever.
    /// </summary>
    private static void SweepStaleTempFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(30);
            foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), "sl_*.*"))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                // Only our own pattern: "sl_" + 32 hex chars (Guid "N").
                if (name.Length != 35 || !Guid.TryParseExact(name[3..], "N", out _)) continue;
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
                }
                catch { /* still open by another instance: leave it */ }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Audio] temp sweep failed: {ex.Message}");
        }
    }

    public NAudioPlayer()
    {
        if (Interlocked.Exchange(ref _tempSweepDone, 1) == 0)
            _ = Task.Run(SweepStaleTempFiles);

        _positionTimer = new System.Timers.Timer(100);
        _positionTimer.Elapsed += (_, _) =>
        {
            TimeSpan position;
            lock (_lock)
            {
                if (_media == null || _output is not { PlaybackState: NAudio.Wave.PlaybackState.Playing }) return;
                position = CurrentAudiblePosition();
                LogSeekClock(position);
                UpdateCrossfade(position);
            }
            PositionChanged?.Invoke(this, position);
        };
    }

    [Conditional("DEBUG")]
    private void LogSeekClock(TimeSpan position)
    {
#if DEBUG
        if (_seekDiagnosticTicks <= 0 || _media == null || _output == null) return;
        _seekDiagnosticTicks--;
        try
        {
            Debug.WriteLine(FormattableString.Invariant(
                $"[Audio] Seek clock generation={_seekDiagnosticGeneration} elapsedMs={Stopwatch.GetElapsedTime(_seekDiagnosticStartTimestamp).TotalMilliseconds:F1} audibleMs={position.TotalMilliseconds:F1} readerMs={_media.Reader.CurrentTime.TotalMilliseconds:F1} decoderAheadMs={(_media.Reader.CurrentTime - position).TotalMilliseconds:F1} baseMs={_audibleBaseTime.TotalMilliseconds:F1} baseBytes={_audibleBaseBytes} playedBytes={_output.GetPosition()} byteRate={_output.OutputWaveFormat.AverageBytesPerSecond}"));
        }
        catch { /* Diagnostics must not interrupt position updates. */ }
#endif
    }

    /// <summary>
    /// Animates the crossfade gain from the current position: a fade-in over the
    /// first <see cref="CrossfadeSeconds"/> seconds and a fade-out over the last.
    /// Must be called under <see cref="_lock"/>, while media is loaded.
    /// </summary>
    private void UpdateCrossfade(TimeSpan position)
    {
        if (_crossfadeSeconds <= 0 || _media == null)
        {
            if (_crossfadeGain < 1.0) { _crossfadeGain = 1.0; ApplyVolume(); }
            return;
        }

        var total = _media.Reader.TotalTime;
        var gain = 1.0;

        if (_fadeInDuration > TimeSpan.Zero && position < _fadeInDuration)
            gain = position.TotalSeconds / _fadeInDuration.TotalSeconds;

        if (total > TimeSpan.Zero)
        {
            var remaining = (total - position).TotalSeconds;
            if (remaining < _crossfadeSeconds)
                gain = Math.Min(gain, Math.Max(0, remaining) / _crossfadeSeconds);
        }

        gain = Math.Clamp(gain, 0.0, 1.0);
        if (Math.Abs(gain - _crossfadeGain) > 0.01)
        {
            _crossfadeGain = gain;
            ApplyVolume();
        }
    }

    /// <summary>
    /// Output played bytes mapped to media time, clamped to the decoder pointer.
    /// This is a reported output timeline, not a speaker-latency measurement.
    /// Must be called under <see cref="_lock"/>.
    /// </summary>
    private TimeSpan CurrentAudiblePosition()
    {
        if (_media == null) return TimeSpan.Zero;
        var reader = _media.Reader;
        var decoder = reader.CurrentTime;

        var byteRate = _output?.OutputWaveFormat?.AverageBytesPerSecond ?? 0;
        if (_output == null || byteRate <= 0) return decoder;

        long played;
        try { played = _output.GetPosition(); }
        catch { return decoder; }

        var delta = played - _audibleBaseBytes;
        if (delta < 0) delta = 0;

        var audible = _audibleBaseTime + TimeSpan.FromSeconds((double)delta / byteRate);
        // Native seeks may land before/after the request. Use the start of the
        // PCM actually delivered to this output, resolved on its reading thread.
        if (reader.TryGetSeekAnchor(out var decodedAnchor))
            audible = decodedAnchor + TimeSpan.FromSeconds((double)delta / byteRate);
        return audible > decoder ? decoder : audible;
    }

    /// <summary>
    /// Starts <paramref name="url"/> (a remote stream or a local file) from
    /// <paramref name="startPosition"/>. The task completes once playback has
    /// started, or quietly once a newer call superseded this one, and throws when
    /// this call is still the current one and the media cannot be played.
    /// </summary>
    public async Task PlayAsync(string url, IReadOnlyDictionary<string, string> headers, TimeSpan startPosition, CancellationToken ct = default)
    {
        CancellationToken openToken;
        int generation;
        lock (_lock)
        {
            // Cancel any open from a previous track click so rapid Next/Previous
            // doesn't pile up stalled googlevideo downloads.
            CancelPendingOpen();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _openCts = cts;
            openToken = cts.Token;
            generation = ++_playGeneration;
            StopInternal();
        }

        MediaSource? media = null;
        try
        {
            // Opening reads from the stream, which can block on the download.
            media = await Task.Run(() => OpenMediaAsync(url, headers, openToken), openToken);

            lock (_lock)
            {
                if (generation != _playGeneration) return; // the finally disposes it
                StartPlayback(media, startPosition);
                media = null; // owned by the player now
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer track or an explicit stop: not an error.
            Debug.WriteLine("[Audio] Open cancelled (superseded/stopped).");
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                if (generation != _playGeneration) return;
                StopInternal();
            }
            Trace.WriteLine($"[Audio] Could not start playback: {ex.GetType().Name}: {ex.Message}");
            SetState(MediaPlayerState.Error);
            throw;
        }
        finally
        {
            if (media != null) DisposeInBackground(media);
        }
    }

    /// <summary>Must be called under <see cref="_lock"/>.</summary>
    private void StartPlayback(MediaSource media, TimeSpan startPosition)
    {
        var reader = media.Reader;
        // Start offset BEFORE the output exists: the buffers fill from the
        // target, so no Stop/Play flush (and its WaveStillPlaying collision on a
        // just-started device) is ever needed here.
        if (startPosition > TimeSpan.Zero && reader.TotalTime > TimeSpan.Zero && startPosition < reader.TotalTime)
        {
            try
            {
                reader.CurrentTime = startPosition;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Audio] Start offset failed ({ex.Message}), starting at 0");
                try { reader.CurrentTime = TimeSpan.Zero; } catch { }
            }
        }
        Trace.WriteLine(FormattableString.Invariant(
            $"[Audio] Decoded durationMs={reader.TotalTime.TotalMilliseconds:F1} readerType={reader.GetType().Name} format={reader.WaveFormat} remote={media.IsRemote}"));

        // Reader → volume/normalisation → equalizer → output.
        _volumeProvider = new VolumeSampleProvider(reader.ToSampleProvider());
        _eqProvider = new BiquadEqualizerSampleProvider(_volumeProvider);

        // A resumed track is already in full swing: only a start from the
        // beginning fades in.
        _fadeInDuration = _crossfadeSeconds > 0 && startPosition <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(_crossfadeSeconds)
            : TimeSpan.Zero;
        _crossfadeGain = _fadeInDuration > TimeSpan.Zero ? 0.0 : 1.0;

        ApplyVolume();
        ApplyEqualizer();

        _media = media;
        try
        {
            CreateOutput();
            // Use the requested start until the reading thread resolves the
            // delivered PCM anchor. A resume must not start at Zero.
            _audibleBaseTime = reader.CurrentTime;
            _audibleBaseBytes = 0;
            _output!.Play();
            // Anchor the byte counter right after Play() starts the device.
            try { _audibleBaseBytes = _output.GetPosition(); } catch { _audibleBaseBytes = 0; }
        }
        catch
        {
            _media = null; // ownership stays with the caller, which disposes it
            DropOutput();
            _volumeProvider = null;
            _eqProvider = null;
            throw;
        }

        SetState(MediaPlayerState.Playing);
        _positionTimer.Start();
    }

    /// <summary>Creates the output for the current chain. Must be called under <see cref="_lock"/>.</summary>
    private void CreateOutput()
    {
        var provider = new RetirableSampleProvider(_eqProvider!);
        var device = ResolveDeviceNumber();
        var output = NewWaveOut(device);
        try
        {
            output.Init(provider);
        }
        catch (Exception ex) when (device != SystemDefaultDevice)
        {
            // The chosen device vanished between listing and opening it.
            Trace.WriteLine($"[Audio] device {device} failed to open ({ex.Message}), using the system default");
            try { output.Dispose(); } catch { }
            output = NewWaveOut(SystemDefaultDevice);
            output.Init(provider);
        }
        output.PlaybackStopped += OnPlaybackStopped;
        _output = output;
        _outputProvider = provider;
    }

    // Low latency keeps the audible position (and therefore the lyrics) close to
    // the reported playback position.
    private static WaveOutEvent NewWaveOut(int device) => new()
    {
        DeviceNumber = device,
        DesiredLatency = OutputLatencyMs,
        NumberOfBuffers = 3
    };

    /// <summary>
    /// Opens a local file, or a remote URL with a chunked background download.
    /// As soon as the first chunk is buffered a growing file stream feeds the
    /// decoder, so playback starts quickly. If the container needs a complete
    /// file (or Media Foundation rejects the stream) it waits for the whole
    /// download and opens the file instead.
    /// </summary>
    private static async Task<MediaSource> OpenMediaAsync(
        string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return new MediaSource(new SeekAlignedMediaFoundationReader(url), null, null);

        var download = await ProgressiveHttpDownload.CreateAsync(url, headers, ct);
        try
        {
            // All download failures land in download.Error (never thrown here).
            _ = download.RunAsync(ct);

            const long threshold = 512 * 1024; // first ~0.5 MB ≈ head + a chunk of audio
            var need = download.Total > 0 ? Math.Min(threshold, download.Total) : threshold;
            await download.WaitUntilAsync(need, ct);

            if (download.Completed)
                return new MediaSource(new SeekAlignedMediaFoundationReader(download.Path), download, null);

            GrowingFileStream? growing = null;
            try
            {
                growing = new GrowingFileStream(download);
                var reader = new SeekAlignedMediaFoundationReader(growing);
                Debug.WriteLine($"[Audio] Progressive start at {download.Downloaded / 1024} KB buffered");
                return new MediaSource(reader, download, growing);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                growing?.Dispose();
                Trace.WriteLine($"[Audio] Progressive decode unsupported ({ex.Message}); waiting for the full download");
                await download.WaitUntilCompleteAsync(ct);
                return new MediaSource(new SeekAlignedMediaFoundationReader(download.Path), download, null);
            }
        }
        catch
        {
            new MediaSource(null, download, null).Dispose();
            throw;
        }
    }

    public Task PauseAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_media == null) return Task.CompletedTask;
            try { _output?.Pause(); }
            catch (Exception ex) { Trace.WriteLine($"[Audio] pause failed: {ex.Message}"); }
            SetState(MediaPlayerState.Paused);
            _positionTimer.Stop();
        }
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_media == null) return Task.CompletedTask;
            try { _output?.Play(); }
            catch (Exception ex) { Trace.WriteLine($"[Audio] resume failed: {ex.Message}"); }
            SetState(MediaPlayerState.Playing);
            _positionTimer.Start();
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            CancelPendingOpen();
            _playGeneration++;
            StopInternal();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Moves playback to <paramref name="position"/>. The old output has to stop
    /// consuming the reader first, and it may be blocked waiting for bytes that
    /// are still downloading, so that wait happens off the caller's thread with
    /// no lock held. A newer seek or a stop simply supersedes this one.
    /// </summary>
    public async Task SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        int generation;
        TimeSpan target;
        Task retired;
        lock (_lock)
        {
            if (_media == null) return;
            var total = _media.Reader.TotalTime;
            target = position < TimeSpan.Zero ? TimeSpan.Zero : position;
            if (total > TimeSpan.Zero && target > total) target = total;

            generation = ++_seekGeneration;
            RetireOutput();
            retired = _retiringOutputs;
        }

        await retired;

        lock (_lock)
        {
            if (generation != _seekGeneration || _media == null) return;
            var reader = _media.Reader;
            // Seek only now that no retired output consumes the reader. Anchor
            // before Play queues the new buffers.
            reader.CurrentTime = target;
            _audibleBaseTime = reader.CurrentTime;
            _audibleBaseBytes = 0;

            if (_seekOwesOutput)
            {
                _seekOwesOutput = false;
                try
                {
                    CreateOutput();
                    try { _audibleBaseBytes = _output!.GetPosition(); } catch { _audibleBaseBytes = 0; }
                    // The state, not a snapshot: pausing or resuming during the
                    // wait must win.
                    if (State == MediaPlayerState.Playing)
                    {
                        _output!.Play();
                        _positionTimer.Start();
                    }
                }
                catch (Exception ex)
                {
                    // Leave no half-built device behind: report Error so the next
                    // Play press restarts cleanly.
                    Trace.WriteLine($"[Audio] Seek re-arm failed: {ex.Message}");
                    DropOutput();
                    SetState(MediaPlayerState.Error);
                }
            }

#if DEBUG
            _seekDiagnosticTicks = 50;
            _seekDiagnosticStartTimestamp = Stopwatch.GetTimestamp();
            _seekDiagnosticGeneration++;
#endif
            Trace.WriteLine(FormattableString.Invariant(
                $"[Audio] Seek -> {target:hh\\:mm\\:ss\\.fff} (reader now {reader.CurrentTime:hh\\:mm\\:ss\\.fff}, remote={_media.IsRemote})"));
        }
    }

    /// <summary>
    /// Detaches the current output so nothing plays it or hears from it anymore,
    /// then finishes it in the background. Must be called under <see cref="_lock"/>.
    /// </summary>
    private void RetireOutput()
    {
        var output = _output;
        var provider = _outputProvider;
        _output = null;
        _outputProvider = null;
        if (output == null) return;

        _seekOwesOutput = true;
        output.PlaybackStopped -= OnPlaybackStopped;
        try { output.Stop(); } catch { /* the device may already be gone */ }

        var previous = _retiringOutputs;
        _retiringOutputs = Task.Run(async () =>
        {
            await previous;
            // Dispose does not join the WaveOut worker, so close its reader gate
            // first: a late callback must not consume data of the next output.
            provider?.Retire();
            try { output.Dispose(); } catch { }
        });
    }

    /// <summary>Forgets a half-built output. Must be called under <see cref="_lock"/>.</summary>
    private void DropOutput()
    {
        var output = _output;
        _output = null;
        _outputProvider?.Retire();
        _outputProvider = null;
        if (output == null) return;
        output.PlaybackStopped -= OnPlaybackStopped;
        try { output.Dispose(); } catch { }
    }

    public Task SetVolumeAsync(double volume, CancellationToken ct = default)
    {
        Volume = Math.Clamp(volume, 0, 1);
        lock (_lock) ApplyVolume();
        return Task.CompletedTask;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        var failure = e.Exception;
        lock (_lock)
        {
            // A callback already posted by a retired device may arrive after the
            // new output starts. It must not stop its clock or end the track.
            if (!ReferenceEquals(sender, _output)) return;
            _positionTimer.Stop();

            // A stream that "ends" long before its duration did not finish, it
            // failed: YouTube's fragmented AAC, for one, decodes to zero samples
            // at full duration. Local files may simply carry a wrong duration.
            if (failure == null && _media is { IsRemote: true } media)
            {
                try
                {
                    var missing = media.Reader.TotalTime - media.Reader.CurrentTime;
                    if (missing > EarlyEndTolerance)
                        failure = new InvalidDataException(
                            $"The stream ended {missing.TotalSeconds:F0} s before its duration.");
                }
                catch { /* the reader is going away: treat it as a normal end */ }
            }
        }

        if (failure != null)
        {
            Trace.WriteLine($"[Audio] Playback failed: {failure.GetType().Name}: {failure.Message}");
            SetState(MediaPlayerState.Error);
            PlaybackFailed?.Invoke(this, failure);
        }
        else
        {
            SetState(MediaPlayerState.Stopped);
            TrackEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CancelPendingOpen()
    {
        try { _openCts?.Cancel(); } catch { }
        _openCts?.Dispose();
        _openCts = null;
    }

    /// <summary>
    /// Silences and forgets the current track right away; finishing it may have
    /// to wait for a read, so that part runs in the background. Must be called
    /// under <see cref="_lock"/>.
    /// </summary>
    private void StopInternal()
    {
        _seekGeneration++;
        _positionTimer.Stop();
#if DEBUG
        _seekDiagnosticTicks = 0;
#endif

        // First: a decoder blocked on bytes must wake up, because everything
        // below eventually waits for the read it is stuck in.
        var media = _media;
        _media = null;
        media?.Abort();

        RetireOutput();
        _seekOwesOutput = false;

        _eqProvider = null;
        _volumeProvider = null;

        var retired = _retiringOutputs;
        _retiringOutputs = Task.CompletedTask;
        if (media != null)
            _lastTeardown = Task.Run(async () =>
            {
                await retired;
                media.Dispose();
            });

        SetState(MediaPlayerState.Stopped);
    }

    private static void DisposeInBackground(MediaSource media)
    {
        media.Abort();
        _ = Task.Run(media.Dispose);
    }

    private void SetState(MediaPlayerState newState)
    {
        if (State == newState) return;
        State = newState;
        StateChanged?.Invoke(this, newState);
    }

    public void Dispose()
    {
        Task teardown;
        lock (_lock)
        {
            CancelPendingOpen();
            _playGeneration++;
            StopInternal();
            teardown = _lastTeardown;
        }
        _positionTimer.Dispose();
        // Best effort: gives the temp file a chance to go; the next start sweeps it otherwise.
        try { teardown.Wait(TimeSpan.FromSeconds(1)); } catch { }
    }

    /// <summary>One opened track: its reader and, for a stream, its download.</summary>
    private sealed class MediaSource(SeekAlignedMediaFoundationReader? reader, ProgressiveHttpDownload? download, Stream? growing)
    {
        public SeekAlignedMediaFoundationReader Reader => reader!;
        public bool IsRemote => download != null;

        public void Abort() => download?.Abort();

        public void Dispose()
        {
            download?.Abort();
            try { reader?.Dispose(); }
            catch (Exception ex) { Trace.WriteLine($"[Audio] reader close failed: {ex.Message}"); }
            try { growing?.Dispose(); } catch { }
            if (download == null) return;
            download.Dispose();
            // The writer closes asynchronously on cancel, so give it a moment.
            for (var attempt = 0; attempt < 10 && File.Exists(download.Path); attempt++)
            {
                try { File.Delete(download.Path); break; }
                catch { Thread.Sleep(100); }
            }
        }
    }
}
