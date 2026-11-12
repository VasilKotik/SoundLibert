using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundLibert.Domain.Interfaces;

namespace SoundLibert.UI.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly IAccountService _account;
    private readonly IAudioSettings _audio;
    private readonly IAppSettings _settings;
    private readonly IDownloadService _downloads;
    private readonly IAudioPlayer _player;
    private readonly IMusicDatabase _database;
    private readonly SoundLibert.UI.Services.ArtworkService _artwork;
    private bool _loading;

    /// <summary>Set by the shell: shows a transient toast.</summary>
    public Action<string>? Notify { get; set; }

    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private string _accountDisplay = "Not signed in";
    [ObservableProperty] private string _accountStatus = string.Empty;
    [ObservableProperty] private string? _accountHandle;
    [ObservableProperty] private string? _accountAvatarUrl;

    /// <summary>Email / handle shown under the account name (null when signed out).</summary>
    [ObservableProperty] private string? _accountSubtitle;

    // ---- Playback ----
    [ObservableProperty] private bool _autoplay;
    [ObservableProperty] private bool _rememberLastSession;

    /// <summary>Sidebar account chip: name (or a sign-in prompt) …</summary>
    public string SidebarAccountName => IsSignedIn
        ? (_account.AccountName ?? _account.AccountHandle ?? "YouTube Music")
        : "Not signed in";

    /// <summary>… and one status line under it.</summary>
    public string SidebarAccountStatus => !IsSignedIn
        ? "Sign in for your library"
        : NeedsReauthentication ? "Sign in again to sync" : (_account.AccountHandle ?? "YouTube Music");

    /// <summary>Signed in, but YouTube stopped recognising the session.</summary>
    [ObservableProperty] private bool _needsReauthentication;
    [ObservableProperty] private bool _shuffleOnByDefault;
    [ObservableProperty] private string _repeatMode = "Off";

    // ---- Video ----
    public IReadOnlyList<string> VideoQualities { get; } =
        new[] { "Auto (720p)", "1080p", "720p", "480p", "360p" };

    /// <summary>Combo index into <see cref="VideoQualities"/> (0 = auto).</summary>
    [ObservableProperty] private int _videoQualityIndex;

    partial void OnVideoQualityIndexChanged(int value)
    {
        if (_loading) return;
        _settings.VideoQuality = value switch { 1 => 1080, 2 => 720, 3 => 480, 4 => 360, _ => 0 };
    }

    // ---- AI music ----
    [ObservableProperty] private bool _hideAiMusic;
    [ObservableProperty] private string _aiMusicNote = string.Empty;
    [ObservableProperty] private bool _hasAiMarks;

    /// <summary>The switch flipped or the listener's word was cleared: what is recommended changes.</summary>
    public event Action? AiMusicChanged;

    private SoundLibert.Domain.Interfaces.IAiMusicFilter? _aiFilter;

    /// <summary>The filter the switch and the note speak about (set by the window's view model).</summary>
    public SoundLibert.Domain.Interfaces.IAiMusicFilter? AiFilter
    {
        get => _aiFilter;
        set
        {
            if (_aiFilter != null) _aiFilter.Changed -= OnAiFilterChanged;
            _aiFilter = value;
            if (_aiFilter != null) _aiFilter.Changed += OnAiFilterChanged;
            UpdateAiMusicNote();
        }
    }

    private void OnAiFilterChanged() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateAiMusicNote);

    private void UpdateAiMusicNote()
    {
        var known = _aiFilter?.KnownCount ?? 0;
        var marked = _aiFilter?.MarkedCount ?? 0;
        var list = known > 0
            ? $"A community list of {known:N0} known AI artists, updated daily"
            : "A community list of known AI artists, updated daily";
        AiMusicNote = marked > 0
            ? $"{list}, and {marked} artist{(marked == 1 ? "" : "s")} you marked yourself. Search still finds them."
            : $"{list}. Search still finds them; mark an artist from its menu.";
        HasAiMarks = marked > 0;
    }

    partial void OnHideAiMusicChanged(bool value)
    {
        if (_loading) return;
        _settings.HideAiMusic = value;
        AiMusicChanged?.Invoke();
    }

    [RelayCommand]
    private void ClearAiMarks()
    {
        _aiFilter?.ClearMarks();
        UpdateAiMusicNote();
        AiMusicChanged?.Invoke();
        Notify?.Invoke("Your AI marks are cleared");
    }

    // ---- Audio ----
    [ObservableProperty] private bool _normalizeVolume;
    [ObservableProperty] private bool _crossfadeEnabled;
    [ObservableProperty] private double _crossfadeSeconds = 6;
    [ObservableProperty] private double _eqBass;
    [ObservableProperty] private double _eqMid;
    [ObservableProperty] private double _eqTreble;
    [ObservableProperty] private string _eqPreset = "Flat";

    // ---- Display ----
    [ObservableProperty] private bool _showCanvas;
    [ObservableProperty] private bool _reduceMotion;
    [ObservableProperty] private bool _showSingleLyric;

    // ---- Lyrics ----
    [ObservableProperty] private bool _lyricsEnabled;

    // ---- Window ----
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _closeToTray;

    public IReadOnlyList<string> RepeatModes { get; } = new[] { "Off", "All", "One" };

    public IReadOnlyList<string> EqPresets { get; } =
        new[] { "Flat", "Bass boost", "Treble boost", "Vocal", "Loudness", "Custom" };

    private static readonly (string Name, double Bass, double Mid, double Treble)[] PresetBands =
    {
        ("Flat", 0, 0, 0),
        ("Bass boost", 7, 1, -1),
        ("Treble boost", -1, 1, 7),
        ("Vocal", -2, 4, 2),
        ("Loudness", 5, -2, 3)
    };

    /// <summary>The preset these bands belong to, or "Custom" (the preset itself is not stored).</summary>
    private static string PresetFor(double bass, double mid, double treble)
    {
        foreach (var preset in PresetBands)
            if (Math.Abs(preset.Bass - bass) < 0.01 && Math.Abs(preset.Mid - mid) < 0.01 && Math.Abs(preset.Treble - treble) < 0.01)
                return preset.Name;
        return "Custom";
    }

    public string Version => Updates.CurrentVersion;

    /// <summary>The crossfade's length shows while crossfade is on, where the platform has it.</summary>
    public bool ShowCrossfadeLength => CrossfadeEnabled && SoundLibert.UI.Services.PlatformFeatures.Crossfade;

    /// <summary>Settings snapshot for bug-report diagnostics (read-only use).</summary>
    public IAppSettings SettingsForReports => _settings;

    /// <summary>Update checks / download / restart (Settings → About).</summary>
    public SoundLibert.UI.Services.UpdateService Updates { get; }

    [ObservableProperty] private bool _autoCheckUpdates;

    partial void OnAutoCheckUpdatesChanged(bool value)
    {
        if (_loading) return;
        _settings.AutoCheckUpdates = value;
        if (value) Updates.StartAutomaticChecks();
        else Updates.StopAutomaticChecks();
    }

    [RelayCommand]
    private Task CheckForUpdatesAsync() => Updates.CheckAndDownloadAsync(userInitiated: true);

    [RelayCommand]
    private void RestartToUpdate() => Updates.RestartToUpdate();

    public SettingsViewModel(IAccountService account, IAudioSettings audio, IAppSettings settings,
        IDownloadService downloads, IAudioPlayer player, IMusicDatabase database,
        SoundLibert.UI.Services.ArtworkService artwork, SoundLibert.UI.Services.UpdateService updates)
    {
        Updates = updates;
        _account = account;
        _audio = audio;
        _settings = settings;
        _downloads = downloads;
        _player = player;
        _database = database;
        _artwork = artwork;

        // These can come from a network thread; Refresh touches bound properties.
        _account.SignedIn += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
        _account.ReauthenticationRequired += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);

        LoadFromSettings();
        Refresh();
    }

    // ---- Audio quality ----
    public IReadOnlyList<string> AudioQualities { get; } = new[] { "Auto", "Data saver", "High" };
    [ObservableProperty] private int _audioQualityIndex;

    partial void OnAudioQualityIndexChanged(int value)
    {
        if (_loading) return;
        _settings.AudioQuality = value;
    }

    // ---- Lyrics size ----
    [ObservableProperty] private double _lyricsFontSize = 15;

    /// <summary>Raised when the lyrics size changes so the player can apply it live.</summary>
    public event Action<double>? LyricsFontSizeChanged;

    partial void OnLyricsFontSizeChanged(double value)
    {
        if (_loading) return;
        _settings.LyricsFontSize = value;
        LyricsFontSizeChanged?.Invoke(value);
    }

    // ---- Window ----
    [ObservableProperty] private bool _startMinimized;

    partial void OnStartMinimizedChanged(bool value)
    {
        if (_loading) return;
        _settings.StartMinimized = value;
    }

    /// <summary>Clears the image/artwork/lyrics caches (Settings → Clear cache).</summary>
    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        try
        {
            SoundLibert.UI.Controls.RemoteImage.ClearCache();
            SoundLibert.UI.Services.HomeFeedCache.Clear();
            _artwork.Clear();
            await _database.ClearLyricsCacheAsync();
            Notify?.Invoke("Cache cleared");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Settings] clear cache failed: {ex.Message}");
        }
    }

    // ---- Audio output device ----
    private const string SystemDefaultDevice = "System default";

    /// <summary>"System default" first, then the devices connected right now.</summary>
    public IReadOnlyList<string> OutputDevices { get; private set; } = new[] { SystemDefaultDevice };
    [ObservableProperty] private int _outputDeviceIndex;

    partial void OnOutputDeviceIndexChanged(int value)
    {
        // A combo box whose list is being replaced reports -1 for a moment.
        if (_loading || value < 0 || value >= OutputDevices.Count) return;
        _settings.OutputDeviceName = value == 0 ? string.Empty : OutputDevices[value];
    }

    /// <summary>
    /// Re-lists the devices (they come and go). A chosen device that is unplugged
    /// shows as the default for now, but stays chosen for when it returns.
    /// </summary>
    private void RefreshOutputDevices()
    {
        var wasLoading = _loading;
        _loading = true;
        var devices = new List<string> { SystemDefaultDevice };
        devices.AddRange(_player.GetOutputDevices());
        OutputDevices = devices;
        OnPropertyChanged(nameof(OutputDevices));
        var chosen = devices.IndexOf(_settings.OutputDeviceName);
        OutputDeviceIndex = chosen > 0 ? chosen : 0;
        _loading = wasLoading;
    }

    // ---- Offline downloads ----

    [ObservableProperty] private string _downloadsStatsText = "…";
    [ObservableProperty] private string _downloadsFolderText = string.Empty;

    /// <summary>Recomputes the offline-copy count, size and folder.</summary>
    public async Task RefreshDownloadsAsync()
    {
        DownloadsFolderText = _downloads.FolderPath;
        try
        {
            var (count, bytes) = await _downloads.GetStatsAsync();
            var size = bytes >= 1024L * 1024 * 1024
                ? $"{bytes / 1024.0 / 1024 / 1024:F1} GB"
                : bytes >= 1024 * 1024
                    ? $"{bytes / 1024.0 / 1024:F1} MB"
                    : $"{bytes / 1024.0:F0} KB";
            DownloadsStatsText = count == 0
                ? "No downloads"
                : $"{count} song{(count == 1 ? "" : "s")} · {size}";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Settings] download stats failed: {ex.Message}");
            DownloadsStatsText = "Could not read downloads";
        }
    }

    /// <summary>
    /// Sets the download folder (empty = default), moves existing offline copies
    /// into it, and refreshes the stats. Moving runs one file at a time.
    /// </summary>
    public async Task SetDownloadFolderAsync(string? path)
    {
        _settings.DownloadFolder = path ?? string.Empty;
        DownloadsStatsText = "Moving downloads…";
        try { await _downloads.MoveExistingAsync(); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[Settings] move downloads failed: {ex.Message}"); }
        await RefreshDownloadsAsync();
    }

    [RelayCommand]
    private async Task ClearDownloadsAsync()
    {
        await _downloads.ClearAllAsync();
        await RefreshDownloadsAsync();
    }

    /// <summary>Pulls persisted values into the observable properties once.</summary>
    private void LoadFromSettings()
    {
        _loading = true;
        NormalizeVolume = _audio.NormalizeVolume;
        CrossfadeEnabled = _settings.CrossfadeEnabled;
        CrossfadeSeconds = _settings.CrossfadeSeconds;
        EqBass = _audio.BassDb;
        EqMid = _audio.MidDb;
        EqTreble = _audio.TrebleDb;
        EqPreset = PresetFor(_audio.BassDb, _audio.MidDb, _audio.TrebleDb);

        Autoplay = _settings.Autoplay;
        RememberLastSession = _settings.RememberLastSession;
        HideAiMusic = _settings.HideAiMusic;
        AutoCheckUpdates = _settings.AutoCheckUpdates;
        ShuffleOnByDefault = _settings.ShuffleOnByDefault;
        RepeatMode = _settings.RepeatMode;
        VideoQualityIndex = _settings.VideoQuality switch { 1080 => 1, 720 => 2, 480 => 3, 360 => 4, _ => 0 };
        RefreshOutputDevices();
        AudioQualityIndex = Math.Clamp(_settings.AudioQuality, 0, 2);
        LyricsFontSize = Math.Clamp(_settings.LyricsFontSize <= 0 ? 15 : _settings.LyricsFontSize, 11, 30);
        StartMinimized = _settings.StartMinimized;

        ShowCanvas = _settings.ShowCanvas;
        ReduceMotion = _settings.ReduceMotion;
        ShowSingleLyric = _settings.ShowSingleLyric;

        LyricsEnabled = _settings.LyricsEnabled;
        MinimizeToTray = _settings.MinimizeToTray;
        CloseToTray = _settings.CloseToTray;
        _loading = false;
    }

    // ---- Playback ----
    partial void OnAutoplayChanged(bool value) { if (!_loading) _settings.Autoplay = value; }
    partial void OnRememberLastSessionChanged(bool value) { if (!_loading) _settings.RememberLastSession = value; }
    partial void OnShuffleOnByDefaultChanged(bool value)
    {
        if (_loading) return;
        _settings.ShuffleOnByDefault = value;
        PlaybackModesChanged?.Invoke();
    }

    partial void OnRepeatModeChanged(string value)
    {
        if (_loading) return;
        _settings.RepeatMode = value;
        PlaybackModesChanged?.Invoke();
    }

    /// <summary>Raised when shuffle or repeat changes here, so the player follows at once.</summary>
    public event Action? PlaybackModesChanged;

    // ---- Audio ----
    partial void OnNormalizeVolumeChanged(bool value) { if (!_loading) _audio.NormalizeVolume = value; }
    partial void OnCrossfadeEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCrossfadeLength));
        if (!_loading) _settings.CrossfadeEnabled = value;
    }
    partial void OnCrossfadeSecondsChanged(double value) { if (!_loading) _settings.CrossfadeSeconds = value; }
    partial void OnEqBassChanged(double value) { if (!_loading) { _audio.BassDb = value; EqPreset = "Custom"; } }
    partial void OnEqMidChanged(double value) { if (!_loading) { _audio.MidDb = value; EqPreset = "Custom"; } }
    partial void OnEqTrebleChanged(double value) { if (!_loading) { _audio.TrebleDb = value; EqPreset = "Custom"; } }

    partial void OnEqPresetChanged(string value)
    {
        if (_loading || value is "Custom") return;
        var (_, bass, mid, treble) = PresetBands.FirstOrDefault(p => p.Name == value);

        // Set the backing fields + audio settings without re-marking "Custom".
        _loading = true;
        EqBass = bass;
        EqMid = mid;
        EqTreble = treble;
        _loading = false;
        _audio.BassDb = bass;
        _audio.MidDb = mid;
        _audio.TrebleDb = treble;
    }

    // ---- Display / lyrics / window ----
    partial void OnShowCanvasChanged(bool value) { if (!_loading) _settings.ShowCanvas = value; }
    partial void OnReduceMotionChanged(bool value)
    {
        if (_loading) return;
        _settings.ReduceMotion = value;
        SoundLibert.UI.Controls.Motion.Enabled = !value && SoundLibert.UI.Controls.Motion.SystemAnimationsOn();
    }
    partial void OnShowSingleLyricChanged(bool value) { if (!_loading) _settings.ShowSingleLyric = value; }
    partial void OnLyricsEnabledChanged(bool value) { if (!_loading) _settings.LyricsEnabled = value; }
    partial void OnMinimizeToTrayChanged(bool value) { if (!_loading) _settings.MinimizeToTray = value; }
    partial void OnCloseToTrayChanged(bool value) { if (!_loading) _settings.CloseToTray = value; }

    public void Refresh()
    {
        // The player's own buttons and the fullscreen toggle change these too.
        _loading = true;
        ShuffleOnByDefault = _settings.ShuffleOnByDefault;
        RepeatMode = _settings.RepeatMode;
        ShowCanvas = _settings.ShowCanvas;
        ReduceMotion = _settings.ReduceMotion;
        _loading = false;
        RefreshOutputDevices();

        IsSignedIn = _account.IsSignedIn;
        AccountDisplay = IsSignedIn
            ? _account.AccountName ?? _account.AccountEmail ?? "Signed in to YouTube Music"
            : "Not signed in — SoundLibert works fully anonymously.";
        AccountHandle = IsSignedIn ? _account.AccountHandle : null;
        AccountAvatarUrl = IsSignedIn ? _account.AccountAvatarUrl : null;

        // Secondary line under the account name (email, else channel handle).
        AccountSubtitle = IsSignedIn
            ? (_account.AccountEmail ?? _account.AccountHandle)
            : null;

        NeedsReauthentication = _account.NeedsReauthentication;
        OnPropertyChanged(nameof(SidebarAccountName));
        OnPropertyChanged(nameof(SidebarAccountStatus));
        if (NeedsReauthentication)
            AccountStatus = "YouTube Music no longer accepts this sign-in (the session expired). Your library is kept as it was — sign in again to resume syncing.";
        else if (AccountStatus.StartsWith("YouTube Music no longer accepts", StringComparison.Ordinal))
            AccountStatus = string.Empty;

        _ = RefreshDownloadsAsync();
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        await _account.SignOutAsync();
        Refresh();
    }
}
