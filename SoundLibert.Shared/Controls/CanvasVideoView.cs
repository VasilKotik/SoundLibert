using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using SoundLibert.Domain.Video;

namespace SoundLibert.UI.Controls;

/// <summary>
/// A pure-managed video surface for the fullscreen Canvas. Unlike
/// <c>LibVLCSharp.Avalonia.VideoView</c> (a native HWND host, which always paints
/// on top), this control receives decoded frames through libvlc's video
/// callbacks into a <see cref="WriteableBitmap"/>, so it composites with the
/// rest of the UI — buttons and text can be drawn <b>over</b> the video.
///
/// The output buffer size is negotiated through the video-format callback, using
/// the SOURCE video's dimensions (scaled down, same aspect ratio) so VLC never
/// has to stretch the picture — a fixed 16:9 buffer distorted 4:3 / vertical
/// clips.
///
/// Many music videos are cinema-format pictures inside a 16:9 file, with black bars
/// baked into the frame. Behind the fullscreen player those would be hard black
/// bands, so the Canvas measures them and moves them out of the window (see
/// <see cref="TrimBars"/>). Video mode shows the frame as it is.
/// </summary>
public sealed class CanvasVideoView : Control
{
    private const int BytesPerPixel = 4;
    // Read on the libvlc format thread; written from the UI thread.
    private volatile int _maxBufferSide = 960;

    private readonly object _bufferLock = new();
    private IntPtr _buffer;
    private int _frameWidth = 960;
    private int _frameHeight = 540;
    private WriteableBitmap? _bitmap;
    private MediaPlayer? _player;
    private bool _dirty;
    // 1 while an InvalidateVisual is queued: a busy UI thread used to get one
    // post per decoded frame piling up behind it (stutter that snowballed).
    private int _invalidatePending;
    private int _framesSinceLightness;
    // Mirrors HasFrame for the libvlc thread (1 = owner already told).
    private int _frameReported;

    // Black bars baked into the frame (see TrimBars). Measured by the clock, not per
    // drawn frame, so a slow machine decides as quickly as a fast one.
    private const double BarSampleSeconds = 0.15;
    private const double TrimSeconds = 1.2;

    /// <summary>Source lines cut from each edge of the picture right now.</summary>
    private readonly record struct Trim(double Top, double Bottom, double Left, double Right)
    {
        public static Trim Mix(Trim a, Trim b, double t) => new(
            a.Top + (b.Top - a.Top) * t,
            a.Bottom + (b.Bottom - a.Bottom) * t,
            a.Left + (b.Left - a.Left) * t,
            a.Right + (b.Right - a.Right) * t);
    }

    /// <summary>QA: how many new pictures of the clip have reached the screen.</summary>
    internal int FramesDrawn { get; private set; }

    /// <summary>QA: how many the video engine has handed over.</summary>
    internal int FramesDelivered => Volatile.Read(ref _framesDelivered);

    private int _framesDelivered;

    private readonly BlackBarMeter _barMeter = new();
    private long _lastBarSample;
    private volatile bool _newClip = true;
    private Trim _trimFrom, _trimTo, _trimNow;
    private long _trimStarted;

    public static readonly StyledProperty<MediaPlayer?> MediaPlayerProperty =
        AvaloniaProperty.Register<CanvasVideoView, MediaPlayer?>(nameof(MediaPlayer));

    public MediaPlayer? MediaPlayer
    {
        get => GetValue(MediaPlayerProperty);
        set => SetValue(MediaPlayerProperty, value);
    }

    /// <summary>
    /// True when the latest decoded frame is bright. The fullscreen buttons bind
    /// to this (via the view model) to flip between light and dark glyphs so they
    /// stay readable over any video. Uses hysteresis to avoid flicker.
    /// </summary>
    public static readonly StyledProperty<bool> IsLightProperty =
        AvaloniaProperty.Register<CanvasVideoView, bool>(nameof(IsLight));

    public bool IsLight
    {
        get => GetValue(IsLightProperty);
        set => SetValue(IsLightProperty, value);
    }

