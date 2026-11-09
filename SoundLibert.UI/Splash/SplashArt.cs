using SkiaSharp;

namespace SoundLibert.UI.Splash;

/// <summary>
/// The opening of the app: the sound wave fills with light from its left end, the light runs on
/// into the bird and out to the tips of its wings, and the name follows. Drawn with Skia alone, so
/// it can play on the render thread while the app builds its window, and so a test can draw any
/// moment of it into a picture.
/// </summary>
internal sealed class SplashArt : IDisposable
{
    // The timeline, in seconds from the first frame.
    public const double RevealStart = 0.12;
    public const double RevealLength = 1.1;
    public const double NameStart = 0.85;
    public const double NameLength = 0.55;

    /// <summary>The app may take over once the bird is drawn and has shone for a moment.</summary>
    public const double ShortestStay = RevealStart + RevealLength + 0.18;

    public const double ExitLength = 0.42;

    private const float MapScale = 0.5f;   // pixels of the distance map per unit of the drawing
    private const int Pad = 4;
    private const float Softness = 0.05f;  // how long the leading edge of the light is, as a share of the drawing

    private static readonly SKColor Ground = new(0x0B, 0x0C, 0x11);
    private static readonly SKColor Ink = new(0xFF, 0xF4, 0xEC);
    private static readonly SKColor Glow = new(0xFF, 0x86, 0x38);
    private static readonly SKColor Hot = new(0xFF, 0xD9, 0xBC);

    private readonly SKPath _mark;
    private readonly SKRect _bounds;
    private readonly int _mapWidth, _mapHeight;
    private readonly float[] _distance;
    private readonly SKBitmap _shown;
    private readonly SKBitmap _front;
    private SKTypeface? _typeface;
    private bool _typefaceAsked;

    private static Task<SplashArt>? _ahead;

    /// <summary>
    /// Starts the drawing's preparation on the thread pool at the very start of the app, so the
    /// opening's window does not wait for it.
    /// </summary>
    public static void PrepareAhead(string markData) => _ahead = Task.Run(() => new SplashArt(markData, null));

    /// <summary>The prepared drawing, or a new one.</summary>
    public static SplashArt Take(string markData)
    {
        var ahead = _ahead;
        _ahead = null;
        try { return ahead?.Result ?? new SplashArt(markData, null); }
        catch { return new SplashArt(markData, null); }
    }

    /// <summary>Gives the font of the name the first time it is drawn (by then the app has its fonts).</summary>
    public Func<SKTypeface?>? NameFont { get; set; }

    public SplashArt(string markData, SKTypeface? typeface)
    {
        _mark = SKPath.ParseSvgPathData(markData) ?? throw new ArgumentException("not path data", nameof(markData));
        _mark.FillType = SKPathFillType.EvenOdd;
        _bounds = _mark.TightBounds;
        _typeface = typeface;

        _mapWidth = (int)Math.Ceiling(_bounds.Width * MapScale) + 2 * Pad;
        _mapHeight = (int)Math.Ceiling(_bounds.Height * MapScale) + 2 * Pad;
        _distance = DistanceAlongTheDrawing();
        _shown = new SKBitmap(new SKImageInfo(_mapWidth, _mapHeight, SKColorType.Alpha8, SKAlphaType.Premul));
        _front = new SKBitmap(new SKImageInfo(_mapWidth, _mapHeight, SKColorType.Alpha8, SKAlphaType.Premul));
    }

    /// <summary>
    /// How far along the drawing every pixel lies, walking through the ink from the left end of
    /// the wave: the light reaches each part of the bird in the order a pen would.
    /// </summary>
    private float[] DistanceAlongTheDrawing()
    {
        int w = _mapWidth, h = _mapHeight, n = w * h;
        var ink = new bool[n];
        using (var mask = new SKBitmap(new SKImageInfo(w, h, SKColorType.Alpha8, SKAlphaType.Premul)))
        {
            using (var canvas = new SKCanvas(mask))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Translate(Pad, Pad);
                canvas.Scale(MapScale);
                canvas.Translate(-_bounds.Left, -_bounds.Top);
                using var paint = new SKPaint { IsAntialias = true, Color = SKColors.Black };
                canvas.DrawPath(_mark, paint);
            }
            var row = mask.RowBytes;
            var bytes = mask.GetPixelSpan();
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    ink[y * w + x] = bytes[y * row + x] > 60;
        }

        // The pen starts at the left end of the wave.
        var seed = -1;
        for (var x = 0; x < w && seed < 0; x++)
        {
            var best = -1;
            for (var y = 0; y < h; y++)
                if (ink[y * w + x] && (best < 0 || Math.Abs(y - h / 2) < Math.Abs(best / w - h / 2))) best = y * w + x;
            seed = best;
        }

