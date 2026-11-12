using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using LibVLCSharp.Shared;

namespace SoundLibert.UI.Services;

/// <summary>
/// The video engine, used two ways:
/// <list type="bullet">
/// <item><b>Song mode</b> — a continuous muted segment from a stable offset behind
/// the now-playing screen (Spotify-Canvas style). Audio stays on NAudio.</item>
/// <item><b>Video mode</b> — a single, self-contained playback of the full clip
/// <b>with its sound</b>. YouTube serves video and audio as separate DASH
/// streams, so the best audio stream is attached as a slave input
/// (<c>:input-slave</c>); NAudio is stopped and the transport drives this player.</item>
/// </list>
/// Decoded frames go through libvlc's video callbacks into a managed bitmap
/// (<c>CanvasVideoView</c>) so the UI composites over the picture.
/// </summary>
public sealed class VideoPlaybackService : IDisposable
{
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    // Read by the serial worker to see whether a newer call replaced this media.
    private volatile Media? _media;
    private readonly object _initLock = new();
    private readonly System.Timers.Timer _tick = new(250);

    // libvlc's Stop() and a media swap on a playing player are SYNCHRONOUS:
    // they join the decoder/vout threads and can take hundreds of ms. Run on
    // the UI thread they froze the window on every track change / fullscreen
    // open. All such calls go through this single serial worker instead, so
    // their order is kept (stop → play) without ever blocking the caller.
    private readonly object _chainLock = new();
    private Task _chain = Task.CompletedTask;

