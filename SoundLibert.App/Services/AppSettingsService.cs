using System.Diagnostics;
using System.Text.Json;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;

namespace SoundLibert.App.Services;

/// <summary>
/// Persists all user preferences to <c>%LocalAppData%\SoundLibert\settings.json</c>
/// and (re)applies the audio ones to the player. Replaces the old in-memory
/// only <c>AudioSettingsService</c> so there is a single settings store.
/// </summary>
internal sealed class AppSettingsService : IAppSettings, IAudioSettings
{
    private readonly IAudioPlayer _player;
    private readonly string _path;
    private readonly System.Timers.Timer _debounce;
    private readonly object _saveLock = new();

    private Data _data = new();

    public AppSettingsService(IAudioPlayer player)
    {
        _player = player;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoundLibert");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");

        // Debounced write so dragging the volume slider doesn't hammer the disk.
        _debounce = new System.Timers.Timer(500) { AutoReset = false };
        _debounce.Elapsed += (_, _) => Save();

        Load();
        ApplyAudio();
    }

    // ---- Playback ----
    public double Volume { get => _data.Volume; set { _data.Volume = value; Touch(); } }
    public bool IsMuted { get => _data.IsMuted; set { _data.IsMuted = value; Touch(); } }
    public bool NormalizeVolume { get => _data.NormalizeVolume; set { _data.NormalizeVolume = value; _player.NormalizationGain = value ? _player.NormalizationGain : 1.0; Touch(); } }
    public double BassDb { get => _player.EqualizerBassDb; set { _player.EqualizerBassDb = value; _data.BassDb = value; Touch(); } }
    public double MidDb { get => _player.EqualizerMidDb; set { _player.EqualizerMidDb = value; _data.MidDb = value; Touch(); } }
    public double TrebleDb { get => _player.EqualizerTrebleDb; set { _player.EqualizerTrebleDb = value; _data.TrebleDb = value; Touch(); } }
    public bool Autoplay { get => _data.Autoplay; set { _data.Autoplay = value; Touch(); } }
    public bool RememberPosition { get => _data.RememberPosition; set { _data.RememberPosition = value; Touch(); } }
    public bool CrossfadeEnabled { get => _data.CrossfadeEnabled; set { _data.CrossfadeEnabled = value; ApplyCrossfade(); Touch(); } }
    public double CrossfadeSeconds
    {
        get => _data.CrossfadeSeconds;
        set { _data.CrossfadeSeconds = value; ApplyCrossfade(); Touch(); }
    }
    public bool ShuffleOnByDefault { get => _data.ShuffleOnByDefault; set { _data.ShuffleOnByDefault = value; Touch(); } }

    public bool HideAiMusic { get => _data.HideAiMusic; set { _data.HideAiMusic = value; Touch(); } }

    public IReadOnlyDictionary<string, bool> AiArtistMarks => _data.AiArtistMarks ??= new();

    public void SetAiArtistMark(string normalizedName, bool? isAi)
    {
        var marks = _data.AiArtistMarks ??= new();
        if (isAi is { } value) marks[normalizedName] = value;
        else marks.Remove(normalizedName);
        Touch();
    }
    public string RepeatMode { get => _data.RepeatMode; set { _data.RepeatMode = value; Touch(); } }

    // ---- General ----
    public bool MinimizeToTray { get => _data.MinimizeToTray; set { _data.MinimizeToTray = value; Touch(); } }
    public bool CloseToTray { get => _data.CloseToTray; set { _data.CloseToTray = value; Touch(); } }
    public bool RememberLastSession { get => _data.RememberLastSession; set { _data.RememberLastSession = value; Touch(); } }
    public event EventHandler<bool>? ShowCanvasChanged;
    public bool ShowCanvas
    {
        get => _data.ShowCanvas;
        set
        {
            if (_data.ShowCanvas == value) return;
            _data.ShowCanvas = value;
            Touch();
            ShowCanvasChanged?.Invoke(this, value);
        }
    }
    public bool ShowSingleLyric { get => _data.ShowSingleLyric; set { _data.ShowSingleLyric = value; Touch(); } }
    public int VideoQuality { get => _data.VideoQuality; set { _data.VideoQuality = value; Touch(); } }

    // ---- Library ----
    public string DownloadFolder { get => _data.DownloadFolder; set { _data.DownloadFolder = value ?? string.Empty; Touch(); } }
    public string OutputDeviceName { get => _data.OutputDeviceName; set { _data.OutputDeviceName = value ?? string.Empty; _player.OutputDeviceName = _data.OutputDeviceName; Touch(); } }
    public int AudioQuality { get => _data.AudioQuality; set { _data.AudioQuality = value; Touch(); } }
    public double LyricsFontSize { get => _data.LyricsFontSize; set { _data.LyricsFontSize = value; Touch(); } }
    public bool StartMinimized { get => _data.StartMinimized; set { _data.StartMinimized = value; Touch(); } }
    public bool AutoCheckUpdates { get => _data.AutoCheckUpdates; set { _data.AutoCheckUpdates = value; Touch(); } }

    // ---- Lyrics ----
    public bool LyricsEnabled { get => _data.LyricsEnabled; set { _data.LyricsEnabled = value; Touch(); } }
    public bool ReduceMotion { get => _data.ReduceMotion; set { _data.ReduceMotion = value; Touch(); } }
    public string LyricsProvider { get => _data.LyricsProvider; set { _data.LyricsProvider = value; Touch(); } }

    public SessionState? LastSession { get => _data.LastSession; set { _data.LastSession = value; Touch(); } }

    // ---- Welcome ----
    public bool WelcomeSeen { get => _data.WelcomeSeen ?? false; set { _data.WelcomeSeen = value; Touch(); } }

    public IReadOnlyList<FavouriteArtist> FavouriteArtists
    {
        get => _data.FavouriteArtists ?? (IReadOnlyList<FavouriteArtist>)Array.Empty<FavouriteArtist>();
        set { _data.FavouriteArtists = value?.ToList(); Touch(); }
    }

    public NudgeState Nudges => _data.Nudges ??= new NudgeState();

    private void Touch()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void ApplyCrossfade()
        => _player.CrossfadeSeconds = _data.CrossfadeEnabled ? _data.CrossfadeSeconds : 0;

    private void ApplyAudio()
    {
        _player.EqualizerBassDb = _data.BassDb;
        _player.EqualizerMidDb = _data.MidDb;
        _player.EqualizerTrebleDb = _data.TrebleDb;
        ApplyCrossfade();
        // Mute is session-only (see MainWindowViewModel): never restore it, or a
        // stray mute would make the next launch start silent.
        _player.Volume = _data.Volume;
        _player.OutputDeviceName = _data.OutputDeviceName;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var data = JsonSerializer.Deserialize<Data>(json);
            if (data != null) _data = data;
            // Settings from before the welcome existed: this is no new listener.
            _data.WelcomeSeen ??= true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Settings] Load failed, using defaults: {ex.Message}");
        }
    }

    public void Save()
    {
        // The debounce timer and an explicit Save (on exit) can overlap. The new
        // content goes to a side file first, so a crash mid-write cannot leave a
        // truncated settings.json that silently resets everything on next launch.
        lock (_saveLock)
        {
            var temp = _path + ".tmp";
            try
            {
                _data.BassDb = _player.EqualizerBassDb;
                _data.MidDb = _player.EqualizerMidDb;
                _data.TrebleDb = _player.EqualizerTrebleDb;
                File.WriteAllText(temp, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Settings] Save failed: {ex.Message}");
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }

    private sealed class Data
    {
        public double Volume { get; set; } = 1.0;
        public bool IsMuted { get; set; }
        public bool NormalizeVolume { get; set; }
        public double BassDb { get; set; }
        public double MidDb { get; set; }
        public double TrebleDb { get; set; }
        public bool Autoplay { get; set; } = true;
        public bool RememberPosition { get; set; } = true;
        public bool CrossfadeEnabled { get; set; }
        public double CrossfadeSeconds { get; set; } = 6;
        public bool ShuffleOnByDefault { get; set; }
        public string RepeatMode { get; set; } = "Off";
        public bool HideAiMusic { get; set; } = true;
        public Dictionary<string, bool>? AiArtistMarks { get; set; }

        public bool MinimizeToTray { get; set; }
        public bool CloseToTray { get; set; }
        public bool RememberLastSession { get; set; } = true;
        public bool ShowCanvas { get; set; } = true;
        public bool ReduceMotion { get; set; }
        public bool ShowSingleLyric { get; set; } = true;
        public int VideoQuality { get; set; }

        public string DownloadFolder { get; set; } = string.Empty;
        public string OutputDeviceName { get; set; } = string.Empty;
        public int AudioQuality { get; set; }
        public double LyricsFontSize { get; set; } = 22;
        public bool StartMinimized { get; set; }
        public bool AutoCheckUpdates { get; set; } = true;

        public bool LyricsEnabled { get; set; } = true;
        public string LyricsProvider { get; set; } = "LRCLIB";

        public SessionState? LastSession { get; set; }

        /// <summary>Null in settings written before the welcome existed.</summary>
        public bool? WelcomeSeen { get; set; }

        public List<FavouriteArtist>? FavouriteArtists { get; set; }

        public NudgeState? Nudges { get; set; }
    }
}