        var dist = new float[n];
        Array.Fill(dist, float.MaxValue);
        var queue = new PriorityQueue<int, float>();
        dist[seed] = 0;
        queue.Enqueue(seed, 0);
        ReadOnlySpan<(int Dx, int Dy, float Len)> steps = new[]
        {
            (1, 0, 1f), (-1, 0, 1f), (0, 1, 1f), (0, -1, 1f),
            (1, 1, 1.4142f), (1, -1, 1.4142f), (-1, 1, 1.4142f), (-1, -1, 1.4142f)
        };
        while (queue.TryDequeue(out var at, out var d))
        {
            if (d > dist[at]) continue;
            int ax = at % w, ay = at / w;
            foreach (var (dx, dy, len) in steps)
            {
                int bx = ax + dx, by = ay + dy;
                if (bx < 0 || by < 0 || bx >= w || by >= h) continue;
                var b = by * w + bx;
                // Through the ink, or slowly across a gap a hair wide.
                var nd = d + len * (ink[b] ? 1 : 25);
                if (nd >= dist[b]) continue;
                dist[b] = nd;
                queue.Enqueue(b, nd);
            }
        }

        float longest = 0;
        for (var i = 0; i < n; i++) if (ink[i]) longest = Math.Max(longest, dist[i]);