    private void Enqueue(Action action)
    {
        lock (_chainLock)
        {
            _chain = _chain.ContinueWith(_ =>
            {
                try { action(); }
                catch (Exception ex) { Trace.WriteLine($"[Video] worker: {ex.Message}"); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    public MediaPlayer? Player => _player;

    /// <summary>True when the current media was played with its own audio.</summary>
    public bool HasAudio { get; private set; }

    public bool IsPlaying => _player?.IsPlaying ?? false;

    /// <summary>True when playback is paused mid-media (as opposed to stopped or ended).</summary>
    public bool IsPaused => _player?.State == VLCState.Paused;

    /// <summary>True when a media is loaded (even while paused) — resume is possible without re-resolving.</summary>
    public bool HasMedia => _media != null;

    public TimeSpan Position => _player is { Time: > 0 } p
        ? TimeSpan.FromMilliseconds(p.Time)
        : TimeSpan.Zero;

    public TimeSpan Duration => _player is { Length: > 0 } p
        ? TimeSpan.FromMilliseconds(p.Length)
        : TimeSpan.Zero;

    public string DebugState => _player == null
        ? "no-player"
        : $"state={_player.State} playing={_player.IsPlaying} time={_player.Time}";

    /// <summary>Raised (on a libvlc thread) when the current media finishes.</summary>
    public event Action? EndReached;

    /// <summary>Raised periodically (thread-pool) so callers can poll Position/Duration.</summary>
    public event Action? Tick;

    public VideoPlaybackService()
    {
        _tick.Elapsed += (_, _) => { try { Tick?.Invoke(); } catch { /* ignore */ } };
    }

    /// <summary>
    /// Creates the libvlc engine/player on a background thread without starting
    /// playback. Callers must run this and attach video callbacks BEFORE
    /// <see cref="PlayFrom"/>: setting libvlc video callbacks while a player is
    /// already playing deadlocks the libvlc video-output thread against the UI
    /// thread (the app froze on entering fullscreen).
    /// </summary>
    public async Task PrepareAsync()
    {
        if (!_nativeLoaded && LoadNativeOnMainThread is { } load)
        {
            await load();
            _nativeLoaded = true;
        }
        await Task.Run(EnsureInit);
    }

    /// <summary>
    /// Loads the engine's native libraries where the platform wants that done on its main
    /// thread: Android loads them through Java, whose class loader is only the app's there.
    /// Null where any thread will do (Windows).
    /// </summary>
    public static Func<Task>? LoadNativeOnMainThread { get; set; }

    private static bool _nativeLoaded;

    /// <summary>
    /// Plays <paramref name="url"/> from <paramref name="startSeconds"/>. With
    /// <paramref name="withAudio"/> the attached <paramref name="audioUrl"/> is
    /// mixed in as the soundtrack (Video mode); otherwise the video is muted
    /// (Canvas/Song mode).
    /// </summary>
    public bool PlayFrom(string url, int startSeconds, bool withAudio = false, string? audioUrl = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        try
        {
            EnsureInit();
            if (_player == null) return false;

            var previous = _media;
            _media = null;

            // Software decoding is REQUIRED so VLC hands frames to the managed
            // control (bitmap compositing) instead of opening a native hardware
            // (d3d11) child window — otherwise the UI would be hidden behind the
            // video (airspace) and nothing could be drawn on top.
            var options = new List<string>
            {
                ":no-video-title-show",
                ":avcodec-hw=none",
                ":drop-late-frames",
                ":skip-frames",
                ":avcodec-skiploopfilter=all",
                // Canvas: a short buffer gets the first frame up sooner (it is
                // muted, a rare hiccup is invisible); Video mode keeps 1s.
                withAudio ? ":network-caching=1000" : ":network-caching=500",
                $":start-time={Math.Max(0, startSeconds)}"
            };
            if (!withAudio) options.Add(":avcodec-fast");

            if (withAudio && !string.IsNullOrWhiteSpace(audioUrl))
            {
                // DASH: video + audio are separate. A local file must be passed as
                // an MRL (file:///C:/...), a remote one as-is.
                var slave = audioUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? audioUrl
                    : "file:///" + audioUrl.Replace('\\', '/');
                options.Add(":input-slave=" + slave);
            }
            else if (!withAudio)
                options.Add(":no-audio");

            HasAudio = withAudio;
            // Remote URLs stream; a local path (downloaded clip) is opened from
            // disk so seeking/pausing are instant.
            var media = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? new Media(_libVlc!, new Uri(url), options.ToArray())
                : new Media(_libVlc!, url, FromType.FromPath, options.ToArray());
            _media = media;
            var player = _player;
            Enqueue(() =>
            {
                try
                {
                    // A newer PlayFrom/Stop already replaced this media.
                    if (!ReferenceEquals(_media, media)) { media.Dispose(); return; }
                    player.Play(media);
                }
                finally
                {
                    try { previous?.Dispose(); } catch { /* ignore */ }
                }
            });
            _tick.Start();

            Debug.WriteLine($"[Video] play from {startSeconds}s (audio={withAudio})");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Video] play failed: {ex.Message}");
            return false;
        }
    }

    public void Seek(TimeSpan position)
    {
        try
        {
            if (_player == null) return;
            var ms = (long)Math.Max(0, position.TotalMilliseconds);
            var state = _player.State;

            // Ended/Stopped: VLC ignores Time on a finished media — restart it
            // and apply the target once it is actually playing again.
            if (state is VLCState.Ended or VLCState.Stopped or VLCState.Error && _media != null && _player is { } p)
            {
                void OnPlaying(object? s, EventArgs e)
                {
                    p.Playing -= OnPlaying;
                    // Never call back into libvlc from its own event thread.
                    ThreadPool.QueueUserWorkItem(_ => { try { p.Time = ms; } catch { /* ignore */ } });
                }
                _player.Playing += OnPlaying;
                var media = _media;
                Enqueue(() => p.Play(media));
                _tick.Start();
                return;
            }

            _player.Time = ms;
            // Scrubbing a paused clip auto-starts it (same feel as YouTube).
            if (state == VLCState.Paused) _player.SetPause(false);
        }
        catch { /* ignore */ }
    }

    public void SetVolume(double volume01)
    {
        try
        {
            if (_player != null)
                _player.Volume = (int)Math.Clamp(Math.Round(volume01 * 100), 0, 100);
        }
        catch { /* ignore */ }
    }

    public void SetMuted(bool muted)
    {
        try { if (_player != null) _player.Mute = muted; } catch { /* ignore */ }
    }

    /// <summary>Toggles play/pause (Video mode transport).</summary>
    public void TogglePause()
    {
        try
        {
            if (_player == null) return;
            if (_player.IsPlaying) _player.Pause();
            else _player.Play();
        }
        catch { /* ignore */ }
    }

    /// <summary>Stops playback without blocking the caller (see <see cref="Enqueue"/>).</summary>
    public void Stop()
    {
        try { _tick.Stop(); } catch { /* ignore */ }
        var player = _player;
        var media = _media;
        _media = null;
        if (player == null && media == null) return;
        Enqueue(() =>
        {
            // A PlayFrom queued after this Stop owns the player now.
            if (_media == null) player?.Stop();
            try { media?.Dispose(); } catch { /* ignore */ }
        });
    }

    public void Pause()
    {
        try { _player?.SetPause(true); } catch { /* ignore */ }
    }

    public void Resume()
    {
        try { _player?.SetPause(false); } catch { /* ignore */ }
    }

    private void EnsureInit()
    {
        lock (_initLock)
        {
        if (_player != null) return;

        if (!_nativeLoaded)
        {
            try
            {
                Core.Initialize();
            }
            catch
            {
                var native = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
                Core.Initialize(native);
            }
            _nativeLoaded = true;
        }

        // NOTE: only a known-good option is passed to the LibVLC constructor —
        // an option libvlc does not recognise makes libvlc_new return null and
        // LibVLCSharp throws ("Failed to perform instanciation..."), which
        // silently kills the whole video. Tuning is applied as per-media options
        // instead (unknown media options are ignored).
        _libVlc = new LibVLC("--no-video-title-show");
        // LibVLCSharp formats each log line from a C va_list, which 64-bit Android cannot
        // hand over: listening there crashes the app. The log is for diagnosing on Windows.
        if (!OperatingSystem.IsAndroid())
        {
            _libVlc.Log += (_, e) =>
            {
                if (e.Level >= LogLevel.Warning || e.Message.Contains("vout") ||
                    e.Message.Contains("d3d") || e.Message.Contains("avcodec"))
                    Debug.WriteLine($"[VLC:{e.Level}] {e.Module}: {e.Message}");
            };
        }
        _player = new MediaPlayer(_libVlc);
        _player.EndReached += (_, _) => EndReached?.Invoke();
        }
    }

    public void Dispose()
    {
        try { _tick.Stop(); _tick.Dispose(); } catch { /* ignore */ }
        try { Stop(); } catch { }
        try { lock (_chainLock) _chain.Wait(TimeSpan.FromSeconds(3)); } catch { /* ignore */ }
        _player?.Dispose();
        _libVlc?.Dispose();
        _player = null;
        _libVlc = null;
    }
}
