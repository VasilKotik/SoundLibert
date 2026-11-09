namespace SoundLibert.Domain.Video;

/// <summary>Lines of a video frame to cut from each edge.</summary>
public readonly record struct BlackBars(int Top, int Bottom, int Left, int Right);

/// <summary>
/// Finds black bars baked into a video's frames: a cinema-format picture inside a 16:9
/// file, an old 4:3 clip with black sides. Fed a frame every now and then, it keeps how
/// many lines from each edge were black in EVERY frame so far. That number can only
/// shrink, so a wrong guess on a dark opening corrects itself, and only lines that have
/// stayed dark are ever hidden.
/// </summary>
public sealed class BlackBarMeter
{
    /// <summary>
    /// Average brightness of a line, out of 255, each pixel counted by its brightest
    /// colour channel: compressed black is never quite 0, and luma would call a pure
    /// blue picture black.
    /// </summary>
    private const int Black = 28;

    /// <summary>Black deeper than this share of the frame is a dark scene, not a bar.</summary>
    private const double DeepestBar = 0.25;

    private const int SamplesPerLine = 48;

    /// <summary>
    /// Frames in a row the edges must hold still before they count as bars. Real bars
    /// do; the edge of a picture fading in from black does not.
    /// </summary>
    private const int SteadyFramesNeeded = 6;

    private const int BytesPerPixel = 4;
    private const int Unknown = int.MaxValue;

    private int _width, _height;
    private int _top = Unknown, _bottom = Unknown, _left = Unknown, _right = Unknown;
    private int _steadyFrames;
    private bool _proven;

    /// <summary>What to cut now. All zero until bars have proven themselves.</summary>
    public BlackBars Bars { get; private set; }

    /// <summary>A new clip: forget everything.</summary>
    public void Reset()
    {
        _top = _bottom = _left = _right = Unknown;
        _steadyFrames = 0;
        _proven = false;
        Bars = default;
    }

    /// <summary>Takes one frame: 32-bit blue-green-red-alpha, rows without padding.</summary>
    public void Measure(ReadOnlySpan<byte> pixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || pixels.Length < width * height * BytesPerPixel) return;

        if (width != _width || height != _height)
        {
            Reset();
            _width = width;
            _height = height;
        }

        // The picture has reached the edges: no bars, and that cannot change. The
        // common case, and from here on it costs nothing.
        if (Math.Min(_top, _bottom) < Thinnest(height) && Math.Min(_left, _right) < Thinnest(width))
            return;

        var top = BlackLines(pixels, width, height, rows: true, fromStart: true);
        var bottom = BlackLines(pixels, width, height, rows: true, fromStart: false);
        var left = BlackLines(pixels, width, height, rows: false, fromStart: true);
        var right = BlackLines(pixels, width, height, rows: false, fromStart: false);

        // A completely dark frame says nothing about any edge.
        if (top < 0 && bottom < 0 && left < 0 && right < 0) return;

        var before = (_top, _bottom, _left, _right);
        if (top >= 0) _top = Math.Min(_top, top);
        if (bottom >= 0) _bottom = Math.Min(_bottom, bottom);
        if (left >= 0) _left = Math.Min(_left, left);
        if (right >= 0) _right = Math.Min(_right, right);

        _steadyFrames = before == (_top, _bottom, _left, _right) ? _steadyFrames + 1 : 0;
        if (_steadyFrames >= SteadyFramesNeeded) _proven = true;
        if (!_proven) return;

        var (cutTop, cutBottom) = Pair(_top, _bottom, height);
        var (cutLeft, cutRight) = Pair(_left, _right, width);
        Bars = new BlackBars(cutTop, cutBottom, cutLeft, cutRight);
    }

    /// <summary>
    /// Black lines from one edge, or -1 when the black goes deeper than a bar could. A
    /// line is judged by its average: next to a bright picture, compression leaves a
    /// faint haze and a few lit specks in the bar, and those must not move its edge.
    /// </summary>
    private static int BlackLines(ReadOnlySpan<byte> pixels, int width, int height, bool rows, bool fromStart)
    {
        var lines = rows ? height : width;
        var along = rows ? width : height;
        var deepest = (int)(lines * DeepestBar);
        var step = Math.Max(1, along / SamplesPerLine);

        for (var line = 0; line <= deepest; line++)
        {
            var at = fromStart ? line : lines - 1 - line;
            var sum = 0;
            var count = 0;
            for (var i = step / 2; i < along; i += step)
            {
                var offset = (rows ? at * width + i : i * width + at) * BytesPerPixel;
                sum += Math.Max(pixels[offset], Math.Max(pixels[offset + 1], pixels[offset + 2]));
                count++;
            }
            if (sum > Black * count) return line;
        }
        return -1;
    }

    private static int Thinnest(int lines) => Math.Max(3, lines / 100);

    /// <summary>
    /// What to cut from two opposite edges. Real bars are equal on both sides and thick
    /// enough to notice; one extra line takes the soft edge compression leaves along the
    /// picture.
    /// </summary>
    private static (int Near, int Far) Pair(int near, int far, int lines)
    {
        if (near == Unknown || far == Unknown) return (0, 0);
        if (Math.Min(near, far) < Thinnest(lines)) return (0, 0);
        if (Math.Abs(near - far) > Math.Max(2, lines / 50)) return (0, 0);
        return (near + 1, far + 1);
    }
}
