namespace SoundLibert.Infra.InnerTube;

public static class StreamClientUtils
{
    public static string ResolveUserAgent(string clientName)
    {
        return clientName?.ToUpperInvariant() switch
        {
            "WEB" or "WEB_REMIX" or "WEB_CREATOR" or "MWEB" or "WEB_EMBEDDED_PLAYER"
                => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36",

            "TVHTML5" or "TVHTML5_SIMPLY_EMBEDDED_PLAYER" or "TVHTML5_SIMPLY"
                => "Mozilla/5.0 (PlayStation; PlayStation 4/12.02) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.4 Safari/605.1.15",

            "IOS_MUSIC"
                => "com.google.ios.youtubemusic/7.27.0 (iPhone16,2; U; CPU iOS 17_5_1 like Mac OS X;)",

            "IOS" or "IPADOS"
                => "com.google.ios.youtube/19.29.1 (iPhone16,2; U; CPU iOS 17_5_1 like Mac OS X;)",

            "ANDROID_VR" or "ANDROID_VR_NO_AUTH"
                => "com.google.android.apps.youtube.vr.oculus/1.37 (Linux; U; Android 12; en_US; Quest 3; Build/SQ3A.220605.009.A1; Cronet/107.0.5304.107)",

            "ANDROID_MUSIC"
                => "com.google.android.apps.youtube.music/7.27.52 (Linux; U; Android 15; en_US; Pixel 9 Pro; Build/AP4A.250205.002; Cronet/132.0.6834.79) gzip",

            "ANDROID_TESTSUITE"
                => "com.google.android.youtube/1.9 (Linux; U; Android 15; en_US; Pixel 9 Pro; Build/AP4A.250205.002)",

            "ANDROID_UNPLUGGED"
                => "com.google.android.apps.youtube.unplugged/8.49.0 (Linux; U; Android 15; en_US; Pixel 9 Pro; Build/AP4A.250205.002; Cronet/132.0.6834.79) gzip",

            "ANDROID_CREATOR"
                => "com.google.android.apps.youtube.creator/23.47.101 (Linux; U; Android 15; en_US; Pixel 9 Pro Fold; Build/AP4A.250205.002; Cronet/132.0.6834.79) gzip",

            "ANDROID" or "MOBILE"
                => "com.google.android.youtube/21.10.38 (Linux; U; Android 15; en_US; Pixel 9 Pro; Build/AP4A.250205.002; Cronet/132.0.6834.79) gzip",

            "VISIONOS"
                => "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15",

            _ => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36"
        };
    }

    public static (string? origin, string? referer) ResolveOriginReferer(string clientName)
    {
        return clientName?.ToUpperInvariant() switch
        {
            "WEB" or "WEB_REMIX" or "WEB_CREATOR" or "MWEB" or "WEB_EMBEDDED_PLAYER"
                => ("https://music.youtube.com", "https://music.youtube.com/"),

            "TVHTML5" or "TVHTML5_SIMPLY_EMBEDDED_PLAYER" or "TVHTML5_SIMPLY"
                => ("https://www.youtube.com", "https://www.youtube.com/tv"),

            _ => (null, null)
        };
    }

    public static bool IsWebClient(string clientName)
    {
        var name = clientName?.ToUpperInvariant() ?? "";
        return name is "WEB" or "WEB_REMIX" or "WEB_CREATOR" or "MWEB"
            or "WEB_EMBEDDED_PLAYER" or "TVHTML5" or "TVHTML5_SIMPLY_EMBEDDED_PLAYER"
            or "TVHTML5_SIMPLY";
    }

    public static string PatchClientVersion(string url, string clientVersion)
    {
        if (!url.Contains("cver=")) return url;
        var idx = url.IndexOf("cver=");
        var ampIdx = url.IndexOf('&', idx);
        var old = ampIdx > 0 ? url.Substring(idx, ampIdx - idx) : url.Substring(idx);
        return url.Replace(old, $"cver={clientVersion}");
    }

    public static Dictionary<string, string> GetStreamHeaders(string? clientName)
    {
        var headers = new Dictionary<string, string>();
        var ua = ResolveUserAgent(clientName ?? "");
        headers["User-Agent"] = ua;

        var (origin, referer) = ResolveOriginReferer(clientName ?? "");
        if (origin != null) headers["Origin"] = origin;
        if (referer != null) headers["Referer"] = referer;

        return headers;
    }

    /// <summary>
    /// Port of Velune's StreamClientUtils.appendPoToken: attaches the GVS poToken
    /// to a stream URL as the `pot` query parameter (required for web clients).
    /// </summary>
    public static string AppendPoToken(string url, string poToken)
    {
        if (url.Contains("pot=")) return url;
        var separator = url.Contains("?") ? "&" : "?";
        return url + separator + "pot=" + Uri.EscapeDataString(poToken);
    }

    /// <summary>
    /// Reads the `c` (client) query parameter embedded in a YouTube stream URL.
    /// Velune resolves per-request headers from this value, not from the
    /// client that was used for the player request.
    /// </summary>
    public static string? GetClientParamFromUrl(string url)
    {
        var idx = url.IndexOf("?c=", StringComparison.Ordinal);
        if (idx < 0) idx = url.IndexOf("&c=", StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + 3;
        var end = url.IndexOf('&', start);
        return end < 0 ? url.Substring(start) : url.Substring(start, end - start);
    }
}
