namespace SoundLibert.UI.Services;

/// <summary>
/// What the system the app runs on can do, so the shared screens offer only what works
/// there. Windows has all of it; the phone's head switches off what it lacks before the
/// first screen is built.
/// </summary>
public static class PlatformFeatures
{
    /// <summary>A tray to hide the window in.</summary>
    public static bool Tray { get; set; } = true;

    /// <summary>A choice of audio output devices (a phone routes sound itself).</summary>
    public static bool OutputDevices { get; set; } = true;

    /// <summary>One track fading into the next.</summary>
    public static bool Crossfade { get; set; } = true;

    /// <summary>The app downloads its new versions and restarts into them.</summary>
    public static bool SelfUpdate { get; set; } = true;

    /// <summary>Folders the listener can see and open: downloads (picked by them) and the logs.</summary>
    public static bool DownloadFolder { get; set; } = true;

    /// <summary>The running version, where the system knows it better than the assembly does.</summary>
    public static string? AppVersion { get; set; }
}