    /// <summary>
    /// Flips to true on the first decoded frame (the owner resets it to false
    /// for a new clip). The layer fades in on this — not when playback was
    /// merely requested — so there is no empty fade followed by a pop-in.
    /// </summary>
    public static readonly StyledProperty<bool> HasFrameProperty =
        AvaloniaProperty.Register<CanvasVideoView, bool>(nameof(HasFrame),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public bool HasFrame
    {
        get => GetValue(HasFrameProperty);
        set => SetValue(HasFrameProperty, value);
    }

    /// <summary>
    /// Longest side of the frame buffer. Smaller for the Canvas (less scaling
    /// and copying per frame), larger for Video mode. Applies from the next clip.
    /// </summary>
    public static readonly StyledProperty<int> MaxFrameSideProperty =
        AvaloniaProperty.Register<CanvasVideoView, int>(nameof(MaxFrameSide), 960);

    public int MaxFrameSide
    {
        get => GetValue(MaxFrameSideProperty);
        set => SetValue(MaxFrameSideProperty, value);
    }

    /// <summary>
    /// Fill the window with the picture itself, moving black bars baked into the
    /// frame out of view. Off in Video mode, where the whole frame belongs on screen.
    /// </summary>
    public static readonly StyledProperty<bool> TrimBarsProperty =
        AvaloniaProperty.Register<CanvasVideoView, bool>(nameof(TrimBars), true);

    public bool TrimBars
    {
        get => GetValue(TrimBarsProperty);
        set => SetValue(TrimBarsProperty, value);
    }

    /// <summary>
    /// Show the whole frame, bars around it, instead of filling the view. A phone held
    /// upright wants this in Video mode: filling would leave a narrow strip of a wide clip.
    /// </summary>
    public static readonly StyledProperty<bool> FitWholeProperty =
        AvaloniaProperty.Register<CanvasVideoView, bool>(nameof(FitWhole));

    public bool FitWhole
    {
        get => GetValue(FitWholeProperty);
        set => SetValue(FitWholeProperty, value);
    }

    public CanvasVideoView()
    {
        ClipToBounds = true;
    }

    static CanvasVideoView()
    {
        AffectsRender<CanvasVideoView>(MediaPlayerProperty, TrimBarsProperty, FitWholeProperty);
        MediaPlayerProperty.Changed.AddClassHandler<CanvasVideoView>((v, _) => v.AttachPlayer());
        HasFrameProperty.Changed.AddClassHandler<CanvasVideoView>((v, e) =>
        {
            Volatile.Write(ref v._frameReported, e.GetNewValue<bool>() ? 1 : 0);
            if (!e.GetNewValue<bool>()) v._newClip = true;
        });
        MaxFrameSideProperty.Changed.AddClassHandler<CanvasVideoView>((v, e) =>
            v._maxBufferSide = Math.Clamp(e.GetNewValue<int>(), 320, 1920));
    }

    private void AttachPlayer()
    {
        var player = MediaPlayer;
        if (ReferenceEquals(player, _player)) return;

        // libvlc allows video callbacks to be set ONLY before playback starts:
        // changing them while a player is running joins the video-output thread
        // and deadlocks against the UI thread (the app hung on entering
        // fullscreen). The view model prepares the player and attaches the view
        // first, so the player must still be idle here.
        if (player is { IsPlaying: true }) return;

        DetachPlayer();
        _player = player;
        if (player == null) return;

        // The format callback allocates the exact buffer (and negotiates its
        // size); the lock/display callbacks then fill and present each frame.
        player.SetVideoFormatCallbacks(FormatBuffer, null);
        player.SetVideoCallbacks(LockBuffer, null, DisplayFrame);
    }

    /// <summary>
    /// libvlc passes the SOURCE video size in <paramref name="width"/>/<paramref name="height"/>.
    /// We return a buffer with the same aspect ratio (scaled down) so the picture
    /// is never stretched.
    /// </summary>
    private uint FormatBuffer(ref IntPtr opaque, IntPtr chroma,
        ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // The callback must WRITE the desired fourcc into `chroma`; otherwise
        // VLC keeps its own preferred format and never delivers usable frames
        // (symptom: the Canvas stays black). "RV32" = 32-bit BGRA.
        if (chroma != IntPtr.Zero)
        {
            var fourcc = new byte[] { (byte)'R', (byte)'V', (byte)'3', (byte)'2', 0 };
            Marshal.Copy(fourcc, 0, chroma, fourcc.Length);
        }

        var srcW = width == 0 ? 960u : width;
        var srcH = height == 0 ? 540u : height;

        double scale = Math.Min(1.0, (double)_maxBufferSide / Math.Max(srcW, srcH));
        var w = Math.Max(2, (int)Math.Round(srcW * scale));
        var h = Math.Max(2, (int)Math.Round(srcH * scale));
        w &= ~1; // even width
        h &= ~1; // even height

        lock (_bufferLock)
        {
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
            }

            _frameWidth = w;
            _frameHeight = h;
            _buffer = Marshal.AllocHGlobal(w * h * BytesPerPixel);
            _newClip = true;

            // The frame size changed: drop the old surface so Render recreates
            // it. Do NOT dispose it here — this callback runs on a libvlc
            // thread while the UI thread may be drawing the bitmap right now
            // (a DrawImage on a disposed bitmap crashed the app). Hand it to
            // the UI thread instead, which serialises it after any in-flight
            // Render has finished.
            var oldBitmap = _bitmap;
            _bitmap = null;
            _dirty = false;
            if (oldBitmap != null)
                Dispatcher.UIThread.Post(oldBitmap.Dispose, DispatcherPriority.Background);
        }

        System.Diagnostics.Debug.WriteLine($"[Canvas] format {srcW}x{srcH} -> {w}x{h} RV32");

        // Give VLC the distortion-free buffer size; it will scale to fit.
        width = (uint)w;
        height = (uint)h;
        pitches = (uint)(w * BytesPerPixel);
        lines = (uint)h;
        return 1;
    }

