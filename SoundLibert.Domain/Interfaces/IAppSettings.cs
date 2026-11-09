using SoundLibert.Domain.Models;

namespace SoundLibert.Domain.Interfaces;

/// <summary>
/// All user preferences, persisted to disk (<c>settings.json</c>). Combining the
/// audio preferences here (instead of a second service) avoids duplicate
/// settings stores. Implementations save on change and expose <see cref="Save"/>.
/// </summary>
public interface IAppSettings
{
    // ---- Playback ----
    double Volume { get; set; }
    bool IsMuted { get; set; }
    bool NormalizeVolume { get; set; }
    double BassDb { get; set; }
    double MidDb { get; set; }
    double TrebleDb { get; set; }
    bool Autoplay { get; set; }
    bool RememberPosition { get; set; }
    bool CrossfadeEnabled { get; set; }

    /// <summary>Crossfade length in seconds (0 = off).</summary>
    double CrossfadeSeconds { get; set; }

    /// <summary>Start playback shuffled by default.</summary>
    bool ShuffleOnByDefault { get; set; }

    /// <summary>Default repeat mode on launch: Off | All | One.</summary>
    string RepeatMode { get; set; }

    // ---- General ----
    bool MinimizeToTray { get; set; }
    bool CloseToTray { get; set; }
    bool RememberLastSession { get; set; }

    /// <summary>Follow the track with the animated Canvas in the fullscreen player.</summary>
    bool ShowCanvas { get; set; }

    /// <summary>Calms the interface: no moving background, no page and card motion.</summary>
    bool ReduceMotion { get; set; }

    /// <summary>
    /// Preferred Video-mode resolution as a target height in pixels
    /// (1080/720/480/360); 0 = automatic (best available).
    /// </summary>
    int VideoQuality { get; set; }

    /// <summary>Raised when <see cref="ShowCanvas"/> changes so the player can apply it live.</summary>
    event EventHandler<bool>? ShowCanvasChanged;

    /// <summary>Show the current synced lyric line under the compact now-playing card.</summary>
    bool ShowSingleLyric { get; set; }

    /// <summary>Folder for offline downloads (empty = the default app-data folder).</summary>
    string DownloadFolder { get; set; }

    /// <summary>Output device by name, applied to the player at once (empty = system default).</summary>
    string OutputDeviceName { get; set; }

    /// <summary>Audio stream quality: 0 = auto, 1 = data saver (lowest bitrate), 2 = high.</summary>
    int AudioQuality { get; set; }

    /// <summary>Font size worked into the fullscreen lyrics list (px).</summary>
    double LyricsFontSize { get; set; }

    /// <summary>Start the app hidden in the tray.</summary>
    bool StartMinimized { get; set; }

    /// <summary>Check GitHub for a new version in the background and download it.</summary>
    bool AutoCheckUpdates { get; set; }

    // ---- Lyrics ----
    bool LyricsEnabled { get; set; }
    string LyricsProvider { get; set; }

    /// <summary>Queue + position from the previous session (for resume).</summary>
    SessionState? LastSession { get; set; }

    // ---- Welcome ----

    /// <summary>
    /// The welcome screen has been seen. A new install shows it once. An install that
    /// already had its settings counts as having seen it.
    /// </summary>
    bool WelcomeSeen { get; set; }

    /// <summary>The artists chosen on the welcome screen. Home's personal shelves start from them.</summary>
    IReadOnlyList<FavouriteArtist> FavouriteArtists { get; set; }

    /// <summary>Starts counted and what the listener was asked. Change it, then <see cref="Save"/>.</summary>
    NudgeState Nudges { get; }

    /// <summary>Persists the current values immediately (bypasses debounce).</summary>
    void Save();
}
