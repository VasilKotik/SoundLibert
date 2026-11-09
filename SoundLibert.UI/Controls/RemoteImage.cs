using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace SoundLibert.UI.Controls;

/// <summary>
/// An <see cref="Image"/> that loads its bitmap from a URL asynchronously.
/// Avalonia cannot bind a URL string to Image.Source directly, so this control
/// owns the cover pipeline for every list and card in the app:
/// <list type="bullet">
/// <item>memory cache (decoded bitmaps, bounded),</item>
/// <item>disk cache of the downloaded bytes, so a restart shows Home / pages
/// from disk instead of re-downloading every cover,</item>
/// <item>one in-flight download per URL (an album page's 20 rows sharing the
/// album cover used to fire 20 identical requests),</item>
/// <item>a short fade-in for covers that were not already in memory,</item>
/// <item>nothing loaded until the cover is on screen or about to be: a page of shelves
/// holds far more covers than anyone reaches, and the ones being looked at used to
/// wait in line behind them.</item>
/// </list>
/// Stale loads are discarded via a per-instance generation counter (list
/// containers can be recycled and get a different URL).
/// </summary>
public class RemoteImage : Image
{
    private const int MaxCache = 300;
    private const int MaxDecodeSize = 256;
    private const long DiskBudgetBytes = 300L * 1024 * 1024;
    private const long DiskTrimTargetBytes = 220L * 1024 * 1024;
    private const int LoadRetries = 2;
    private const int RetrySeconds = 6;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly ConcurrentDictionary<string, Bitmap> Cache = new();
    private static readonly ConcurrentQueue<string> Order = new();
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> InFlight = new();
    private static readonly SemaphoreSlim NetworkGate = new(8);
    private static readonly string DiskDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoundLibert", "cache", "covers");
    private static int _diskTrimStarted;

    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<RemoteImage, string?>(nameof(Url));

    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    private int _generation;

    /// <summary>Covers further away than this many screens wait their turn.</summary>
    private const double LoadAheadScreens = 1;

    private static readonly HashSet<RemoteImage> Waiting = new();
    private static readonly List<RemoteImage> WaitingNow = new();
    private string? _waitingUrl;

    static RemoteImage()
    {
        UrlProperty.Changed.AddClassHandler<RemoteImage>((img, e) => img.OnUrlChanged(e));
    }

