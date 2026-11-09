using System.Text.RegularExpressions;

namespace SoundLibert.UI.Services;

/// <summary>
/// YouTube Music serves the same cover at several sizes via a `=wN-hN-l..`
/// suffix on the URL. InnerTube usually returns a small variant, which looks
/// blurry when shown large (e.g. the fullscreen player). Rewriting the size
/// segment asks the CDN for a sharp image instead.
/// </summary>
public static class ThumbnailUrl
{
    private static readonly Regex SizePattern = new(@"=w\d+-h\d+", RegexOptions.Compiled);

    /// <summary>Returns the URL resized to <paramref name="size"/> px, or the original if it has no size suffix.</summary>
    public static string AtSize(string? url, int size)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (!SizePattern.IsMatch(url)) return url;
        return SizePattern.Replace(url, $"=w{size}-h{size}");
    }
}
