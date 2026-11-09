using System;
using System.Diagnostics;
using System.IO;

namespace SoundLibert.UI.Services;

/// <summary>
/// The sign-in window's browser profile, which holds a Google session. It lives
/// with the user's data, not next to the executable (the updater replaces that
/// folder), and it is deleted on sign-out: signing out must leave nothing of the
/// account behind.
/// </summary>
public static class WebViewProfile
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundLibert", "webview");

    /// <summary>Where WebView2 put the profile before it was given a folder of its own.</summary>
    private static string LegacyFolder => Path.Combine(AppContext.BaseDirectory, "SoundLibert.UI.exe.WebView2");

    public static void Clear()
    {
        foreach (var folder in new[] { Folder, LegacyFolder })
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex)
            {
                // Its browser process may still be closing: the next start tries again.
                Trace.WriteLine($"[Login] could not clear the sign-in browser profile: {ex.Message}");
            }
        }
    }
}