    public RemoteImage()
    {
        Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(280) }
        };
        if (Interlocked.Exchange(ref _diskTrimStarted, 1) == 0)
            _ = Task.Run(TrimDiskCache);
    }

    private void OnUrlChanged(AvaloniaPropertyChangedEventArgs e)
    {
        var url = e.NewValue as string;
        var generation = ++_generation;
        StopWaiting();

        if (string.IsNullOrWhiteSpace(url))
        {
            Source = null;
            return;
        }

        // Request a slightly larger source than the decode size so the CDN
        // image stays crisp after down-scaling (and on high-DPI displays).
        url = SoundLibert.UI.Services.ThumbnailUrl.AtSize(url, 400);

        if (Cache.TryGetValue(url, out var cached))
        {
            SetImmediate(cached);
            return;
        }

        // Not in memory: clear the recycled container's previous cover (it
        // showed the wrong art until the new one arrived) and fade the new one in.
        SetImmediate(null);
        Opacity = 0;

        if (IsNearScreen()) _ = ShowAsync(url, generation);
        else StartWaiting(url);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && _waitingUrl != null) NearScreen.SomethingMoved();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_waitingUrl != null) JoinWaiting();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Waiting.Remove(this);
    }

    private void StartWaiting(string url)
    {
        _waitingUrl = url;
        if (TopLevel.GetTopLevel(this) != null) JoinWaiting();
    }

    private void StopWaiting()
    {
        _waitingUrl = null;
        Waiting.Remove(this);
    }

    private void JoinWaiting()
    {
        Waiting.Add(this);
        NearScreen.Watch(LookAtWaiting);
    }

    private static bool LookAtWaiting()
    {
        WaitingNow.AddRange(Waiting);
        foreach (var image in WaitingNow)
        {
            if (image._waitingUrl is not { } url || !image.IsNearScreen()) continue;
            image.StopWaiting();
            _ = image.ShowAsync(url, image._generation);
        }
        WaitingNow.Clear();
        return Waiting.Count > 0;
    }

    private bool IsNearScreen() => NearScreen.GapInScreens(this) is <= LoadAheadScreens;

    private async Task ShowAsync(string url, int generation)
    {
        // A slow moment of the network must not leave a card blank for as long as
        // the page stays open: the cover is asked for again, a little later each time.
        for (var attempt = 0; attempt <= LoadRetries; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(RetrySeconds * attempt));
                if (generation != _generation) return;
            }

            var task = InFlight.GetOrAdd(url, LoadAsync);
            try
            {
                var bitmap = await task;
                if (generation != _generation) return;
                Source = bitmap;
                Opacity = 1;
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RemoteImage] Failed {url}: {ex.Message}");
                if (generation != _generation) return;
            }
            finally
            {
                // Only this exact task (a failed load must be retryable).
                InFlight.TryRemove(new KeyValuePair<string, Task<Bitmap?>>(url, task));
            }
        }

        Opacity = 1;
    }

    /// <summary>Shows a bitmap (or nothing) without the fade.</summary>
    private void SetImmediate(Bitmap? bitmap)
    {
        var transitions = Transitions;
        Transitions = null;
        Source = bitmap;
        Opacity = 1;
        Transitions = transitions;
    }

    /// <summary>Disk first, then network; the result is cached in memory.</summary>
    private static async Task<Bitmap?> LoadAsync(string url)
    {
        var path = DiskPath(url);
        byte[]? bytes = null;
        if (File.Exists(path))
        {
            try
            {
                bytes = await File.ReadAllBytesAsync(path);
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            }
            catch { bytes = null; }
        }

        if (bytes is { Length: > 0 })
        {
            try
            {
                var cachedBitmap = await DecodeAsync(bytes);
                if (cachedBitmap != null) return Remember(url, cachedBitmap);
            }
            catch
            {
                // A truncated/corrupt cache file (crash mid-write, disk issue)
                // must not break this cover forever: drop it and re-download.
                try { File.Delete(path); } catch { }
            }
        }

        await NetworkGate.WaitAsync();
        try { bytes = await Http.GetByteArrayAsync(url); }
        finally { NetworkGate.Release(); }

        var bitmap = await DecodeAsync(bytes);
        if (bitmap == null) return null;
        _ = SaveToDiskAsync(path, bytes);
        return Remember(url, bitmap);
    }

    /// <summary>Decodes off the UI thread — covers arrive in bulk on the Home page.</summary>
    private static Task<Bitmap?> DecodeAsync(byte[] bytes) => Task.Run(() =>
    {
        using var ms = new MemoryStream(bytes);
        return Decode(ms);
    });

    private static string DiskPath(string url)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)));
        return Path.Combine(DiskDir, hash[..2], hash + ".img");
    }

    private static async Task SaveToDiskAsync(string path, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[RemoteImage] disk write failed: {ex.Message}");
        }
    }

    /// <summary>Keeps the disk cache under budget, dropping least recently used covers.</summary>
    private static void TrimDiskCache()
    {
        try
        {
            if (!Directory.Exists(DiskDir)) return;
            var files = new DirectoryInfo(DiskDir).GetFiles("*.img", SearchOption.AllDirectories);
            var total = files.Sum(f => f.Length);
            if (total <= DiskBudgetBytes) return;
            foreach (var file in files.OrderBy(f => f.LastAccessTimeUtc))
            {
                if (total <= DiskTrimTargetBytes) break;
                try
                {
                    total -= file.Length;
                    file.Delete();
                }
                catch { /* in use — skip */ }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[RemoteImage] disk trim failed: {ex.Message}");
        }
    }

    private static Bitmap? Decode(Stream stream)
    {
        var bitmap = new Bitmap(stream);
        var size = bitmap.PixelSize;
        if (size.Width <= MaxDecodeSize && size.Height <= MaxDecodeSize)
            return bitmap;

        double scale = (double)MaxDecodeSize / Math.Max(size.Width, size.Height);
        var target = new PixelSize(
            Math.Max(1, (int)(size.Width * scale)),
            Math.Max(1, (int)(size.Height * scale)));
        var scaled = bitmap.CreateScaledBitmap(target, BitmapInterpolationMode.MediumQuality);
        bitmap.Dispose();
        return scaled;
    }

    /// <summary>
    /// Stores the bitmap and returns the one that owns the cache slot. Cached
    /// bitmaps are shared as <see cref="Image.Source"/> across many controls, so
    /// evicted entries are only forgotten, never disposed here — an Image may
    /// still be displaying one, and disposing it crashes the layout pass with
    /// ObjectDisposedException.
    /// </summary>
    private static Bitmap Remember(string url, Bitmap bitmap)
    {
        if (!Cache.TryAdd(url, bitmap))
        {
            if (Cache.TryGetValue(url, out var winner))
            {
                bitmap.Dispose();
                return winner;
            }
        }
        else
        {
            Order.Enqueue(url);
        }

        while (Order.Count > MaxCache && Order.TryDequeue(out var old))
            Cache.TryRemove(old, out _);

        return bitmap;
    }

    /// <summary>Forgets every cached cover (Settings → Clear cache), in memory
    /// and on disk. Shared bitmaps are never disposed here.</summary>
    public static void ClearCache()
    {
        Cache.Clear();
        while (Order.TryDequeue(out _)) { }
        try
        {
            if (Directory.Exists(DiskDir)) Directory.Delete(DiskDir, recursive: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[RemoteImage] disk clear failed: {ex.Message}");
        }
    }
}