    private void DetachPlayer()
    {
        WriteableBitmap? oldBitmap = null;
        lock (_bufferLock)
        {
            // The frame buffer stays allocated. A player told to stop can still
            // be inside a frame for a moment on libvlc's thread, and writing into
            // a freed buffer or a null plane takes the whole process down. The
            // next clip's format callback replaces it.
            oldBitmap = _bitmap;
            _bitmap = null;
        }
        // Dispose on the UI thread so it can never race an in-flight Render.
        if (oldBitmap != null && Dispatcher.UIThread.CheckAccess())
            oldBitmap.Dispose();
        else if (oldBitmap != null)
            Dispatcher.UIThread.Post(oldBitmap.Dispose, DispatcherPriority.Background);
        _player = null;
        _dirty = false;
        IsLight = false;
    }

    private IntPtr LockBuffer(IntPtr opaque, IntPtr planes)
    {
        // libvlc expects the lock callback to fill the planes array and return
        // it (NOT the raw buffer) — returning the buffer directly crashes VLC.
        lock (_bufferLock)
            Marshal.WriteIntPtr(planes, 0, _buffer);
        return planes;
    }

    private void DisplayFrame(IntPtr opaque, IntPtr picture)
    {
        // Called on a libvlc thread — never touch UI objects here.
        _dirty = true;
        Interlocked.Increment(ref _framesDelivered);
        // First frame is detected HERE, not in Render: the layer stays at
        // Opacity 0 until this fires, and Avalonia never renders a fully
        // transparent visual — detecting it in Render deadlocked (endless loader).
        if (Interlocked.Exchange(ref _frameReported, 1) == 0)
            Dispatcher.UIThread.Post(() => HasFrame = true);
        if (Interlocked.Exchange(ref _invalidatePending, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _invalidatePending, 0);
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    public override void Render(DrawingContext context)
    {
        WriteableBitmap? bitmap;
        int w, h;
        var newFrame = false;

        lock (_bufferLock)
        {
            // Nothing decoded yet — nothing to draw. (Must still be able to
            // CREATE the bitmap here: the first Render after FormatBuffer has a
            // buffer but no bitmap, and bailing out on _bitmap == null left the
            // Canvas permanently blank.)
            if (_buffer == IntPtr.Zero) return;
            w = _frameWidth;
            h = _frameHeight;

            if (_bitmap == null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
            {
                // Defer the old surface's disposal too: the compositor may still
                // hold a reference to it for the frame it is finishing.
                var oldSurface = _bitmap;
                _bitmap = new WriteableBitmap(
                    new PixelSize(w, h), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Opaque);
                _dirty = true;   // the new surface must be filled before it is shown
                if (oldSurface != null)
                    Dispatcher.UIThread.Post(oldSurface.Dispose, DispatcherPriority.Background);
            }
            bitmap = _bitmap;

            // Only copy the source bytes when a new frame actually arrived.
            if (_dirty)
            {
                using (var fb = bitmap.Lock())
                {
                    var bytes = w * h * BytesPerPixel;
                    unsafe
                    {
                        Buffer.MemoryCopy((void*)_buffer, (void*)fb.Address, bytes, bytes);
                    }
                }
                _dirty = false;
                newFrame = true;
                FramesDrawn++;
            }
        }

        // Luma only changes with the picture: sample new frames, ~4x a second.
        if (newFrame && ++_framesSinceLightness >= 8)
        {
            _framesSinceLightness = 0;
            UpdateLightness();
        }

        // Waits for the new clip's first frame: the old one may still be fading out,
        // and must not jump back to its uncut size on the way.
        if (_newClip && HasFrame)
        {
            _newClip = false;
            _barMeter.Reset();
            _trimFrom = _trimTo = _trimNow = default;
        }
        if (newFrame && Stopwatch.GetElapsedTime(_lastBarSample).TotalSeconds >= BarSampleSeconds)
        {
            _lastBarSample = Stopwatch.GetTimestamp();
            MeasureBars();
        }
        var trimMoving = UpdateTrim(w, h);

        // UniformToFill of the picture without its bars: preserve the aspect ratio
        // and crop the overflow (a plain Rect(0,0,Bounds) would distort the frame).
        var pictureW = w - _trimNow.Left - _trimNow.Right;
        var pictureH = h - _trimNow.Top - _trimNow.Bottom;
        var scale = FitWhole
            ? Math.Min(Bounds.Width / pictureW, Bounds.Height / pictureH)
            : Math.Max(Bounds.Width / pictureW, Bounds.Height / pictureH);
        var x = (Bounds.Width - pictureW * scale) / 2.0 - _trimNow.Left * scale;
        var y = (Bounds.Height - pictureH * scale) / 2.0 - _trimNow.Top * scale;
        // Fitted whole, the bars cut from the picture would still show around it: clip them.
        using var fitted = FitWhole && TrimBars
            ? context.PushClip(new Rect(x + _trimNow.Left * scale, y + _trimNow.Top * scale, pictureW * scale, pictureH * scale))
            : default(DrawingContext.PushedState?);
        // A surface swapped by a format change can be disposed between frames:
        // skipping one frame is fine, crashing the renderer is not.
        try { context.DrawImage(bitmap, new Rect(x, y, w * scale, h * scale)); }
        catch (ObjectDisposedException) { InvalidateVisual(); }

        // A paused clip delivers no frames; the move still has to finish.
        if (trimMoving) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
    }

    private void MeasureBars()
    {
        lock (_bufferLock)
        {
            if (_buffer == IntPtr.Zero) return;
            unsafe
            {
                _barMeter.Measure(
                    new ReadOnlySpan<byte>((void*)_buffer, _frameWidth * _frameHeight * BytesPerPixel),
                    _frameWidth, _frameHeight);
            }
        }
    }

    /// <summary>Moves the cut towards what is known now. True while it is still moving.</summary>
    private bool UpdateTrim(int w, int h)
    {
        var bars = TrimBars ? _barMeter.Bars : default;
        var target = new Trim(bars.Top, bars.Bottom, bars.Left, bars.Right);

        if (target != _trimTo)
        {
            _trimFrom = _trimNow;
            _trimTo = target;
            _trimStarted = Stopwatch.GetTimestamp();
            if (bars != default)
                Trace.WriteLine($"[Canvas] black bars cut: top {bars.Top}, bottom {bars.Bottom}, left {bars.Left}, right {bars.Right} of {w}x{h}");
        }

        if (_trimNow == _trimTo) return false;

        var t = Stopwatch.GetElapsedTime(_trimStarted).TotalSeconds / TrimSeconds;
        if (t >= 1 || !Motion.Enabled)
        {
            _trimNow = _trimTo;
            return false;
        }
        _trimNow = Trim.Mix(_trimFrom, _trimTo, t * t * (3 - 2 * t));
        return true;
    }

    /// <summary>
    /// Averages a sparse sample of the frame's luma and flips <see cref="IsLight"/>
    /// with hysteresis (bright above ~150, dark below ~120, out of 255) so the
    /// UI contrast adapts to the video without flickering.
    /// </summary>
    private void UpdateLightness()
    {
        long sum = 0;
        int count = 0;
        lock (_bufferLock)
        {
            if (_buffer == IntPtr.Zero) return;
            // Sample ~every 37th pixel, but keep the step odd and coprime-ish so we
            // don't lock onto a single column/row alignment. The size is read under
            // the lock: a format change can swap in a smaller buffer at any moment.
            var total = _frameWidth * _frameHeight;
            unsafe
            {
                var pixels = (byte*)_buffer;
                for (int i = 0; i < total; i += 37)
                {
                    var p = pixels + i * BytesPerPixel;
                    // BT.601 luma approximation: (54R + 183G + 19B) >> 8.
                    sum += (p[2] * 54 + p[1] * 183 + p[0] * 19) >> 8;
                    count++;
                }
            }
        }

        if (count == 0) return;
        var avg = sum / (double)count;
        var light = avg > (IsLight ? 120 : 150);
        if (light != IsLight) IsLight = light;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Re-attach the player after a detach (e.g. the fullscreen overlay was
        // hidden and shown again) — otherwise the video would never come back.
        AttachPlayer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        DetachPlayer();
    }
}