        // Off the ink, each pixel takes the value of the nearest ink, so a smooth sampling at the
        // edge of a line reads the line itself.
        var result = new float[n];
        var from = new int[n];
        Array.Fill(from, -1);
        var wave = new Queue<int>();
        for (var i = 0; i < n; i++)
            if (ink[i]) { from[i] = i; wave.Enqueue(i); }
        while (wave.Count > 0)
        {
            var at = wave.Dequeue();
            int ax = at % w, ay = at / w;
            foreach (var (dx, dy, _) in steps)
            {
                int bx = ax + dx, by = ay + dy;
                if (bx < 0 || by < 0 || bx >= w || by >= h) continue;
                var b = by * w + bx;
                if (from[b] >= 0) continue;
                from[b] = from[at];
                wave.Enqueue(b);
            }
        }
        for (var i = 0; i < n; i++) result[i] = from[i] >= 0 ? dist[from[i]] / longest : 1;
        return result;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);
    private static double EaseInOut(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    private static double EaseInOutSine(double t) => -(Math.Cos(Math.PI * t) - 1) / 2;
    private static double SmoothStep(double a, double b, double x) { var t = Clamp01((x - a) / (b - a)); return t * t * (3 - 2 * t); }

    /// <summary>Where the light is: what is lit, and the bright edge running ahead.</summary>
    private void UpdateMasks(double progress)
    {
        var shownRow = _shown.RowBytes;
        var frontRow = _front.RowBytes;
        var shown = _shown.GetPixelSpan();
        var front = _front.GetPixelSpan();
        var edge = progress - Softness * 0.45;
        for (var y = 0; y < _mapHeight; y++)
            for (var x = 0; x < _mapWidth; x++)
            {
                double d = _distance[y * _mapWidth + x];
                var lit = 1 - SmoothStep(progress - Softness, progress, d);
                var k = (d - edge) / (Softness * 0.75);
                var hot = Math.Exp(-k * k) * (d <= progress + 0.01 ? 1 : 0);
                shown[y * shownRow + x] = (byte)(lit * 255);
                front[y * frontRow + x] = (byte)(Math.Min(1, hot) * 255);
            }
        _shown.NotifyPixelsChanged();
        _front.NotifyPixelsChanged();
    }

    /// <summary>
    /// Where the drawing stands, a third of the window wide and a little above the middle:
    /// its centre and its width. The welcome screen takes the bird over from here.
    /// </summary>
    public (float X, float Y, float Width) LogoAt(float width, float height)
    {
        var scale = Math.Min(width * 0.27f / _bounds.Width, height * 0.34f / _bounds.Height);
        return (width / 2f, height * 0.44f, _bounds.Width * scale);
    }

    /// <param name="t">Seconds since the first frame.</param>
    /// <param name="exit">0 while it plays, rising to 1 as it hands over to the app.</param>
    public void Draw(SKCanvas canvas, float width, float height, double t, double exit)
    {
        var appear = EaseOut(Clamp01(t / 0.35));
        var leave = 1 - EaseInOut(Clamp01(exit));
        DrawGround(canvas, width, height, t, appear * leave);

        var progress = 1.08 * EaseInOutSine(Clamp01((t - RevealStart) / RevealLength));
        var drawn = progress >= 1.0 + Softness;

        var (cx, cy, logoWidth) = LogoAt(width, height);
        var scale = (float)(logoWidth / _bounds.Width * (1 + 0.05 * EaseInOut(exit)));
        canvas.Save();
        canvas.Translate(cx, cy);
        canvas.Scale(scale);
        canvas.Translate(-_bounds.MidX, -_bounds.MidY);

        UpdateMasks(progress);
        var dest = new SKRect(_bounds.Left - Pad / MapScale, _bounds.Top - Pad / MapScale,
            _bounds.Left + (_mapWidth - Pad) / MapScale, _bounds.Top + (_mapHeight - Pad) / MapScale);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        using var shownImage = SKImage.FromBitmap(_shown);
        using var frontImage = SKImage.FromBitmap(_front);

        // A warm halo around what is lit, breathing once the bird is drawn.
        var breathe = drawn ? 0.5 + 0.5 * Math.Sin((t - RevealStart - RevealLength) * Math.PI * 2 / 2.2 - Math.PI / 2) : 0;
        var haloStrength = (0.42 + 0.18 * breathe) * leave;
        using (var halo = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(14, 14), Color = SKColors.White.WithAlpha((byte)(255 * haloStrength)) })
        {
            canvas.SaveLayer(halo);
            canvas.ClipPath(_mark, SKClipOperation.Intersect, true);
            using var paint = new SKPaint { Color = Glow };
            canvas.DrawImage(shownImage, dest, sampling, paint);
            canvas.Restore();
        }

        // The line itself.
        canvas.Save();
        canvas.ClipPath(_mark, SKClipOperation.Intersect, true);
        using (var paint = new SKPaint { Color = Ink.WithAlpha((byte)(255 * leave)) })
            canvas.DrawImage(shownImage, dest, sampling, paint);

        // The bright edge, sharp on the line...
        if (!drawn)
        {
            using var paint = new SKPaint { Color = Hot.WithAlpha((byte)(255 * leave)) };
            canvas.DrawImage(frontImage, dest, sampling, paint);
        }
        canvas.Restore();

        // ...and glowing around it.
        if (!drawn)
        {
            using var bloom = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(9, 9), BlendMode = SKBlendMode.Plus };
            canvas.SaveLayer(bloom);
            canvas.ClipPath(_mark, SKClipOperation.Intersect, true);
            using var paint = new SKPaint { Color = Glow.WithAlpha((byte)(230 * leave)) };
            canvas.DrawImage(frontImage, dest, sampling, paint);
            canvas.Restore();
        }
        canvas.Restore();

        DrawName(canvas, cx, cy + _bounds.Height / 2 * scale, scale, t, leave);
    }

    private static void DrawGround(SKCanvas canvas, float width, float height, double t, double alpha)
    {
        if (alpha <= 0) return;
        // Windows 11 rounds window corners by eight pixels; the app's window under this one does too.
        var frame = new SKRoundRect(new SKRect(0, 0, width, height), 8, 8);
        canvas.Save();
        canvas.ClipRoundRect(frame, SKClipOperation.Intersect, true);
        using (var ground = new SKPaint { Color = Ground.WithAlpha((byte)(255 * alpha)) })
            canvas.DrawRect(0, 0, width, height, ground);

        // The same living colour as the app behind it, drifting slowly.
        var drift = (float)Math.Sin(t * 0.7) * 0.02f;
        Light(canvas, width * (0.30f + drift), height * 0.68f, width * 0.62f, new SKColor(0xFF, 0x6A, 0x1A), 0.20 * alpha);
        Light(canvas, width * (0.66f - drift), height * 0.18f, width * 0.46f, new SKColor(0xD9, 0x2C, 0x6B), 0.10 * alpha);
        Light(canvas, width * (0.84f + drift), height * 0.80f, width * 0.52f, new SKColor(0x6D, 0x3B, 0xF0), 0.13 * alpha);
        canvas.Restore();
    }

    private static void Light(SKCanvas canvas, float x, float y, float radius, SKColor color, double alpha)
    {
        using var paint = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Plus };
        paint.Shader = SKShader.CreateRadialGradient(new SKPoint(x, y), radius,
            new[] { color.WithAlpha((byte)(255 * alpha)), color.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
        canvas.DrawCircle(x, y, radius, paint);
    }

    /// <summary>The name, uncovered from left to right as it rises into place.</summary>
    private void DrawName(SKCanvas canvas, float cx, float drawingBottom, float scale, double t, double leave)
    {
        var q = EaseOut(Clamp01((t - NameStart) / NameLength));
        if (q <= 0 || leave <= 0) return;
        if (_typeface == null && !_typefaceAsked)
        {
            _typefaceAsked = true;
            try { _typeface = NameFont?.Invoke(); } catch { _typeface = null; }
        }

        const string name = "SoundLibert";
        using var font = new SKFont(_typeface ?? SKTypeface.Default, Math.Max(18, 66 * scale)) { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true };
        var textWidth = font.MeasureText(name);
        var x = cx - textWidth / 2;
        var y = drawingBottom + 70 * scale + font.Size + (float)(1 - q) * 12;

        var edge = x - 60 + (textWidth + 120) * (float)q;
        using var paint = new SKPaint { IsAntialias = true };
        paint.Shader = SKShader.CreateLinearGradient(new SKPoint(edge - 60, 0), new SKPoint(edge, 0),
            new[] { Ink.WithAlpha((byte)(255 * leave)), Ink.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
        canvas.DrawText(name, x, y, SKTextAlign.Left, font, paint);
    }

    public void Dispose()
    {
        _mark.Dispose();
        _shown.Dispose();
        _front.Dispose();
    }
}
