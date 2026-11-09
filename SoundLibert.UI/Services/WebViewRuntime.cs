namespace SoundLibert.UI.Services;

/// <summary>
/// Detects the Microsoft Edge WebView2 Evergreen Runtime, which the official
/// Avalonia WebView needs for Google sign-in. It is a separate machine-wide
/// install: a self-contained .NET build does NOT include it, so a release must
/// check for it and tell the user (login otherwise fails with a native error).
/// </summary>
public static class WebViewRuntime
{
    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsWindows()) return false;

        foreach (var root in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         "Microsoft", "EdgeWebView", "Application"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         "Microsoft", "EdgeWebView", "Application"),
                     // Installed without admin rights it lives in the user's profile.
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Microsoft", "EdgeWebView", "Application")
                 })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var exe in Directory.EnumerateFiles(root, "msedgewebview2.exe", SearchOption.AllDirectories))
                    if (new FileInfo(exe).Length > 0) return true;
            }
            catch { /* unreadable path — keep looking */ }
        }
        return false;
    }
}
