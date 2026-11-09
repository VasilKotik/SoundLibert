using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SoundLibert.UI.Services;

/// <summary>
/// Downloads a track's artwork and derives a representative colour from it.
/// Used by Dynamic Now Playing: the monochrome shell is washed with a very
/// subtle tint of the current cover, so the UI breathes with the music
/// without turning into RGB noise.
///
/// Results are cached per URL (bounded) because the player often re-selects
/// the same track and bitmaps must not be re-decoded every time.
/// </summary>
public sealed class ArtworkService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private readonly ConcurrentDictionary<string, ArtworkInfo> _cache = new();
    private readonly ConcurrentQueue<string> _order = new();
    private const int MaxCache = 48;

    public sealed record ArtworkInfo(Bitmap? Image, Color Color);

    public async Task<ArtworkInfo> GetAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new ArtworkInfo(null, Colors.White);

        // Ask the CDN for a large cover so the fullscreen player stays sharp.
        url = ThumbnailUrl.AtSize(url, 720);

        if (_cache.TryGetValue(url, out var cached))
            return cached;

        try
        {
            var bytes = await Http.GetByteArrayAsync(url, ct);

            // Off the UI thread: a 720 px cover per track change is a visible hitch.
            var info = await Task.Run(() =>
            {
                using var ms = new MemoryStream(bytes);
                // Decode a small copy just for colour analysis, and keep the full
                // bitmap for display.
                var image = new Bitmap(ms);
                ms.Position = 0;
                return new ArtworkInfo(image, EnsureVivid(ExtractDominantColor(ms)));
            }, ct);
            Remember(url, info);
            return info;
        }
        catch (Exception ex)
        {
            // Not remembered: one network hiccup must not leave this track
            // without a cover for the rest of the session.
            System.Diagnostics.Trace.WriteLine($"[Artwork] Failed for {url}: {ex.Message}");
            return new ArtworkInfo(null, Colors.White);
        }
    }

    private void Remember(string url, ArtworkInfo info)
    {
        _cache[url] = info;
        _order.Enqueue(url);
        while (_order.Count > MaxCache && _order.TryDequeue(out var old))
            _cache.TryRemove(old, out _);
    }

    /// <summary>
    /// Forgets every cached artwork (Settings → Clear cache). Bitmaps are shared
    /// as Image.Source, so they are only evicted — never disposed here.
    /// </summary>
    public void Clear()
    {
        _cache.Clear();
        while (_order.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Colour pick: down-sample the image to a coarse grid, drop near-black /
    /// near-white / low-saturation pixels (usually background and highlights),
    /// then average what is left. Cheap and good enough for an ambient wash.
    /// </summary>
    private static Color ExtractDominantColor(Stream stream)
    {
        using var wb = WriteableBitmap.Decode(stream);
        using var fb = wb.Lock();

        int width = fb.Size.Width;
        int height = fb.Size.Height;
        int stride = fb.RowBytes;
        if (width <= 0 || height <= 0) return Colors.White;

        // Work straight on the locked buffer — no byte[] copy (that allocation was
        // a large GC cost on every track change for a high-res cover).
        var length = (long)stride * height;
        bool bgra = fb.Format == PixelFormat.Bgra8888;

        long rSum = 0, gSum = 0, bSum = 0, count = 0;
        int stepX = Math.Max(1, width / 40);
        int stepY = Math.Max(1, height / 40);

        unsafe
        {
            byte* buffer = (byte*)fb.Address;

            for (int y = 0; y < height; y += stepY)
            {
                long row = (long)y * stride;
                for (int x = 0; x < width; x += stepX)
                {
                    long i = row + (long)x * 4;
                    if (i + 3 >= length) continue;

                    byte b0 = buffer[i];
                    byte b1 = buffer[i + 1];
                    byte b2 = buffer[i + 2];
                    byte a = buffer[i + 3];
                    if (a < 40) continue;

                    byte r, g, b;
                    if (bgra) { b = b0; g = b1; r = b2; }
                    else { r = b0; g = b1; b = b2; }

                    int max = Math.Max(r, Math.Max(g, b));
                    int min = Math.Min(r, Math.Min(g, b));
                    int sum = r + g + b;
                    int sat = max - min;

                    // Skip near-black, near-white and grey pixels.
                    if (sum < 90 || sum > 690) continue;
                    if (sat < 28) continue;

                    rSum += r; gSum += g; bSum += b; count++;
                }
            }

            if (count == 0)
            {
                // Fallback: overall average of everything visible.
                for (int y = 0; y < height; y += stepY)
                {
                    long row = (long)y * stride;
                    for (int x = 0; x < width; x += stepX)
                    {
                        long i = row + (long)x * 4;
                        if (i + 3 >= length) continue;
                        if (buffer[i + 3] < 40) continue;
                        byte b0 = buffer[i], b1 = buffer[i + 1], b2 = buffer[i + 2];
                        if (bgra) { bSum += b0; gSum += b1; rSum += b2; }
                        else { rSum += b0; gSum += b1; bSum += b2; }
                        count++;
                    }
                }
            }
        }

        if (count == 0) return Colors.White;
        return Color.FromRgb((byte)(rSum / count), (byte)(gSum / count), (byte)(bSum / count));
    }

    /// <summary>Pushes a colour away from muddy grey so the ambient tint reads on a dark UI.</summary>
    private static Color EnsureVivid(Color c)
    {
        var r = c.R / 255.0;
        var g = c.G / 255.0;
        var b = c.B / 255.0;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var s = max == min ? 0 : (l > 0.5 ? (max - min) / (2 - max - min) : (max - min) / (max + min));

        // Nudge saturation up but keep the colour DARK so the tint reads as an
        // ambient mood on a near-black UI instead of washing it out.
        s = Math.Clamp(s * 1.25 + 0.05, 0, 1);
        l = 0.4;

        return FromHsl(c, s, l);
    }

    private static Color FromHsl(Color c, double s, double l)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double h = 0;
        double d = max - min;
        if (d > 0)
        {
            if (max == r) h = ((g - b) / d) % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
            if (h < 0) h += 1;
        }

        if (s == 0)
            return Color.FromRgb((byte)(l * 255), (byte)(l * 255), (byte)(l * 255));

        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        return Color.FromRgb(
            (byte)(HueToRgb(p, q, h + 1.0 / 3) * 255),
            (byte)(HueToRgb(p, q, h) * 255),
            (byte)(HueToRgb(p, q, h - 1.0 / 3) * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}
