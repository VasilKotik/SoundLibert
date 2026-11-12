using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundLibert.Domain.Enums;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Lyrics;
using SoundLibert.Domain.Models;
using SoundLibert.UI.Services;

using SoundLibert.UI.Controls;

namespace SoundLibert.UI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IMusicProvider _musicProvider;
    private readonly IAudioPlayer _audioPlayer;
    private readonly IQueueService _queueService;
    private readonly IMusicDatabase _musicDatabase;
    private readonly ArtworkService _artwork;
    private readonly ILyricsProvider _lyricsProvider;
    private readonly IAccountService _account;
    private readonly IAudioSettings _audioSettings;
    private readonly IAppSettings _appSettings;
    private readonly ILikeService _likeService;
    private readonly IDownloadService _downloads;
    private readonly IGenreService _genreService;
    private readonly IViewCountService _viewCounts;
    private int _playGeneration;
    private int _artworkGeneration;
    private int _lyricsGeneration;
    private int _videoPreparationGeneration;
    /// <summary>
    /// Bumped whenever a whole new queue is set. A slow load started earlier (a
    /// radio, a card's tracks, a clip) must not replace what the user chose since.
    /// </summary>
    private int _queueReplaceGeneration;
    private string? _videoStartTrackId;
    private bool _settingVideoModeForTrack;
    private PreparedVideoStreams? _preparedVideoStreams;
    /// <summary>User chose Video; following queue tracks keep playing as clips.</summary>
    private volatile bool _videoQueueMode;
    /// <summary>Set just before a video SetQueue so the queue hook keeps Video mode.</summary>
    private volatile bool _videoQueueIntent;

    // ---- Video-queue transitions: the ONLY writers of the two flags above.
    // Protocol: a SetQueue keeps Video mode ONLY when armed just before it.
    // The QueueChanged hook consumes the one-shot intent; the sticky mode
    // survives solely through it. Song choices disarm outright. Do NOT write
    // the flags directly from new call sites — use these three functions
    // (the single exception is the documented one-shot in PlayVideoCardAsync).
    private void ArmVideoQueue()
    {
        _videoQueueIntent = true;
        _videoQueueMode = true;
    }

    private void DisarmVideoQueue()
    {
        _videoQueueIntent = false;
        _videoQueueMode = false;
    }

    private readonly Stack<ViewModelBase> _navStack = new();
    private SongItem? _highlightedTrack;
    private SongItem? _likeObservedTrack;

    /// <summary>Pins already background-checked this session (song|video).</summary>
    private readonly HashSet<string> _pinVerifyChecked = new(StringComparer.Ordinal);
    private TimeSpan _resumePosition;
    private string? _resumeTrackId;

    private sealed record PreparedVideoStreams(string VideoId, StreamInfo Video, StreamInfo? Audio);

    [ObservableProperty]
    private ViewModelBase _currentView;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isSearchVisible;

    [ObservableProperty]
    private SongItem? _currentTrack;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private double _volume = 1.0;

    [ObservableProperty]
    private TimeSpan _position;

    [ObservableProperty]
    private TimeSpan _duration;

    [ObservableProperty]
    private string _windowTitle = "SoundLibert";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>
    /// One feedback channel for the whole app: only in-progress states
    /// ("Fetching stream…") stay as the muted line under the track. Outcomes —
    /// confirmations and failures — become a toast (red for failures) instead
    /// of a lingering red line that also duplicated the toasts.
    /// </summary>
    partial void OnStatusMessageChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.EndsWith('…')) return;
        ShowToast(value, IsFailureMessage(value));
        StatusMessage = string.Empty;
    }

    private static bool IsFailureMessage(string message)
        => message.StartsWith("Couldn't", StringComparison.OrdinalIgnoreCase)
           || message.StartsWith("Could not", StringComparison.OrdinalIgnoreCase)
           || message.StartsWith("No official video", StringComparison.OrdinalIgnoreCase)
           || message.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
           || message.Contains("failed", StringComparison.OrdinalIgnoreCase);

    // ---- Transient toast (added to playlist / downloaded / copied …) ----
    [ObservableProperty] private string? _toastMessage;
    [ObservableProperty] private bool _isToastVisible;
    [ObservableProperty] private bool _isToastError;
    private CancellationTokenSource? _toastCts;

    /// <summary>Shows a short toast bottom-centre for ~2.5 s (UI thread safe).</summary>
    public void ShowToast(string message) => ShowToast(message, isError: false);

    /// <summary>Shows a toast; <paramref name="isError"/> styles it as a warning.</summary>
    public void ShowToast(string message, bool isError)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        Dispatcher.UIThread.Post(() =>
        {
            ToastMessage = message;
            IsToastError = isError;
            IsToastVisible = true;
            _toastCts?.Cancel();
            try { _toastCts?.Dispose(); } catch { }
            var cts = new CancellationTokenSource();
            _toastCts = cts;
            _ = HideToastAfterAsync(cts);
        });
    }

    /// <summary>Friendly transient error (a child view model reported a failure).</summary>
    private void ReportError(string message) => ShowToast(message, isError: true);

    /// <summary>Fullscreen lyrics font size (Settings → Display).</summary>
    [ObservableProperty] private double _lyricsFontSize = 15;

    /// <summary>Shows the keyboard-shortcut help overlay (F1 / ?).</summary>
    [ObservableProperty] private bool _showShortcuts;
    public void ToggleShortcuts() => ShowShortcuts = !ShowShortcuts;

    private async Task HideToastAfterAsync(CancellationTokenSource cts)
    {
        try { await Task.Delay(2500, cts.Token); }
        catch (OperationCanceledException) { return; }
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_toastCts, cts)) IsToastVisible = false;
        });
    }

#if WINDOWS
    private SoundLibert.UI.Services.SmtcService? _smtc;

    /// <summary>Starts publishing the now-playing track to the Windows SMTC overlay.</summary>
    public void InitializeSmtc(IntPtr hwnd)
    {
        try
        {
            _smtc ??= new SoundLibert.UI.Services.SmtcService();
            // Explicit, not a toggle: the overlay can be a moment behind the player.
            _smtc.PlayRequested = () => Dispatcher.UIThread.Post(() => { if (!IsPlaying) PlayPauseCommand.Execute(null); });
            _smtc.PauseRequested = () => Dispatcher.UIThread.Post(() => { if (IsPlaying) PlayPauseCommand.Execute(null); });
            _smtc.NextRequested = () => Dispatcher.UIThread.Post(() => NextTrackCommand.Execute(null));
            _smtc.PreviousRequested = () => Dispatcher.UIThread.Post(() => PreviousTrackCommand.Execute(null));
            _smtc.Initialize(hwnd);
            UpdateSmtcMetadata();
            _smtc.SetPlaybackState(IsPlaying, CurrentTrack != null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[SMTC] wire failed: {ex.Message}");
        }

        _taskbar ??= new SoundLibert.UI.Services.TaskbarProgress(hwnd);
        UpdateTaskbarProgress();
    }

    private SoundLibert.UI.Services.TaskbarProgress? _taskbar;

    partial void OnIsTrackLoadingChanged(bool value) => UpdateTaskbarProgress();

    /// <summary>Mirrors play state + position onto the taskbar button (UI thread).</summary>
    private void UpdateTaskbarProgress()
    {
        if (_taskbar == null) return;
        var state = CurrentTrack == null
            ? SoundLibert.UI.Services.TaskbarProgress.TaskbarState.None
            : IsTrackLoading
                ? SoundLibert.UI.Services.TaskbarProgress.TaskbarState.Indeterminate
                : IsPlaying
                    ? SoundLibert.UI.Services.TaskbarProgress.TaskbarState.Normal
                    : SoundLibert.UI.Services.TaskbarProgress.TaskbarState.Paused;
        _taskbar.Update(state, ProgressFraction);
    }

    private void UpdateSmtcMetadata()
    {
        var track = CurrentTrack;
        _smtc?.SetNowPlaying(track?.Title, track?.ArtistDisplay, track?.AlbumTitle, track?.ThumbnailUrl);
    }
#endif

    [ObservableProperty]
    private bool _isShuffled;

    [ObservableProperty]
    private bool _isRepeating;

    [ObservableProperty]
    private bool _isMuted;

    /// <summary>0..1 position shown on the seek slider (decoupled from Position while dragging).</summary>
    [ObservableProperty]
    private double _seekFraction;

    /// <summary>True while the user drags the seek slider; playback updates are ignored then.</summary>
    [ObservableProperty]
    private bool _isSeeking;

    [ObservableProperty]
    private bool _isFullscreenPlayer;

    // ---- Dynamic Now Playing (artwork + colour from the current cover) ----
    // LiquidBackdrop takes both and cross-fades the whole window when they change.

    [ObservableProperty]
    private Bitmap? _artworkImage;

    private static readonly Color RestingAmbient = Color.FromRgb(0xFF, 0x7B, 0x3A);

    /// <summary>The cover's colour; the brand colour while nothing plays.</summary>
    [ObservableProperty]
    private Color _ambientColor = RestingAmbient;

    /// <summary>The colours flow behind the app only while music plays and it is on show.</summary>
    public bool IsShellFlowing => IsPlaying && !IsFullscreenPlayer;

    /// <summary>The same for the fullscreen player's backdrop.</summary>
    public bool IsFullscreenFlowing => IsPlaying && IsFullscreenPlayer;

    // ---- Lyrics ----

    public ObservableCollection<LyricLine> Lyrics { get; } = new();

    [ObservableProperty]
    private bool _hasLyrics;

    [ObservableProperty]
    private bool _showLyrics;

    [ObservableProperty]
    private bool _isLoadingLyrics;

    /// <summary>True while the current track's stream is resolving/starting
    /// (feeds the fullscreen loading pill so a stall reads as "working",
    /// not "frozen"). Mirrors the loading StatusMessage posts below.</summary>
    [ObservableProperty]
    private bool _isTrackLoading;

    /// <summary>"No lyrics" shows only when the load finished with nothing.</summary>
    public bool ShowNoLyrics => !HasLyrics && !IsLoadingLyrics;

    partial void OnHasLyricsChanged(bool value) => OnPropertyChanged(nameof(ShowNoLyrics));
    partial void OnIsLoadingLyricsChanged(bool value) => OnPropertyChanged(nameof(ShowNoLyrics));

    [ObservableProperty]
    private int _currentLyricIndex = -1;

    // Immutable snapshot of the normalized lyrics, swapped on the UI
    // thread when lyrics load and read from the audio thread for the highlight.
    // (The ObservableCollection itself is UI-thread-affine.)
    private SyncedLyrics? _syncedLyrics;

    // ---- Seek fence: stale PositionChanged ticks, captured before a seek but
    // delivered after via Dispatcher.Post, must not overwrite the seek target.
    // The fence is armed on every seek (slider + keyboard + video) and ignores
    // ticks far from the target for a short window; ticks already at the new
    // time pass through, so normal playback resumes instantly.
    private DateTime _seekFenceUntilUtc = DateTime.MinValue;
    private TimeSpan _seekFenceTarget;
    private int _seekFenceGeneration;
    private static readonly TimeSpan SeekFenceWindow = TimeSpan.FromMilliseconds(500);
    private const double SeekFenceToleranceSeconds = 1.0;

    /// <summary>The current synced lyric line (for the single-line display under the cover).</summary>
    public string CurrentLyricText =>
        CurrentLyricIndex >= 0 && CurrentLyricIndex < Lyrics.Count ? Lyrics[CurrentLyricIndex].Text : string.Empty;

    public bool HasCurrentLyric => !string.IsNullOrWhiteSpace(CurrentLyricText);

    /// <summary>Show the single line only when the full lyrics panel is closed and the setting is on.</summary>
    // Not in Video mode: the lyrics are timed for the SONG, the clip has its
    // own intro/outro, so the line drifted over the video.
    public bool ShowSingleLyric => HasCurrentLyric && !ShowLyrics && !IsVideoMode && _appSettings.ShowSingleLyric;

    /// <summary>
    /// What the single lyric line actually binds its visibility to. Mirrors
    /// <see cref="ShowSingleLyric"/>, except the collapse is delayed ~180 ms so
    /// the 0.15 s opacity fade-out stays visible instead of popping.
    /// </summary>
    [ObservableProperty] private bool _singleLyricShown;

    /// <summary>Generation guard for the delayed single-lyric collapse.</summary>
    private int _singleLyricHideGen;

    /// <summary>
    /// Keeps <see cref="SingleLyricShown"/> in sync with <see cref="ShowSingleLyric"/>:
    /// shows instantly, hides after the fade-out finishes. Call it everywhere
    /// <see cref="ShowSingleLyric"/> is raised.
    /// </summary>
    private void SyncSingleLyricShown()
    {
        if (ShowSingleLyric)
        {
            Interlocked.Increment(ref _singleLyricHideGen);
            SingleLyricShown = true;
            return;
        }
        var gen = Interlocked.Increment(ref _singleLyricHideGen);
        _ = Task.Run(async () =>
        {
            await Task.Delay(180);
            Dispatcher.UIThread.Post(() =>
            {
                if (gen == Volatile.Read(ref _singleLyricHideGen) && !ShowSingleLyric)
                    SingleLyricShown = false;
            });
        });
    }

    /// <summary>The lyrics panel is never shown in Video mode (the clip stays unobstructed).</summary>
    public bool ShowLyricsPanel => ShowLyrics && !IsVideoMode;

    partial void OnCurrentLyricIndexChanged(int oldValue, int newValue)
    {
        // Highlight the playing line explicitly (ListBox SelectedIndex proved
        // unreliable with a virtualised list + auto-scroll).
        if (oldValue >= 0 && oldValue < Lyrics.Count) Lyrics[oldValue].IsCurrent = false;
        if (newValue >= 0 && newValue < Lyrics.Count) Lyrics[newValue].IsCurrent = true;

        OnPropertyChanged(nameof(CurrentLyricText));
        OnPropertyChanged(nameof(HasCurrentLyric));
        OnPropertyChanged(nameof(ShowSingleLyric));
        SyncSingleLyricShown();
    }

    partial void OnShowLyricsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSingleLyric));
        OnPropertyChanged(nameof(ShowLyricsPanel));
        SyncSingleLyricShown();
    }

    // ---- Queue panel ----

    [ObservableProperty]
    private bool _showQueue;

    [ObservableProperty]
    private bool _hasQueueItems;

    public ObservableCollection<SongItem> QueueItems { get; } = new();

    [ObservableProperty] private int _queueCurrentIndex = -1;

    public string QueueDurationText
    {
        get
        {
            if (QueueItems.Count == 0) return string.Empty;
            var total = TimeSpan.FromSeconds(QueueItems.Sum(t => t.DurationSeconds));
            return total.TotalHours >= 1
                ? $"{QueueItems.Count} tracks · {(int)total.TotalHours}h {total.Minutes:D2}m"
                : $"{QueueItems.Count} tracks · {total.TotalMinutes:0} min";
        }
    }

    [RelayCommand]
    private void ToggleQueue()
    {
        ShowQueue = !ShowQueue;
        if (ShowQueue) SyncQueue();
    }

    [RelayCommand]
    private void PlayQueueItem(int index)
    {
        // Temporary origin log: catches which tap moves the queue.
        var id = index >= 0 && index < _queueService.Queue.Count ? _queueService.Queue[index].Id : "<bad>";
        System.Diagnostics.Debug.WriteLine($"[Queue] PlayQueueItem index={index} id={id}");
        if (index >= 0) _queueService.SetCurrentIndex(index);
    }

    [RelayCommand]
    private void RemoveQueueItem(int index)
    {
        if (index >= 0) _queueService.RemoveAt(index);
    }

    [RelayCommand]
    private void ClearQueue() => _queueService.Clear();

    [RelayCommand]
    private void ReshuffleQueue()
    {
        _queueService.Reshuffle();
        IsShuffled = _queueService.IsShuffled;
    }

    private void SyncQueue()
    {
        QueueItems.Clear();
        foreach (var track in _queueService.Queue) QueueItems.Add(track);
        QueueCurrentIndex = _queueService.CurrentIndex;
        HasQueueItems = QueueItems.Count > 0;
        OnPropertyChanged(nameof(NextTrackTitle));
        OnPropertyChanged(nameof(NextTrackDisplay));
        OnPropertyChanged(nameof(QueueDurationText));
        SyncFullscreenUpNext();
    }

    /// <summary>In-app updates from GitHub Releases (top-bar pill + Settings → About).</summary>
    public UpdateService Updates { get; }

    [RelayCommand]
    private void RestartToUpdate() => Updates.RestartToUpdate();

    /// <summary>Drag-reorder in the queue panel (the playing track stays current).</summary>
    public void MoveQueueItem(int fromIndex, int toIndex) => _queueService.MoveItem(fromIndex, toIndex);

    public HomeViewModel HomeViewModel { get; }
    public SearchViewModel SearchViewModel { get; }
    public LibraryViewModel LibraryViewModel { get; }
    public SettingsViewModel SettingsViewModel { get; }

    public MainWindowViewModel(
        IMusicProvider musicProvider,
        IAudioPlayer audioPlayer,
        IQueueService queueService,
        IMusicDatabase musicDatabase,
        ArtworkService artwork,
        ILyricsProvider lyricsProvider,
        IAccountService account,
        ILocalLibrary localLibrary,
        IAudioSettings audioSettings,
        IAppSettings appSettings,
        ILikeService likeService,
        IDownloadService downloads,
        IGenreService genreService,
        IViewCountService viewCounts,
        UpdateService updates)
    {
        Updates = updates;
        _musicProvider = musicProvider;
        _audioPlayer = audioPlayer;
        _queueService = queueService;
        _musicDatabase = musicDatabase;
        _artwork = artwork;
        _lyricsProvider = lyricsProvider;
        _account = account;
        _audioSettings = audioSettings;
        _appSettings = appSettings;
        _likeService = likeService;
        _downloads = downloads;
        _genreService = genreService;
        _viewCounts = viewCounts;

        // One central like-sync: rows across screens are separate objects, so
        // a toggle anywhere refreshes every twin (otherwise stale hearts make
        // the next tap toggle "backwards"). Both ends are app-lifetime
        // singletons, so this subscription cannot leak.
        _likeService.LikeChanged += (_, e) =>
            Dispatcher.UIThread.Post(() => ApplyLikeEverywhere(e.SongId, e.IsLiked));

        // Keep the player-bar download button in step with the current track,
        // and refresh the "downloaded" badge on every visible row.
        _downloads.Changed += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            if (e.State is DownloadState.Downloaded or DownloadState.None)
                ApplyDownloadEverywhere(e.SongId, e.State == DownloadState.Downloaded);
            if (e.State == DownloadState.Failed)
                ShowToast("Couldn't download the track. Try again later.", isError: true);
            if (CurrentTrack != null && e.SongId == CurrentTrack.Id)
            {
                OnPropertyChanged(nameof(IsCurrentTrackDownloaded));
                OnPropertyChanged(nameof(IsCurrentTrackDownloading));
            }
        });

        // Restore persisted playback preferences (volume / EQ). Mute is not
        // restored — a persisted mute made the app start silent and look broken.
        Volume = _appSettings.Volume;
        IsMuted = false;

        HomeViewModel = new HomeViewModel(queueService, musicDatabase, musicProvider, account, appSettings);
        SearchViewModel = new SearchViewModel(musicProvider, queueService, audioPlayer, musicDatabase, likeService, account, downloads, viewCounts);
        LibraryViewModel = new LibraryViewModel(musicDatabase, queueService, localLibrary, musicProvider, account, likeService, downloads, genreService, viewCounts);
        SettingsViewModel = new SettingsViewModel(account, audioSettings, appSettings, downloads, audioPlayer, musicDatabase, artwork, updates);
        SettingsViewModel.Notify = ShowToast;
        SettingsViewModel.LyricsFontSizeChanged += v => LyricsFontSize = v;
        SettingsViewModel.PlaybackModesChanged += ApplyPlaybackModes;
        LyricsFontSize = _appSettings.LyricsFontSize is <= 0 or 15 ? 22 : Math.Clamp(_appSettings.LyricsFontSize, 11, 30);

        // Friendly, user-facing errors from the child view models (toast).
        HomeViewModel.ReportError = ReportError;
        SearchViewModel.ReportError = ReportError;
        LibraryViewModel.ReportError = ReportError;

        _currentView = HomeViewModel;
        _activeSection = HomeViewModel;

        // Restore the previous session (queue + position) BEFORE the playback
        // events are wired, so restoring does not auto-start playback.
        RestoreSession();

        // NOTE: these events can fire on background threads (audio callback,
        // timer, queue changes from TrackEnded). All-bound properties must be
        // set on the UI thread — Avalonia does not marshal them for us.
        _queueService.CurrentTrackChanged += (_, _) =>
        {
            var generation = Interlocked.Increment(ref _playGeneration);
            var track = _queueService.CurrentTrack;
            var requestedVideoTrackId = Interlocked.Exchange(ref _videoStartTrackId, null);
            // Video mode is used ONLY when a video context requested it: a video
            // card / liked-videos row / artist Videos rail (one-shot track id),
            // or a queue the user put into Video mode (the next track has a
            // clip). The CONTEXT decides — a row merely tagged as a clip no
            // longer forces Video mode from an ordinary song queue, so album /
            // playlist / search song rows always play as songs.
            var startInVideoMode = track != null &&
                                   ((requestedVideoTrackId != null &&
                                     string.Equals(track.Id, requestedVideoTrackId, StringComparison.Ordinal) &&
                                     !string.IsNullOrWhiteSpace(track.VideoVersionId))
                                    || (_videoQueueMode && HasClip(track)));
            System.Diagnostics.Debug.WriteLine(
                $"[Video] queue transition track={track?.Id ?? "<null>"}, requested={requestedVideoTrackId ?? "<none>"}, start={startInVideoMode}, queueIndex={_queueService.CurrentIndex}, queueSize={_queueService.Queue.Count}");
            if (!startInVideoMode) Interlocked.Exchange(ref _preparedVideoStreams, null);
            Dispatcher.UIThread.Post(() =>
            {
                if (generation != Volatile.Read(ref _playGeneration)) return;

                var wasVideoMode = IsVideoMode;
                CurrentTrack = track;
                // Next/Previous/SetCurrentIndex raise only CurrentTrackChanged
                // (not QueueChanged), so the queue position — and the "Up next"
                // line derived from it — would stay stuck on the previous track.
                QueueCurrentIndex = _queueService.CurrentIndex;
                OnPropertyChanged(nameof(NextTrackTitle));
                OnPropertyChanged(nameof(NextTrackDisplay));
                if (track == null)
                {
                    // The queue emptied (clear / last row removed): stop
                    // everything instead of leaving orphaned audio behind with
                    // no current track.
                    _settingVideoModeForTrack = true;
                    IsVideoMode = false;
                    _settingVideoModeForTrack = false;
                    HasVideoCounterpart = false;
                    _ = _audioPlayer.StopAsync();
                    StopCanvas();
                    IsPlaying = false;
                    Position = TimeSpan.Zero;
                    Duration = TimeSpan.Zero;
                    StatusMessage = string.Empty;
                    return;
                }
                HasVideoCounterpart = CanToggleVersions(track);
                _settingVideoModeForTrack = true;
                IsVideoMode = startInVideoMode;
                _settingVideoModeForTrack = false;

                // Fullscreen follows only intentional video queues (a video card
                // or sticky video mode) — an incidental clip inside a song
                // queue must not yank a listener into fullscreen.
                if (startInVideoMode && (requestedVideoTrackId != null || _videoQueueMode)) IsFullscreenPlayer = true;

                // A queue change normally leaves Video mode. Stop the old VLC
                // clip directly; the mode setter is suppressed during this
                // track transition to avoid launching playback twice.
                if (wasVideoMode && !startInVideoMode) _video?.Stop();
                _ = PlayCurrentTrackAsync(generation);
            });
        };

        _audioPlayer.StateChanged += (_, state) =>
        {
            // In Video mode the transport state comes from the video player.
            if (IsVideoMode) return;
            Dispatcher.UIThread.Post(() => IsPlaying = state == MediaPlayerState.Playing);
        };

        _queueService.QueueReplaced += (_, _) =>
        {
            Interlocked.Increment(ref _queueReplaceGeneration);
            _consecutivePlayFailures = 0;
            // Central guard: a queue built for SONGS must leave Video mode. Only a
            // video-context SetQueue (video card / liked videos) sets the intent,
            // so Video mode can no longer leak into an ordinary song queue started
            // from any list. Edits to the current queue (repeat, shuffle, reorder,
            // add) keep the mode.
            var videoIntent = _videoQueueIntent;
            _videoQueueIntent = false;
            if (!videoIntent) _videoQueueMode = false;
        };

        _queueService.QueueChanged += (_, _) => Dispatcher.UIThread.Post(SyncQueue);

        _audioPlayer.PositionChanged += (_, pos) =>
        {
            var seekGeneration = Volatile.Read(ref _seekFenceGeneration);
            // A tick captured before a seek but delivered after must not echo
            // the old time back over the seek target (visible jump-back of
            // both the slider and the highlighted line).
            if (IsStaleSeekEcho(pos)) return;
            // Bind each queued position to its lyrics snapshot. A track/load
            // change must not apply an index from the previous snapshot.
            var lyricsSnapshot = Volatile.Read(ref _syncedLyrics);
            Dispatcher.UIThread.Post(() =>
            {
                // While the user scrubs, the highlight follows the finger
                // (drag preview, Velune pattern) — audio ticks must not drag
                // it back to the still-playing old time.
                if (IsSeeking) return;
                if (seekGeneration != Volatile.Read(ref _seekFenceGeneration)) return;
                if (IsStaleSeekEcho(pos)) return;
                if (!ReferenceEquals(lyricsSnapshot, Volatile.Read(ref _syncedLyrics))) return;
                Position = pos;
                UpdateCurrentLyricIndex(pos);
            });
        };

        // Natural track end advances the queue. Manual StopAsync() does not
        // raise this (NAudioPlayer unsubscribes before stopping), so no
        // spuriousNext on user stop.
        _audioPlayer.TrackEnded += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            FlushPlaySession();
            AdvanceOrRecommend();
        });

        _audioPlayer.PlaybackFailed += (_, error) => Dispatcher.UIThread.Post(() => OnPlaybackFailed(error));

        ApplyStartupPreferences();
    }

    /// <summary>
    /// Applies the persisted startup preferences (shuffle/repeat defaults,
    /// animated cover, single lyric line). Called once after the events are wired.
    /// </summary>
    private void ApplyStartupPreferences()
    {
        // The restored queue is already in its playing order: reshuffling it on
        // every launch would change what plays next for no reason.
        if (_appSettings.ShuffleOnByDefault && !_queueService.IsShuffled)
        {
            _queueService.EnableShuffleKeepingOrder();
            IsShuffled = _queueService.IsShuffled;
        }

        ApplyRepeatMode();

        IsCanvasEnabled = _appSettings.ShowCanvas;

        // "Up next" and the queue panel need the restored queue right away.
        SyncQueue();
    }

    private void ApplyRepeatMode()
    {
        _queueService.RepeatMode = _appSettings.RepeatMode switch
        {
            "All" => RepeatMode.All,
            "One" => RepeatMode.One,
            _ => RepeatMode.Off
        };
        IsRepeating = _queueService.RepeatMode != RepeatMode.Off;
        OnPropertyChanged(nameof(RepeatIcon));
    }

    /// <summary>Settings → Playback changed shuffle or repeat: applies to the running queue too.</summary>
    private void ApplyPlaybackModes()
    {
        if (_appSettings.ShuffleOnByDefault != _queueService.IsShuffled) _queueService.ToggleShuffle();
        IsShuffled = _queueService.IsShuffled;
        ApplyRepeatMode();
    }

    public override void Initialize()
    {
        // Child view models are created here, not by DI, so they must be
        // initialized explicitly — previously nothing called Initialize() and
        // Home/Library never loaded their data on startup.
        HomeViewModel.Initialize();
        SearchViewModel.Initialize();
        // Play-all/Shuffle inside a videos tab plays clips through the shell.
        LibraryViewModel.PlayVideosRequest = (tracks, start) => PlayAsVideo(tracks, start);
        // "Jump back in" clip tiles replay as clips, not songs.
        HomeViewModel.PlayVideoRequest = (tracks, start) => PlayAsVideo(tracks, start);
        // Clip rows on every browse/search page replay as clips, not songs.
        SearchViewModel.PlayVideoRequest = (tracks, start) => PlayAsVideo(tracks, start);
        LibraryViewModel.Initialize();
        // Load recorded offline copies + start the download worker, then fill in
        // what a restored queue does not remember.
        _ = InitializeDownloadsAndRestoredQueueAsync();
        // The Animated-cover toggle applies live (no restart needed).
        _appSettings.ShowCanvasChanged += (_, on) => Dispatcher.UIThread.Post(() => ApplyShowCanvas(on));
        StartNetworkMonitor();
        _ = RefreshPlaylistsAsync();
        // Signing out leaves nothing of the account behind: neither the sign-in
        // window's Google session nor the personal Home feed on disk.
        _account.SignedOut += (_, _) =>
        {
            _ = Task.Run(SoundLibert.UI.Services.WebViewProfile.Clear);
            SoundLibert.UI.Services.HomeFeedCache.Clear();
        };

        // Background update checks (installed builds only; off in Settings). A downloaded
        // version is announced by the window's card, with its release notes.
        Updates.BeforeRestart = () =>
        {
            EndPlaybackSession();
            SaveSession();
        };
        // The track menu's playlist list follows every library reload: a sign-in
        // changes which playlists are in scope, and the reload adopts local ones.
        LibraryViewModel.LibraryReloaded += () => _ = ReloadMenuPlaylistsAsync();
        LibraryViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryViewModel.IsCollectionOpen)) NotifyNavigation();
        };
        if (_appSettings.AutoCheckUpdates) Updates.StartAutomaticChecks();
    }

    /// <summary>
    /// The session file keeps only the track's identity, so a restored row would
    /// show an empty heart for a liked song (and the next tap would unlike it).
    /// </summary>
    private async Task InitializeDownloadsAndRestoredQueueAsync()
    {
        try
        {
            await _downloads.InitializeAsync();
            var liked = await _musicDatabase.GetLikedSongIdsAsync();
            foreach (var track in _queueService.Queue.ToList())
            {
                track.IsLiked = liked.Contains(track.Id);
                track.IsDownloaded = _downloads.IsDownloaded(track.Id);
            }
            OnPropertyChanged(nameof(IsCurrentTrackLiked));
            OnPropertyChanged(nameof(IsCurrentTrackDownloaded));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Resume] could not hydrate the restored queue: {ex.Message}");
        }
    }

    // ---- Network state (offline banner) ----

    [ObservableProperty] private bool _isOffline;

    /// <summary>Shown when the YouTube session expired (cleared on re-sign-in).</summary>
    [ObservableProperty] private string? _sessionNote;

    private const string ReauthNote =
        "YouTube Music no longer accepts your sign-in — your library is kept. Sign in again from Settings to sync.";

    /// <summary>Single top banner text: a session problem wins over "offline".</summary>
    public string? StatusBanner => !string.IsNullOrEmpty(SessionNote)
        ? SessionNote
        : IsOffline ? "You're offline — downloaded songs keep playing." : null;

    public bool ShowStatusBanner => !string.IsNullOrEmpty(StatusBanner);

    /// <summary>True when the banner reports a session problem (red-ish).</summary>
    public bool IsStatusBannerDanger => !string.IsNullOrEmpty(SessionNote);

    partial void OnIsOfflineChanged(bool value) => RaiseBanner();
    partial void OnSessionNoteChanged(string? value) => RaiseBanner();

    private void RaiseBanner()
    {
        OnPropertyChanged(nameof(StatusBanner));
        OnPropertyChanged(nameof(ShowStatusBanner));
        OnPropertyChanged(nameof(IsStatusBannerDanger));
    }

    private bool _networkMonitorStarted;

    private void StartNetworkMonitor()
    {
        if (_networkMonitorStarted) return;
        _networkMonitorStarted = true;
        try
        {
            IsOffline = !System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, e) =>
                Dispatcher.UIThread.Post(() => IsOffline = !e.IsAvailable);
            _account.SignedIn += (_, _) => Dispatcher.UIThread.Post(() => SessionNote = null);
            _account.SignedOut += (_, _) => Dispatcher.UIThread.Post(() => SessionNote = null);
            // YouTube no longer accepts the session: the library is kept, only
            // syncing pauses.
            _account.ReauthenticationRequired += (_, _) => Dispatcher.UIThread.Post(() =>
                SessionNote = ReauthNote);
            if (_account.NeedsReauthentication) SessionNote = ReauthNote;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Net] monitor failed: {ex.Message}");
        }
    }

    /// <summary>Applies the Settings → Animated-cover toggle immediately.</summary>
    private void ApplyShowCanvas(bool on)
    {
        IsCanvasEnabled = on;
        if (!on) StopCanvas();
        else if (IsFullscreenPlayer && !IsVideoMode && CurrentTrack is { } track) _ = LoadCanvasAsync(track);
    }

    /// <summary>Restores the previous session's queue and play position (paused).</summary>
    private void RestoreSession()
    {
        if (!_appSettings.RememberLastSession) return;
        var session = _appSettings.LastSession;
        if (session?.Queue is not { Count: > 0 }) return;

        try
        {
            var tracks = session.Queue.Select(SessionState.ToSong).ToList();
            var index = Math.Clamp(session.Index, 0, tracks.Count - 1);

            if (session.IsVideoQueue)
            {
                // A saved clip queue must keep playing as clips: without the
                // intent the QueueChanged guard drops Video mode and every
                // restored video replays as a song.
                ArmVideoQueue();
            }
            _queueService.SetQueue(tracks, index);
            // The hook that normally consumes the one-shot intent is not wired
            // yet: left armed, it would turn the user's first song queue into video.
            _videoQueueIntent = false;
            CurrentTrack = _queueService.CurrentTrack;
            if (session.IsVideoQueue && CurrentTrack?.VideoVersionId is { Length: > 0 })
            {
                // Restore the mode without starting playback (the session
                // stays paused until the user presses Play).
                _settingVideoModeForTrack = true;
                IsVideoMode = true;
                _settingVideoModeForTrack = false;
            }
            _resumePosition = TimeSpan.FromSeconds(Math.Max(0, session.PositionSeconds));
            _resumeTrackId = CurrentTrack?.Id;
            // Show the restored track's length too: the bar read "2:59 / 0:00"
            // with the thumb at zero until Play was pressed.
            if (CurrentTrack is { DurationSeconds: > 0 } restored)
                Duration = TimeSpan.FromSeconds(restored.DurationSeconds);
            Position = _resumePosition;
            System.Diagnostics.Debug.WriteLine(
                $"[Resume] restored {tracks.Count} tracks, index {index}, pos {_resumePosition:mm\\:ss}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Resume] restore failed: {ex.Message}");
        }
    }

    /// <summary>Persists the queue + position for next launch, then saves settings.</summary>
    public void SaveSession()
    {
        try
        {
            _appSettings.LastSession = _appSettings.RememberLastSession && _queueService.Queue.Count > 0
                ? new SessionState
                {
                    Queue = _queueService.Queue.Select(s => SessionState.FromSong(s, IsVideoMode)).ToList(),
                    Index = _queueService.CurrentIndex,
                    PositionSeconds = Position.TotalSeconds,
                    IsVideoQueue = IsVideoMode
                }
                : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Resume] save failed: {ex.Message}");
        }
        _appSettings.Save();
    }

    // NAudio always follows, or a change made while watching a clip would be
    // lost when the next song starts. VLC follows only while it is audible (the
    // Canvas stays muted by itself); PlayVideoAsync applies both when a clip starts.
    partial void OnVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeIcon));
        _ = _audioPlayer.SetVolumeAsync(value);
        if (IsVideoMode) _video?.SetVolume(value);
        _appSettings.Volume = value;
    }

    partial void OnIsMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(VolumeIcon));
        _audioPlayer.IsMuted = value;
        if (IsVideoMode) _video?.SetMuted(value);
        // Mute is intentionally session-only: persisting it meant the app could
        // start silent after a stray mute and look completely broken. Volume is
        // still remembered; mute always starts off.
    }

    /// <summary>Pages opened on top of the current section; drives the back button and page motion.</summary>
    public int NavigationDepth => _navStack.Count;

    /// <summary>The back button shows while there is somewhere to go back to.</summary>
    public bool CanGoBack => _navStack.Count > 0 ||
                             (ReferenceEquals(CurrentView, LibraryViewModel) && LibraryViewModel.IsCollectionOpen);

    private void NotifyNavigation()
    {
        OnPropertyChanged(nameof(NavigationDepth));
        OnPropertyChanged(nameof(CanGoBack));
    }

    /// <summary>Typing in the search field: shows the search page and live suggestions.</summary>
    public void OnSearchTyped(string text)
    {
        if (!ReferenceEquals(CurrentView, SearchViewModel) && !string.IsNullOrWhiteSpace(text))
            NavigateToSearch();
        if (SearchViewModel.Query != text) SearchViewModel.Query = text;
    }

    [RelayCommand]
    private void NavigateToHome()
    {
        _navStack.Clear();
        CurrentView = HomeViewModel;
        NotifyNavigation();
        IsSearchVisible = false;
        // Refresh the feed (stats + recommendation shelves) on every visit.
        HomeViewModel.Initialize();
    }

    [RelayCommand]
    private void NavigateToSearch()
    {
        _navStack.Clear();
        CurrentView = SearchViewModel;
        NotifyNavigation();
        IsSearchVisible = true;
        // Refresh recent searches on every visit.
        SearchViewModel.Initialize();
    }

    /// <summary>Navigates to the Library and opens one of the built-in collections
    /// (0 Liked, 1 Recent, 2 Local, 3 History).</summary>
    public void NavigateToLibraryTab(int tab)
    {
        NavigateToLibraryCommand.Execute(null);
        LibraryViewModel.CloseCollectionCommand.Execute(null);
        var section = tab switch
        {
            1 => LibrarySection.Recent,
            2 => LibrarySection.Local,
            3 => LibrarySection.History,
            _ => LibrarySection.Liked
        };
        LibraryViewModel.OpenSection(section);
    }

    /// <summary>Navigates to Search and runs the given query (used by Home chips).</summary>
    [RelayCommand]
    private void SearchFor(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        _navStack.Clear();
        CurrentView = SearchViewModel;
        IsSearchVisible = true;
        SearchQuery = query;
        SearchViewModel.Query = query;
        SearchViewModel.SearchCommand.Execute(null);
    }

    [RelayCommand]
    private void NavigateToLibrary()
    {
        _navStack.Clear();
        // The Library row always leads back to the Library itself.
        if (ReferenceEquals(CurrentView, LibraryViewModel) && LibraryViewModel.IsCollectionOpen)
            LibraryViewModel.CloseCollectionCommand.Execute(null);
        CurrentView = LibraryViewModel;
        NotifyNavigation();
        IsSearchVisible = false;
        LibraryViewModel.Initialize();
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        _navStack.Clear();
        CurrentView = SettingsViewModel;
        NotifyNavigation();
        IsSearchVisible = false;
        SettingsViewModel.Refresh();
    }

    [RelayCommand]
    private void GoBack()
    {
        // A collection opened inside the Library closes first.
        if (ReferenceEquals(CurrentView, LibraryViewModel) && LibraryViewModel.IsCollectionOpen)
        {
            LibraryViewModel.CloseCollectionCommand.Execute(null);
            NotifyNavigation();
            return;
        }
        if (_navStack.Count == 0) return;
        var previous = _navStack.Pop();
        NotifyNavigation();
        CurrentView = previous;
        RefreshActiveView();
    }

    /// <summary>Re-runs the load for whichever top-level view is now active.</summary>
    private void RefreshActiveView()
    {
        switch (CurrentView)
        {
            case HomeViewModel home: home.Initialize(); break;
            case SearchViewModel search: search.Initialize(); break;
            case LibraryViewModel library: library.Initialize(); break;
            case SettingsViewModel settings: settings.Refresh(); break;
        }
    }

    /// <summary>
    /// Opens an album, playlist or artist page (routed by browse-id prefix) and
    /// pushes the current view so the user can go back.
    /// </summary>
    public async Task OpenBrowseAsync(string id, string title, string? thumbnail, string? subtitle)
    {
        if (string.IsNullOrWhiteSpace(id)) return;

        _navStack.Push(CurrentView);

        // Artist pages have their own richer view (top songs, albums, related).
        if (id.StartsWith("UC", StringComparison.Ordinal))
        {
            var artist = new ArtistViewModel(_musicProvider, _queueService, _musicDatabase, _likeService, _downloads, _viewCounts, _account);
            artist.PlayVideoRequest = (tracks, start) => PlayAsVideo(tracks, start);
            artist.OpenDiscographyRequest = () => OpenArtistDiscography(artist);
            artist.StartRadioRequest = seed => _ = PlayRadioAsync(seed);
            CurrentView = artist;
            await artist.LoadAsync(id, title, thumbnail);
            return;
        }

        var collection = new CollectionViewModel(_musicProvider, _queueService, _musicDatabase, _likeService, _downloads, _genreService, _viewCounts, _account);
        collection.PlayVideoRequest = (tracks, start) => PlayAsVideo(tracks, start);
        CurrentView = collection;

        if (id.StartsWith(PlaylistEntity.LocalIdPrefix, StringComparison.Ordinal))
            await collection.LoadLocalPlaylistAsync(id, title, thumbnail);
        else
            await collection.LoadBrowseAsync(id, title, thumbnail, subtitle);
    }

    /// <summary>
    /// Opens the full-page artist discography (albums / singles / appears-on /
    /// playlists in one place with category chips). Pushes the nav stack so the
    /// back arrow returns to the artist page.
    /// </summary>
    public void OpenArtistDiscography(ArtistViewModel artist)
    {
        if (artist == null || !artist.HasDiscography) return;

        _navStack.Push(CurrentView);

        var vm = new ArtistDiscographyViewModel
        {
            OpenRequest = card => _ = OpenBrowseAsync(card.Id, card.Title, card.CoverUrl, card.Subtitle)
        };
        vm.Load(artist.Title, artist.ThumbnailUrl,
            artist.Albums.ToList(), artist.Singles.ToList(),
            artist.AppearsOn.ToList(), artist.Playlists.ToList());
        CurrentView = vm;
    }

    // ---- Shelves: every card on every page lands here ----

    /// <summary>The songs or the videos of the shelf a card sits on: the queue when it plays.</summary>
    private static List<SongItem> NeighboursOf(ShelfCard card)
    {
        IEnumerable<ShelfCard> cards = card.Shelf switch
        {
            CardShelfVm shelf => shelf.Items,
            VideoShelfVm shelf => shelf.Items,
            RowShelfVm shelf => shelf.Items,
            GridShelfVm shelf => shelf.Items,
            _ => new[] { card }
        };
        return cards.Where(c => c.Item.Kind == card.Item.Kind && c.Item.Song != null).Select(c => c.Item.Song!).ToList();
    }

    /// <summary>A tap: plays a song or a video, opens anything else.</summary>
    public void OpenShelfCard(ShelfCard card)
    {
        var item = card.Item;
        switch (item.Kind)
        {
            case ShelfItemKind.Song when item.Song != null:
                PlaySongs(NeighboursOf(card), item.Song);
                break;
            case ShelfItemKind.Video when item.Song != null:
                PlayAsVideo(NeighboursOf(card), item.Song);
                break;
            default:
                IsFullscreenPlayer = false;
                _ = OpenBrowseAsync(item.Id, item.Title, item.ThumbnailUrl, item.Subtitle);
                break;
        }
    }

    /// <summary>The play button on a card: an album or a playlist starts without opening.</summary>
    public void PlayShelfCard(ShelfCard card)
    {
        if (card.Item.Kind is ShelfItemKind.Album or ShelfItemKind.Playlist)
            _ = PlayCollectionAsync(card.Item.Id, card.Item.Title, card.Item.ThumbnailUrl);
        else
            OpenShelfCard(card);
    }

    /// <summary>A shelf's "Show all".</summary>
    public void ShowAll(ShelfVm shelf)
    {
        if (shelf.ShowAllAction != null)
        {
            shelf.ShowAllAction();
            return;
        }

        IsFullscreenPlayer = false;
        if (shelf.OpenFeed != null)
        {
            _ = OpenVideoFeedAsync(shelf.Title, shelf.OpenFeed);
            return;
        }

        var id = shelf.MoreBrowseId;
        if (string.IsNullOrWhiteSpace(id)) return;
        if (id == "FEmusic_new_releases_videos")
            _ = OpenVideoFeedAsync(shelf.Title, () => new SoundLibert.Domain.Video.SinglePageVideoFeed(ct => _musicProvider.GetNewVideosAsync(ct)));
        else if (id.StartsWith("VL", StringComparison.Ordinal) || id.StartsWith("PL", StringComparison.Ordinal))
            _ = OpenBrowseAsync(id, shelf.Title, null, null);
        else
            _ = OpenShelfPageAsync(new ShelfPageViewModel(_musicProvider, shelf.Title, id, shelf.MoreParams));
    }

    /// <summary>A mood or genre tile.</summary>
    public void OpenMood(MoodTile tile)
    {
        IsFullscreenPlayer = false;
        _ = OpenShelfPageAsync(new ShelfPageViewModel(_musicProvider, tile.Name, tile.BrowseId, tile.Params)
        {
            Eyebrow = "EXPLORE",
            Color = tile.Color
        });
    }

    private async Task OpenShelfPageAsync(ShelfPageViewModel page)
    {
        _navStack.Push(CurrentView);
        CurrentView = page;
        await page.LoadAsync();
    }

    /// <summary>The full page of an artist's channel videos (their Videos rail's "Show all").</summary>
    public Task OpenArtistVideosAsync(string artistId, string artistName, string? thumbnail)
    {
        if (string.IsNullOrWhiteSpace(artistName)) return Task.CompletedTask;

        return ShowVideoFeedAsync(new VideoFeedViewModel(_queueService, _downloads,
            () => _musicProvider.OpenArtistVideoFeed(artistId, artistName))
        {
            Title = artistName,
            ThumbnailUrl = thumbnail,
            IsRound = true,
            Glyph = SoundLibert.UI.Controls.IconKind.User
        });
    }

    /// <summary>The full page behind a video rail on Home.</summary>
    public Task OpenVideoFeedAsync(string title, Func<SoundLibert.Domain.Video.IVideoFeed> openFeed)
        => ShowVideoFeedAsync(new VideoFeedViewModel(_queueService, _downloads, openFeed) { Title = title });

    /// <summary>Pushes the nav stack, so the back arrow returns to where "Show all" was pressed.</summary>
    private async Task ShowVideoFeedAsync(VideoFeedViewModel page)
    {
        _navStack.Push(CurrentView);
        page.PlayVideoRequest = (tracks, start) => PlayAsVideo(tracks, start);
        CurrentView = page;
        await page.LoadAsync();
    }

    // ---- Fullscreen Canvas + Song/Video toggle ----
    // Song-mode Canvas plays a continuous muted segment from one stable offset;
    // the resolved URL is reused for the loop. Video mode plays the full clip
    // with its own audio.

    private VideoPlaybackService? _video;
    private int _canvasGeneration;
    private const int CanvasStartSeconds = 12;
    /// <summary>The clip loaded in the Canvas (the provider caches its stream URL).</summary>
    private string? _canvasVideoId;

    /// <summary>
    /// Song-mode Canvas resume: where the muted snippet was when fullscreen
    /// closed, so re-entering continues from the exit moment instead of
    /// restarting. Keyed by video id — a different track still starts fresh.
    /// </summary>
    private string? _canvasResumeVideoId;
    private int _canvasResumeSeconds;

    /// <summary>User toggle: follow the current track with an animated cover (default on).</summary>
    [ObservableProperty] private bool _isCanvasEnabled = true;

    /// <summary>True while a Canvas video is actually playing.</summary>
    [ObservableProperty] private bool _isCanvasActive;

    /// <summary>The Canvas surface has shown the first frame of the current clip.</summary>
    [ObservableProperty] private bool _isCanvasFrameReady;

    /// <summary>A clip was requested but no frame is on screen yet (loading animation).</summary>
    [ObservableProperty] private bool _isCanvasLoading;

    partial void OnIsCanvasFrameReadyChanged(bool value)
    {
        if (value && IsCanvasLoading)
            System.Diagnostics.Trace.WriteLine($"[Canvas] first frame after {_canvasLoadClock.ElapsedMilliseconds} ms (video={IsVideoMode})");
        if (value) IsCanvasLoading = false;
        // Opened while the song is paused: show the frame, then wait for the song.
        if (value && !IsVideoMode && !IsPlaying) _video?.Pause();
        OnPropertyChanged(nameof(ShowAmbientBackground));
    }

    /// <summary>
    /// A 24×24 copy of the cover. Upscaled with smooth interpolation it reads
    /// as a heavy blur for free (no per-frame BlurEffect) — the ambient
    /// background behind tracks without a Canvas.
    /// </summary>
    [ObservableProperty] private Bitmap? _artworkBackdrop;

    partial void OnArtworkImageChanged(Bitmap? value)
    {
        try
        {
            ArtworkBackdrop = value?.CreateScaledBitmap(new PixelSize(24, 24), BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Ambient] backdrop failed: {ex.Message}");
            ArtworkBackdrop = null;
        }
    }

    /// <summary>Fullscreen without a visible Canvas frame: animated ambient glow.</summary>
    public bool ShowAmbientBackground => IsFullscreenPlayer && !(IsCanvasActive && IsCanvasFrameReady);

    /// <summary>Frame buffer size: small for the muted Canvas, full for Video mode.</summary>
    public int CanvasMaxFrameSide => IsVideoMode ? 960 : 640;

    private readonly System.Diagnostics.Stopwatch _canvasLoadClock = new();

    /// <summary>New clip: hide the surface until its first frame, show the loader.</summary>
    private void BeginCanvasLoad()
    {
        IsCanvasFrameReady = false;
        if (!IsCanvasLoading) _canvasLoadClock.Restart();
        IsCanvasLoading = true;
    }

    /// <summary>
    /// True when the latest Canvas video frame is bright (set by the
    /// <c>CanvasVideoView</c> from the decoded frame). Drives the adaptive
    /// button colours: a bright video needs dark glyphs, a dark one light.
    /// </summary>
    [ObservableProperty] private bool _isCanvasLight;

    /// <summary>True when the current track has an official music-video counterpart.</summary>
    [ObservableProperty] private bool _hasVideoCounterpart;

    /// <summary>False = play the song audio; True = play the music-video audio.</summary>
    [ObservableProperty] private bool _isVideoMode;

    /// <summary>Label for the Song/Video toggle button.</summary>
    public string VideoModeButtonText => IsVideoMode ? "Video" : "Song";

    /// <summary>Tooltip for the Song/Video toggle button.</summary>
    public string VideoModeToolTip => IsVideoMode ? "Switch to song audio" : "Switch to music video";

    /// <summary>Current Video-mode quality label ("Auto", "1080p", …).</summary>
    public string VideoQualityText => _appSettings.VideoQuality switch
    {
        1080 => "1080p",
        720 => "720p",
        480 => "480p",
        360 => "360p",
        _ => "Auto (720p)"
    };

    /// <summary>Sets the Video-mode quality (0 = auto) and re-resolves if watching.</summary>
    [RelayCommand]
    private void SetVideoQuality(string? value)
    {
        _appSettings.VideoQuality = int.TryParse(value, out var h) ? h : 0;
        OnPropertyChanged(nameof(VideoQualityText));

        // Re-resolve the clip so the change takes effect immediately.
        if (IsVideoMode && CurrentTrack is { } track)
        {
            var generation = Interlocked.Increment(ref _playGeneration);
            _ = PlayCurrentTrackAsync(generation);
        }
    }

    partial void OnIsCanvasLightChanged(bool value) => OnPropertyChanged(nameof(IsFullscreenBackgroundLight));

    partial void OnIsCanvasActiveChanged(bool value)
    {
        if (!value) IsCanvasLoading = false;
        OnPropertyChanged(nameof(ShowAmbientBackground));
        OnPropertyChanged(nameof(IsFullscreenBackgroundLight));
        OnPropertyChanged(nameof(ShowCanvasShade));
        OnPropertyChanged(nameof(ShowVideoShade));
    }

    partial void OnIsPlayingChanged(bool value)
    {
        EqBars.IsPlaying = value;
        OnPropertyChanged(nameof(IsShellFlowing));
        OnPropertyChanged(nameof(IsFullscreenFlowing));

        // Only time actually listened counts in the history.
        if (_pendingTrack != null)
        {
            if (value) _pendingListenClock.Start();
            else _pendingListenClock.Stop();
        }

#if WINDOWS
        _smtc?.SetPlaybackState(value, CurrentTrack != null);
        UpdateTaskbarProgress();
#endif
        // In the full player the controls step aside after a few seconds of no
        // input; pausing brings them straight back.
        if (IsFullscreenPlayer)
        {
            if (value) ArmFullscreenControlsTimer();
            else { _fsControlsTimer?.Stop(); ShowFullscreenControls = true; }
        }

        // Song mode keeps the muted Canvas in step with the audio. In Video mode
        // this player IS the audio, so it must not be driven from here.
        if (IsVideoMode) return;
        // Resume the canvas only while it is visible: resuming a hidden engine
        // (paused on fullscreen close) would decode in the background for nobody.
        if (value) { if (IsFullscreenPlayer) _video?.Resume(); }
        else _video?.Pause();
    }

    // ---- Fullscreen control overlay (auto-hide) ----

    /// <summary>Fullscreen controls are visible unless the idle timer hid them.</summary>
    [ObservableProperty] private bool _showFullscreenControls = true;

    /// <summary>Fade target for the fullscreen controls overlay.</summary>
    public double FullscreenControlsOpacity => ShowFullscreenControls ? 1 : 0;

    partial void OnShowFullscreenControlsChanged(bool value)
        => OnPropertyChanged(nameof(FullscreenControlsOpacity));

    /// <summary>Dark cover-derived shade over the Song-mode Canvas.</summary>
    public bool ShowCanvasShade => IsCanvasActive && !IsVideoMode;

    /// <summary>Very light shade over the Video-mode clip so the picture stays bright.</summary>
    public bool ShowVideoShade => IsCanvasActive && IsVideoMode;

    private DispatcherTimer? _fsControlsTimer;

    /// <summary>Reveals the fullscreen controls and restarts the idle countdown.</summary>
    public void NotifyFullscreenActivity()
    {
        ShowFullscreenControls = true;
        ArmFullscreenControlsTimer();
    }

    /// <summary>The view says when the controls must stay (pointer on them, details scrolled, a menu open).</summary>
    public Func<bool>? KeepFullscreenControls { get; set; }

    private void ArmFullscreenControlsTimer()
    {
        if (!IsFullscreenPlayer)
        {
            _fsControlsTimer?.Stop();
            ShowFullscreenControls = true;
            return;
        }

        _fsControlsTimer ??= CreateFullscreenControlsTimer();
        _fsControlsTimer.Stop();
        // A clip wants a clear picture sooner; a song can wait a little longer.
        _fsControlsTimer.Interval = TimeSpan.FromSeconds(IsVideoMode ? 3 : 5);
        _fsControlsTimer.Start();
    }

    private DispatcherTimer CreateFullscreenControlsTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!IsFullscreenPlayer || !IsPlaying) return;
            if (KeepFullscreenControls?.Invoke() == true)
            {
                timer.Start();
                return;
            }
            ShowFullscreenControls = false;
        };
        return timer;
    }

    partial void OnIsVideoModeChanged(bool value)
    {
        OnPropertyChanged(nameof(VideoModeButtonText));
        OnPropertyChanged(nameof(VideoModeToolTip));
        OnPropertyChanged(nameof(CanvasMaxFrameSide));
        OnPropertyChanged(nameof(ShowSingleLyric));
        OnPropertyChanged(nameof(ShowLyricsPanel));
        OnPropertyChanged(nameof(ShowCanvasShade));
        OnPropertyChanged(nameof(ShowVideoShade));
        ArmFullscreenControlsTimer();
        if (_settingVideoModeForTrack) return;
        _ = SwitchVideoModeAsync(value);
    }

    /// <summary>The fullscreen background is light only when a bright Canvas frame is showing.</summary>
    public bool IsFullscreenBackgroundLight => IsCanvasActive && IsCanvasLight;

    /// <summary>LibVLC media player bound to the fullscreen Canvas VideoView.</summary>
    public LibVLCSharp.Shared.MediaPlayer? CanvasPlayer => _video?.Player;

    /// <summary>Diagnostics only (QA).</summary>
    public string CanvasDebugState => _video?.DebugState ?? "none";

    [RelayCommand]
    private void ToggleCanvas()
    {
        // One setting, the same as in Settings; ApplyShowCanvas does the rest.
        var on = !IsCanvasEnabled;
        _appSettings.ShowCanvas = on;
        if (!on)
            StatusMessage = "Animated cover off.";
        else if (CurrentTrack is null)
            StatusMessage = "Play a track first to see the animated cover.";
        else if (string.IsNullOrEmpty(CurrentTrack.VideoVersionId))
            StatusMessage = "No official video for this track.";
    }

    /// <summary>The Song | Video switch asks for a side, not a flip.</summary>
    public void SetVideoMode(bool video)
    {
        if (IsVideoMode != video) ToggleVideoMode();
    }

    [RelayCommand]
    private void ToggleVideoMode()
    {
        if (!HasVideoCounterpart || CurrentTrack?.VideoVersionId is null)
        {
            StatusMessage = "No official video for this track.";
            return;
        }
        IsVideoMode = !IsVideoMode;
        // Remember the choice so the rest of the queue keeps playing as clips.
        // NOTE: intentionally raw (not Arm/Disarm): the toggle follows the mode
        // but must NOT touch the one-shot intent — that belongs to queue builders.
        _videoQueueMode = IsVideoMode;
    }

    /// <summary>
    /// Discovers the official YouTube Music versions (song / music video) for a
    /// track, so the Canvas and the Song/Video switch only ever use official
    /// content — never a search-result guess.
    /// </summary>
    private async Task PrimeTrackVersionsAsync(SongItem track)
    {
        // Only a resolved VIDEO pin skips discovery. A lone trivial
        // SongVersionId (== the track's own id, stamped even when no clip was
        // ever found) must NOT block it — otherwise one failed lookup disables
        // clips for the track forever. Repeat lookups are bounded by the
        // provider's hourly versions cache.
        if (!string.IsNullOrEmpty(track.VideoVersionId))
        {
            Dispatcher.UIThread.Post(() => ApplyVersions(track));
            // Background self-heal for stale cached pins (never blocks
            // playback): a pin failing title/playable/duration verification is
            // dropped from DB and memory so the next play re-discovers.
            _ = VerifyCachedPinAsync(track);
            return;
        }

        // Offline: skip discovery entirely — no wasted requests, and above all
        // no false "no clip" answer (a cached pin above still applies).
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) return;

        // Negative cache: a recent "no clip" answer skips the whole discovery
        // (cheap indexed read; a miss here just means "proceed as before").
        try
        {
            // The clip may already be known from an album/artist row that
            // resolved to this song — reuse it instead of trusting a stale
            // "no clip" stamp (Canvas missing from search/home, present from
            // the album).
            var knownClip = await _musicDatabase.FindKnownClipForSongAsync(track.Id);
            // Played once as a video does not make a clip fit for the background
            // (a lyric video, a fan's upload): it must pass the same check first.
            if (!string.IsNullOrEmpty(knownClip) && !await KnownClipFitsAsync(track, knownClip))
                knownClip = null;
            if (!string.IsNullOrEmpty(knownClip))
            {
                track.SongVersionId ??= track.Id;
                track.VideoVersionId = knownClip;
                Dispatcher.UIThread.Post(() => ApplyVersions(track));
                _ = SaveVersionsAsync(track, clipMissAt: null);
                System.Diagnostics.Trace.WriteLine($"[Versions] {track.Id} known clip {knownClip}");
                return;
            }
            if (await _musicDatabase.HasFreshClipMissAsync(track.Id))
            {
                Dispatcher.UIThread.Post(() => ApplyVersions(track));
                return;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Versions] clip-miss check failed: {ex.Message}");
        }

        try
        {
            var versions = await _musicProvider.GetTrackVersionsAsync(track);
            if (versions is null) return;

            track.SongVersionId = versions.SongId;
            track.VideoVersionId = versions.VideoId;

            Dispatcher.UIThread.Post(() => ApplyVersions(track));

            // Cache the resolved versions so the next launch skips discovery, and
            // record the outcome for the negative cache: a found clip clears any
            // old miss, a genuine "no clip" stamps a fresh one (failures above
            // already returned, so this only stamps real misses).
            _ = SaveVersionsAsync(track, string.IsNullOrWhiteSpace(versions.VideoId) ? DateTime.UtcNow : null);

            System.Diagnostics.Debug.WriteLine(
                $"[Versions] {track.Id} song={track.SongVersionId} video={track.VideoVersionId}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Versions] prime failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether a clip known from an album or artist row may stand behind the song.
    /// Could-not-check counts as yes: the next background check heals it.
    /// </summary>
    private async Task<bool> KnownClipFitsAsync(SongItem track, string clipId)
    {
        if (string.Equals(clipId, track.Id, StringComparison.Ordinal)) return true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var row = await _musicDatabase.GetSongAsync(clipId, cts.Token);
            return await _musicProvider.VerifyKnownClipAsync(track, clipId, row?.VideoType, cts.Token);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Versions] known-clip check failed: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// Stores the versions, then the negative-cache stamp, in that order: the
    /// stamp only updates an existing row. Never blocks playback.
    /// </summary>
    private async Task SaveVersionsAsync(SongItem track, DateTime? clipMissAt)
    {
        await UpsertOnlyAsync(track);
        try { await _musicDatabase.SetClipMissAtAsync(track.Id, clipMissAt); }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Versions] clip-miss stamp failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Background-checks one cached video pin (once per session per pin).
    /// Skipped offline. A positively-mismatched pin is dropped from the DB and
    /// from the in-memory track so the next play re-discovers; transport
    /// failures keep the pin (fail open).
    /// </summary>
    private async Task VerifyCachedPinAsync(SongItem track)
    {
        var videoId = track.VideoVersionId;
        if (string.IsNullOrWhiteSpace(videoId)) return;
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) return;
        lock (_pinVerifyChecked)
        {
            if (!_pinVerifyChecked.Add(track.Id + "|" + videoId)) return;
        }
        bool ok;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            ok = await _musicProvider.VerifyVideoPinAsync(track, videoId, cts.Token);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Versions] cached-pin check failed: {ex.Message}");
            return;
        }
        if (ok) return;
        // A clip known from a video row is checked before it is used (see
        // PrimeTrackVersionsAsync), so it cannot come back and flicker.
        System.Diagnostics.Trace.WriteLine($"[Versions] dropping stale pin {track.Id} -> {videoId}");
        try { await _musicDatabase.ClearVersionPinsAsync(track.Id); }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Versions] pin clear failed: {ex.Message}");
        }
        track.VideoVersionId = null;
        if (ReferenceEquals(track, CurrentTrack))
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(track, CurrentTrack)) return;
                // Recompute (a bare PropertyChanged left the toggle visible).
                HasVideoCounterpart = CanToggleVersions(track);
                if (!IsVideoMode) StopCanvas();
            });
    }

    /// <summary>
    /// Applies freshly discovered versions to the UI: enables the Song/Video
    /// switch (when both versions exist) and (re)loads the Canvas from the
    /// official music video when one exists.
    /// </summary>
    private void ApplyVersions(SongItem track)
    {
        if (!ReferenceEquals(CurrentTrack, track)) return;

        HasVideoCounterpart = CanToggleVersions(track);

        // The user is in Video mode and the next track's clip was only just
        // resolved: enter Video for it too (playlist keeps playing as clips).
        if (_videoQueueMode && !IsVideoMode && HasClip(track) && !ReferenceEquals(track, _videoFailedTrack))
        {
            IsVideoMode = true;
            return;
        }

        if (IsVideoMode) return;   // Video mode is played by PlayVideoAsync

        // Warm the Canvas stream for this clip now (background), so opening the
        // fullscreen player later is instant instead of resolving on the spot.
        if (IsCanvasEnabled && !string.IsNullOrEmpty(track.VideoVersionId))
            _ = PrefetchCanvasStreamAsync(track.VideoVersionId);

        if (!IsCanvasEnabled || !IsFullscreenPlayer) return;
        if (!string.IsNullOrEmpty(track.VideoVersionId)) _ = LoadCanvasAsync(track);
        else StopCanvas();
    }

    /// <summary>
    /// A few seconds into a track, resolve the NEXT track's clip and Canvas
    /// stream in the background, so the Canvas on the track change starts
    /// almost at once instead of waiting for discovery + stream lookup.
    /// </summary>
    private async Task WarmNextCanvasAsync(SongItem current)
    {
        if (!IsCanvasEnabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            if (!ReferenceEquals(CurrentTrack, current) || !IsCanvasEnabled) return;
            var queue = _queueService.Queue;
            var nextIndex = _queueService.CurrentIndex + 1;
            if (nextIndex <= 0 || nextIndex >= queue.Count) return;
            var next = queue[nextIndex];
            if (next.IsLocal) return;
            if (string.IsNullOrEmpty(next.VideoVersionId)) await PrimeTrackVersionsAsync(next);
            if (!string.IsNullOrEmpty(next.VideoVersionId)) await PrefetchCanvasStreamAsync(next.VideoVersionId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Canvas] warm next failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves + caches (provider-side, 90 min) the Canvas stream URL for a
    /// clip so the fullscreen player starts without a stream lookup.
    /// </summary>
    private async Task PrefetchCanvasStreamAsync(string videoId)
    {
        try
        {
            await _musicProvider.GetVideoStreamInfoAsync(videoId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Canvas] prefetch failed: {ex.Message}");
        }
    }

    /// <summary>Pre-creates the libvlc engine in the background so the first video isn't slow.</summary>
    public Task PrepareVideoEngineAsync() => GetOrCreateVideo().PrepareAsync();

    /// <summary>True when the track has any resolved video counterpart.</summary>
    private static bool HasClip(SongItem track) => !string.IsNullOrWhiteSpace(track.VideoVersionId);

    /// <summary>True when an official music video exists that differs from the song, so the Song/Video switch is meaningful.</summary>
    private static bool CanToggleVersions(SongItem track)
    {
        if (string.IsNullOrEmpty(track.VideoVersionId)) return false;

        // Many catalogue rows have no explicit ATV id (the track id IS the song),
        // so compare against the effective song id rather than requiring a
        // separate SongVersionId — that hid the Video button on most tracks.
        var songId = string.IsNullOrEmpty(track.SongVersionId) ? track.Id : track.SongVersionId;
        return !string.Equals(track.VideoVersionId, songId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Loads the <b>Song-mode</b> Canvas: a continuous muted segment of the
    /// official music video. Video mode is played by its own single-player path
    /// (<see cref="PlayVideoAsync"/>), so this does nothing in Video mode.
    /// </summary>
    private async Task LoadCanvasAsync(SongItem track)
    {
        if (IsVideoMode) return;

        var generation = Interlocked.Increment(ref _canvasGeneration);

        // Fullscreen was closed with the engine paused on this exact clip:
        // resume it instantly instead of re-resolving + rebuffering. Requires
        // the paused state (not stopped/ended) so a naturally finished loop
        // still restarts fresh below.
        var resumeVideoId = track.VideoVersionId;
        if (!string.IsNullOrEmpty(resumeVideoId) && _video != null)
        {
            var pausedVideo = _video;
            if (string.Equals(_canvasResumeVideoId, resumeVideoId, StringComparison.Ordinal)
                && _canvasResumeSeconds > 0
                && pausedVideo.HasMedia
                && pausedVideo.IsPaused)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (generation != Volatile.Read(ref _canvasGeneration) || !IsCanvasEnabled || IsVideoMode) return;
                    OnPropertyChanged(nameof(CanvasPlayer));
                    if (IsPlaying) pausedVideo.Resume();
                    IsCanvasActive = true;
                });
                return;
            }
        }

        StopCanvas();

        // The Canvas is the official music video only. If the track has no clip
        // (the "without video" version), there is no Canvas — we never fetch a
        // random video from search results.
        var videoId = track.VideoVersionId;
        if (string.IsNullOrEmpty(videoId))
        {
            Dispatcher.UIThread.Post(() => { IsCanvasActive = false; IsCanvasLoading = false; });
            return;
        }

        // The stream lookup is the slow part: the loader shows from here.
        Dispatcher.UIThread.Post(() =>
        {
            if (generation == Volatile.Read(ref _canvasGeneration)) BeginCanvasLoad();
        });

        // Prepare the libvlc engine concurrently with the stream lookup: engine
        // init and the network round-trip now overlap instead of running one
        // after the other (the biggest first-video cost).
        var canvasVideo = GetOrCreateVideo();
        var prepareTask = canvasVideo.PrepareAsync();

        string? streamUrl = null;
        try
        {
            // Instant when prefetched (the provider caches it, and drops it
            // before the link expires).
            streamUrl = (await _musicProvider.GetVideoStreamInfoAsync(videoId))?.Url;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Canvas] stream lookup failed for {videoId}: {ex.Message}");
        }
        if (generation != Volatile.Read(ref _canvasGeneration) || !IsCanvasEnabled || IsVideoMode) return;

        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsCanvasActive = false;
                IsCanvasLoading = false;
                StatusMessage = "Official video found, but its stream is unavailable.";
            });
            return;
        }
        _canvasVideoId = videoId;

        // Continue from the exit moment when re-opening fullscreen for the
        // same clip; a different track still starts from the stable offset.
        var startSeconds = CanvasStartSeconds;
        if (!string.IsNullOrEmpty(_canvasResumeVideoId)
            && string.Equals(_canvasResumeVideoId, videoId, StringComparison.Ordinal)
            && _canvasResumeSeconds > 0)
        {
            startSeconds = _canvasResumeSeconds;
        }

        // Engine ready before attach+play: setting libvlc callbacks on a
        // playing player deadlocks (hard freeze).
        try
        {
            await prepareTask;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Canvas] video engine failed to start: {ex.Message}");
            await Dispatcher.UIThread.InvokeAsync(() => { IsCanvasActive = false; IsCanvasLoading = false; });
            return;
        }
        if (generation != Volatile.Read(ref _canvasGeneration) || !IsCanvasEnabled || IsVideoMode) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(CanvasPlayer));
            BeginCanvasLoad();
            IsCanvasActive = true;
            if (!canvasVideo.PlayFrom(streamUrl, startSeconds: startSeconds))
                IsCanvasActive = false;
        });
    }

    /// <summary>
    /// Video mode: one player (VLC) plays the whole clip <b>with its sound</b>;
    /// NAudio is stopped and the transport drives VLC instead.
    /// </summary>
    private async Task PlayVideoAsync(SongItem track, int generation)
    {
        await _audioPlayer.StopAsync();

        var videoId = track.VideoVersionId!;

        // Overlap the (one-off) libvlc engine init with the stream lookup so the
        // first clip doesn't pay for both sequentially.
        var videoPlayer = GetOrCreateVideo();
        var prepareTask = videoPlayer.PrepareAsync();

        var prepared = Interlocked.Exchange(ref _preparedVideoStreams, null);
        StreamInfo? videoInfo = null;
        StreamInfo? audioInfo = null;
        try
        {
            if (prepared != null && string.Equals(prepared.VideoId, videoId, StringComparison.Ordinal))
            {
                videoInfo = prepared.Video;
                audioInfo = prepared.Audio;
                await prepareTask;
            }
            else
            {
                Dispatcher.UIThread.Post(() => StatusMessage = "Loading video…");
                Dispatcher.UIThread.Post(() => IsTrackLoading = true);
                // Regular Song/Video mode switches still resolve their streams here.
                var videoTask = _musicProvider.GetVideoStreamInfoAsync(videoId, highQuality: true, targetHeight: _appSettings.VideoQuality);
                var audioTask = _musicProvider.GetStreamInfoAsync(videoId, null, _appSettings.AudioQuality);
                await Task.WhenAll(videoTask, audioTask, prepareTask);
                videoInfo = await videoTask;
                audioInfo = await audioTask;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Video] could not prepare {videoId}: {ex.Message}");
        }

        if (generation != Volatile.Read(ref _playGeneration)) return;

        // DASH keeps picture and sound apart, so a clip needs both.
        if (string.IsNullOrWhiteSpace(videoInfo?.Url) || string.IsNullOrWhiteSpace(audioInfo?.Url))
        {
            await FallBackToSongAsync(track, generation);
            return;
        }

        var start = (int)TakeResumePosition(track).TotalSeconds;
        var videoUrl = videoInfo.Url;
        var audioUrl = audioInfo.Url;

        var started = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(CanvasPlayer));
            BeginCanvasLoad();
            var ok = videoPlayer.PlayFrom(videoUrl, start, withAudio: true, audioUrl: audioUrl);
            if (ok)
            {
                videoPlayer.SetVolume(Volume);
                videoPlayer.SetMuted(IsMuted);
            }
            return ok;
        });

        if (generation != Volatile.Read(ref _playGeneration)) return;
        if (!started)
        {
            await FallBackToSongAsync(track, generation);
            return;
        }
        _consecutivePlayFailures = 0;

        Dispatcher.UIThread.Post(() =>
        {
            IsCanvasActive = true;
            IsPlaying = true;
            Position = TimeSpan.FromSeconds(start);
            Duration = track.DurationSeconds > 0
                ? TimeSpan.FromSeconds(track.DurationSeconds)
                : Duration;
            StatusMessage = string.Empty;
            IsTrackLoading = false;
        });

        RecordHistoryAsync(track);
    }

    /// <summary>The clip could not start, but the track can still play as a song.</summary>
    private async Task FallBackToSongAsync(SongItem track, int generation)
    {
        System.Diagnostics.Trace.WriteLine($"[Video] clip unavailable for {track.Id}, playing the song");
        _videoFailedTrack = track;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ShowToast("Video unavailable, playing the song.", isError: true);
            _settingVideoModeForTrack = true;
            IsVideoMode = false;
            _settingVideoModeForTrack = false;
            StopCanvas();
        });
        if (generation != Volatile.Read(ref _playGeneration)) return;
        await PlayCurrentTrackAsync(generation);
    }

    /// <summary>The track whose clip just failed (does not re-enter Video mode for it).</summary>
    private SongItem? _videoFailedTrack;

    private VideoPlaybackService GetOrCreateVideo()
    {
        if (_video != null) return _video;

        _video = new VideoPlaybackService();
        _video.EndReached += () => Dispatcher.UIThread.Post(() =>
        {
            // Video mode: the clip IS the media — advance or recommend on end.
            if (IsVideoMode)
            {
                FlushPlaySession();
                AdvanceOrRecommend();
                return;
            }

            // Song mode: loop the muted snippet seamlessly. Replaying the
            // loaded media (its :start-time applies again) avoids the old full
            // reload — stop, re-resolve, fade out/in — that blinked every loop.
            if (IsCanvasEnabled && IsFullscreenPlayer && CurrentTrack is { } track)
            {
                if (_video is { HasMedia: true } loop) loop.Seek(TimeSpan.FromSeconds(CanvasStartSeconds));
                else _ = LoadCanvasAsync(track);
            }
        });
        _video.Tick += () => Dispatcher.UIThread.Post(UpdateFromVideoTick);
        return _video;
    }

    /// <summary>Mirrors the Video-mode player's position/duration/state into the UI.</summary>
    private void UpdateFromVideoTick()
    {
        if (!IsVideoMode || _video is null) return;

        // Same scrub rule as audio: the finger owns the highlight while seeking.
        if (IsSeeking) return;
        var pos = _video.Position;
        // VLC seeks asynchronously: ticks still reporting the pre-seek time
        // must not drag the slider/highlight back.
        if (IsStaleSeekEcho(pos)) return;
        var lyricIndex = LyricIndexFor(pos);
        Position = pos;
        if (lyricIndex != CurrentLyricIndex) CurrentLyricIndex = lyricIndex;

        var duration = _video.Duration;
        if (duration > TimeSpan.Zero) Duration = duration;
        IsPlaying = _video.IsPlaying;
    }

    private void StopCanvas()
    {
        _video?.Stop();
        IsCanvasActive = false;
        IsCanvasLoading = false;
    }

    /// <summary>Auto-load the Canvas when the fullscreen player opens; stop on close.</summary>
    partial void OnIsFullscreenPlayerChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowAmbientBackground));
        OnPropertyChanged(nameof(IsShellFlowing));
        OnPropertyChanged(nameof(IsFullscreenFlowing));
        if (value)
        {
            _ = EnsureCanvasAsync();
            RefreshFullscreenDetails();
            ArmFullscreenControlsTimer();
            return;
        }

        // Hiding the fullscreen overlay must not switch an active video to Song
        // mode. VLC keeps the clip/audio alive; reopening the overlay resumes the
        // same Video mode and position.
        if (!IsVideoMode)
        {
            // Freeze the Song-mode Canvas instead of stopping it: the media
            // stays loaded with ~zero decode cost, so re-opening fullscreen
            // resumes the exact frame instantly instead of re-resolving +
            // rebuffering from scratch.
            if (IsCanvasActive && _video != null && !string.IsNullOrEmpty(_canvasVideoId))
            {
                var canvasPos = (int)_video.Position.TotalSeconds;
                if (canvasPos > 0)
                {
                    _canvasResumeVideoId = _canvasVideoId;
                    _canvasResumeSeconds = canvasPos;
                }
                _video.Pause();
            }
            else
            {
                StopCanvas();
            }
            IsCanvasActive = false;
        }
        ArmFullscreenControlsTimer();
    }

    private Task EnsureCanvasAsync()
    {
        if (!IsCanvasEnabled) return Task.CompletedTask;
        if (IsVideoMode) return Task.CompletedTask;   // the video player is already the media
        return CurrentTrack is { } track ? LoadCanvasAsync(track) : Task.CompletedTask;
    }

    /// <summary>Stops both players and releases their resources (called on window close).</summary>
    public void ShutdownPlayers()
    {
        _video?.Dispose();
        _video = null;
        _audioPlayer.Dispose();
    }

    /// <summary>Opens an artist page by id when known, else falls back to a search.</summary>
    public void OpenArtist(string? artistId, string? artistName)
    {
        if (!string.IsNullOrWhiteSpace(artistId))
            _ = OpenBrowseAsync(artistId!, artistName ?? "Artist", null, null);
        else if (!string.IsNullOrWhiteSpace(artistName))
            SearchForCommand.Execute(artistName);
    }

    /// <summary>
    /// Serious recommendation: builds a queue from YouTube Music's own radio
    /// ("start radio") for the seed track — the same source as the up-next
    /// queue — rather than a fabricated similarity score.
    /// </summary>
    public async Task PlayRadioAsync(SongItem seed)
    {
        var queueGeneration = Volatile.Read(ref _queueReplaceGeneration);
        StatusMessage = $"Building radio for “{seed.Title}”…";
        try
        {
            var tracks = await _musicProvider.GetRadioAsync(seed.Id);
            if (queueGeneration != Volatile.Read(ref _queueReplaceGeneration))
            {
                StatusMessage = string.Empty;
                return;
            }
            if (tracks.Count == 0)
            {
                StatusMessage = "Couldn't build a radio for this track.";
                return;
            }

            var list = new List<SongItem> { seed };
            list.AddRange(tracks.Where(t => !string.Equals(t.Id, seed.Id, StringComparison.Ordinal)));
            _queueService.SetQueue(list, 0);
            StatusMessage = $"Radio · {list.Count} tracks.";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Radio] {ex.Message}");
            StatusMessage = "Couldn't build a radio for this track.";
        }
    }

    /// <summary>A track ended by itself: Repeat One replays it, otherwise move on.</summary>
    private void AdvanceOrRecommend()
    {
        if (_queueService.RepeatMode == RepeatMode.One)
        {
            _queueService.ReplayCurrent();
            return;
        }
        SkipForward();
    }

    /// <summary>
    /// The next track, and at the end of the queue keeps the music going with a
    /// YouTube Music radio for the last one (autoplay). Playing a single track or
    /// a video therefore flows into recommendations automatically.
    /// </summary>
    private void SkipForward()
    {
        var hasNext = _queueService.CurrentIndex + 1 < _queueService.Queue.Count;
        if (hasNext || _queueService.RepeatMode != RepeatMode.Off)
        {
            _queueService.Next();
            return;
        }

        if (!_appSettings.Autoplay) return;
        if (_queueService.CurrentTrack is { } seed) _ = ContinueWithRecommendationsAsync(seed);
    }

    private async Task ContinueWithRecommendationsAsync(SongItem seed)
    {
        var queueGeneration = Volatile.Read(ref _queueReplaceGeneration);
        try
        {
            StatusMessage = "Finding something similar…";
            var tracks = await _musicProvider.GetRadioAsync(seed.Id);

            // The user started something else while this loaded: that wins.
            if (queueGeneration != Volatile.Read(ref _queueReplaceGeneration)
                || !ReferenceEquals(_queueService.CurrentTrack, seed))
            {
                StatusMessage = string.Empty;
                return;
            }

            // Added to the queue rather than replacing it, so Previous still goes
            // back through what was played. Tracks already queued are skipped so
            // a long session does not circle around the same songs.
            var known = _queueService.Queue.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            var list = tracks.Where(t => known.Add(t.Id)).ToList();
            if (list.Count == 0)
                list = tracks.Where(t => !string.Equals(t.Id, seed.Id, StringComparison.Ordinal)).ToList();
            if (list.Count == 0)
            {
                StatusMessage = string.Empty;
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _queueService.Enqueue(list);
                _queueService.Next();
                StatusMessage = string.Empty;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Autoplay] recommendations failed: {ex.Message}");
            StatusMessage = string.Empty;
        }
    }

    // ---- User playlists (create / add / view / delete) ----

    /// <summary>
    /// Re-imports the whole YouTube Music account library on demand (likes,
    /// albums, subscribed artists and playlists). Used by the Settings → Account
    /// "Re-import library" button; the work itself is the Library reload, so both
    /// entries always show the same data.
    /// </summary>
    public async Task ReimportAccountAsync()
    {
        SettingsViewModel.AccountStatus = "Importing your library…";
        try
        {
            await LibraryViewModel.ReimportAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Account] Re-import failed: {ex.Message}");
            SettingsViewModel.AccountStatus = "Import failed. Check your connection and try again.";
            return;
        }
        SettingsViewModel.AccountStatus = string.Empty;
    }

    /// <summary>Editable (user-created) playlists, kept in sync for the track menu.</summary>
    public ObservableCollection<PlaylistEntity> Playlists { get; } = new();

    /// <summary>Re-reads the playlists for the track menu and the Library tiles.</summary>
    public async Task RefreshPlaylistsAsync()
    {
        await ReloadMenuPlaylistsAsync();
        await LibraryViewModel.RefreshPlaylistTilesAsync();
    }

    private async Task ReloadMenuPlaylistsAsync()
    {
        try
        {
            var all = await _musicDatabase.GetLibraryPlaylistsAsync();
            var scope = _account.AccountScopeKey;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Playlists.Clear();
                foreach (var playlist in all.Where(p => p.IsEditable && PlaylistEntity.InScope(p, scope))) Playlists.Add(playlist);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Playlists] Refresh failed: {ex.Message}");
        }
    }

    public async Task<PlaylistEntity?> CreatePlaylistAsync(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        try
        {
            var playlist = await _musicDatabase.CreatePlaylistAsync(title, _account.AccountScopeKey);
            await RefreshPlaylistsAsync();
            StatusMessage = $"Created playlist “{playlist.Title}”.";
            return playlist;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Playlists] Create failed: {ex.Message}");
            StatusMessage = "Could not create the playlist.";
            return null;
        }
    }

    public async Task AddToPlaylistAsync(PlaylistEntity playlist, SongItem song)
    {
        try
        {
            // Local-only: adding never writes to the YouTube account.
            // Guard against the same track under a different version id (ATV/OMV):
            // a real duplicate would show a song twice and make deletes confusing.
            var existing = await _musicDatabase.GetPlaylistSongsAsync(playlist.Id);
            if (existing.Any(e => SongIdentity.SameTrack(song, e)))
            {
                StatusMessage = $"“{song.Title}” is already in {playlist.Title}.";
                return;
            }

            await _musicDatabase.AddSongToPlaylistAsync(playlist.Id, song.ToEntity());
            StatusMessage = $"Added “{song.Title}” to {playlist.Title}.";
            ShowToast($"Added to {playlist.Title}");
            await LibraryViewModel.RefreshPlaylistTilesAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Playlists] Add failed: {ex.Message}");
            StatusMessage = "Could not add the track to the playlist.";
        }
    }

    public async Task CreatePlaylistWithSongAsync(string title, SongItem song)
    {
        var playlist = await CreatePlaylistAsync(title);
        if (playlist != null) await AddToPlaylistAsync(playlist, song);
    }

    public Task DeletePlaylistAsync(PlaylistEntity playlist) => DeletePlaylistCoreAsync(playlist);

    private async Task DeletePlaylistCoreAsync(PlaylistEntity playlist)
    {
        try
        {
            await _musicDatabase.DeletePlaylistAsync(playlist.Id);
            await RefreshPlaylistsAsync();
            StatusMessage = $"Deleted playlist “{playlist.Title}”.";

            // If that playlist is open, leave the page.
            if (CurrentView is CollectionViewModel collection &&
                string.Equals(collection.CollectionId, playlist.Id, StringComparison.Ordinal))
            {
                NavigateToLibraryCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Playlists] Delete failed: {ex.Message}");
            StatusMessage = "Could not delete the playlist.";
        }
    }

    /// <summary>Plays a single song (or a list) via the queue service.</summary>
    public void PlaySongs(IReadOnlyList<SongItem> songs, SongItem? start)
    {
        if (songs.Count == 0) return;
        // An explicit song/collection choice resets to Song mode; Video mode is
        // re-enabled only by the Song/Video toggle or a video card.
        DisarmVideoQueue();
        var list = songs.ToList();
        var index = start == null ? 0 : list.IndexOf(start);
        // Temporary origin log: catches which tap moves the queue.
        System.Diagnostics.Debug.WriteLine($"[Queue] PlaySongs '{start?.Title ?? list[0].Title}' index={index} count={list.Count}");
        _queueService.SetQueue(list, index < 0 ? 0 : index);
    }

    /// <summary>What a card action does with the collection's tracks.</summary>
    public enum CollectionAction { Play, Shuffle, PlayNext, AddToQueue }

    private int _collectionPlayGeneration;

    /// <summary>
    /// Plays (or queues) an album / playlist / local playlist / artist straight
    /// from its card — the hover ▶ and the card context menu everywhere. The
    /// Home ▶ used to only open the page for anything but a single song.
    /// </summary>
    public async Task PlayCollectionAsync(string id, string? title, string? thumbnail,
        CollectionAction action = CollectionAction.Play)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var generation = Interlocked.Increment(ref _collectionPlayGeneration);
        var replacesQueue = action is CollectionAction.Play or CollectionAction.Shuffle;
        var queueGeneration = Volatile.Read(ref _queueReplaceGeneration);
        if (replacesQueue) StatusMessage = "Loading…";
        try
        {
            var songs = (await LoadCollectionSongsAsync(id)).ToList();
            if (generation != Volatile.Read(ref _collectionPlayGeneration)) return;
            // Another queue was started while this loaded: that choice is newer.
            if (replacesQueue && queueGeneration != Volatile.Read(ref _queueReplaceGeneration)) return;
            if (songs.Count == 0)
            {
                ShowToast("Nothing playable here.", isError: true);
                return;
            }
            foreach (var song in songs)
                if (string.IsNullOrWhiteSpace(song.ThumbnailUrl) && !string.IsNullOrWhiteSpace(thumbnail))
                    song.ThumbnailUrl = thumbnail!;

            var name = string.IsNullOrWhiteSpace(title) ? "collection" : $"“{title}”";
            switch (action)
            {
                case CollectionAction.PlayNext:
                    _queueService.InsertNext(songs);
                    ShowToast($"{songs.Count} songs from {name} play next");
                    break;
                case CollectionAction.AddToQueue:
                    _queueService.Enqueue(songs);
                    ShowToast($"Added {songs.Count} songs from {name} to the queue");
                    break;
                case CollectionAction.Shuffle:
                    var shuffled = songs.OrderBy(_ => Random.Shared.Next()).ToList();
                    PlaySongs(shuffled, shuffled[0]);
                    break;
                default:
                    PlaySongs(songs, songs[0]);
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Collection] card action failed for {id}: {ex.Message}");
            ShowToast("Couldn't load this collection. Check your connection.", isError: true);
        }
        finally
        {
            if (StatusMessage == "Loading…") StatusMessage = string.Empty;
        }
    }

    /// <summary>Tracks behind a card id, with the same routing as the pages.</summary>
    private async Task<IReadOnlyList<SongItem>> LoadCollectionSongsAsync(string id)
    {
        if (id.StartsWith(PlaylistEntity.LocalIdPrefix, StringComparison.Ordinal))
            return (await _musicDatabase.GetPlaylistSongsAsync(id)).Select(s => s.ToSongItem()).ToList();

        if (id.StartsWith("MPREb", StringComparison.Ordinal))
        {
            var tracks = await _musicProvider.GetAlbumTracksAsync(id);
            if (tracks.Count > 0) return tracks;
            var album = await _musicProvider.GetAlbumAsync(id);
            return string.IsNullOrWhiteSpace(album?.PlaylistId)
                ? Array.Empty<SongItem>()
                : await _musicProvider.GetAlbumSongsAsync(album.PlaylistId);
        }

        if (id.StartsWith("UC", StringComparison.Ordinal))
        {
            // Artist: their top songs, continued by the radio of the first one
            // so a card play is a real listening session, not 5 tracks.
            var page = await _musicProvider.GetArtistPageAsync(id);
            var songs = page.Songs.ToList();
            if (songs.Count > 0 && songs.Count < 15)
            {
                var seen = songs.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var extra in await _musicProvider.GetRadioAsync(songs[0].Id))
                    if (seen.Add(extra.Id)) songs.Add(extra);
            }
            return songs;
        }

        return await _musicProvider.GetPlaylistSongsAsync(id);
    }

    /// <summary>Prepares a Home video tile first, then enters fullscreen Video mode.</summary>
    public async Task PlayVideoCardAsync(VideoCard card)
    {
        if (string.IsNullOrWhiteSpace(card.Id)) return;

        var generation = Interlocked.Increment(ref _videoPreparationGeneration);
        var queueGeneration = Volatile.Read(ref _queueReplaceGeneration);
        StatusMessage = "Loading video…";
        IsTrackLoading = true;

        async Task<IReadOnlyList<SongItem>> ResolveMetadataAsync()
        {
            try { return await _musicProvider.GetQueueAsync(new[] { card.Id }); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Video] metadata lookup failed: {ex.Message}");
                return Array.Empty<SongItem>();
            }
        }

        async Task<StreamInfo?> ResolveVideoAsync()
        {
            try { return await _musicProvider.GetVideoStreamInfoAsync(card.Id, highQuality: true, targetHeight: _appSettings.VideoQuality); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Video] video stream lookup failed: {ex.Message}");
                return null;
            }
        }

        async Task<StreamInfo?> ResolveAudioAsync()
        {
            try { return await _musicProvider.GetStreamInfoAsync(card.Id, null, _appSettings.AudioQuality); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Video] audio stream lookup failed: {ex.Message}");
                return null;
            }
        }

        try
        {
            // Resolve metadata and both DASH streams while the user remains on
            // Home. Fullscreen is opened only after a playable video URL exists.
            var metadataTask = ResolveMetadataAsync();
            var videoTask = ResolveVideoAsync();
            var audioTask = ResolveAudioAsync();
            await Task.WhenAll(metadataTask, videoTask, audioTask);

            // A newer card or queue owns the loading state from here on.
            if (generation != Volatile.Read(ref _videoPreparationGeneration)
                || queueGeneration != Volatile.Read(ref _queueReplaceGeneration)) return;

            var videoInfo = await videoTask;
            var audioInfo = await audioTask;
            if (string.IsNullOrWhiteSpace(videoInfo?.Url) || string.IsNullOrWhiteSpace(audioInfo?.Url))
            {
                StatusMessage = "Couldn't load the video and its audio.";
                IsTrackLoading = false;
                return;
            }

            var track = (await metadataTask).FirstOrDefault(s =>
                            string.Equals(s.Id, card.Id, StringComparison.Ordinal) ||
                            string.Equals(s.VideoVersionId, card.Id, StringComparison.Ordinal))
                        ?? new SongItem
                        {
                            Id = card.Id,
                            Title = card.Title,
                            Artists = string.IsNullOrWhiteSpace(card.Subtitle)
                                ? Array.Empty<Artist>()
                                : card.Subtitle.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                                    .Select(name => new Artist { Name = name }).ToArray(),
                            ThumbnailUrl = card.ThumbnailUrl,
                            DurationSeconds = card.DurationSeconds
                        };

            // A video card is played as a CLIP only: pin the song version to the
            // same clip id so there is no separate "song version" to switch to
            // (the Song/Video toggle stays hidden and Song mode would play the
            // very same video anyway).
            track.VideoVersionId = card.Id;
            track.SongVersionId = card.Id;
            track.VideoType = string.IsNullOrWhiteSpace(track.VideoType)
                ? "MUSIC_VIDEO_TYPE_OMV"
                : track.VideoType;
            if (track.DurationSeconds <= 0) track.DurationSeconds = card.DurationSeconds;
            if (string.IsNullOrWhiteSpace(track.ThumbnailUrl)) track.ThumbnailUrl = card.ThumbnailUrl;

            System.Diagnostics.Debug.WriteLine(
                $"[Video] prepared card id={card.Id}, track={track.Id}, stream={videoInfo.Url is not null}, audio={audioInfo?.Url is not null}");
            Interlocked.Exchange(ref _preparedVideoStreams,
                new PreparedVideoStreams(card.Id, videoInfo, audioInfo));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != Volatile.Read(ref _videoPreparationGeneration)
                    || queueGeneration != Volatile.Read(ref _queueReplaceGeneration)) return;
                // Arm Video mode only right before OUR SetQueue: arming earlier
                // leaked the intent (and the one-shot start id) into whatever
                // song queue the user started while the clip was resolving —
                // that song then played as a video.
                ArmVideoQueue();
                Interlocked.Exchange(ref _videoStartTrackId, track.Id);
                _queueService.SetQueue(new[] { track }, 0);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Video] preparation failed: {ex.Message}");
            if (generation == Volatile.Read(ref _videoPreparationGeneration))
            {
                StatusMessage = "Couldn't load this video.";
                IsTrackLoading = false;
            }
        }
    }

    /// <summary>
    /// Plays a list as CLIPS (Video mode): each entry is pinned to its clip id so
    /// it can never resolve to a separate song version. Used by the Library's
    /// liked-videos section.
    /// </summary>
    public void PlayAsVideo(IReadOnlyList<SongItem> tracks, SongItem? start)
    {
        if (tracks.Count == 0) return;

        var list = tracks.ToList();
        foreach (var track in list)
        {
            var clipId = string.IsNullOrWhiteSpace(track.VideoVersionId) ? track.Id : track.VideoVersionId!;
            track.VideoVersionId = clipId;
            // A row that IS the clip has no separate song version. A song row with
            // a known clip keeps its song version: pinned to the clip and stored,
            // it would play the clip's audio in Song mode from then on.
            if (string.Equals(track.Id, clipId, StringComparison.Ordinal))
            {
                track.SongVersionId = clipId;
                if (string.IsNullOrWhiteSpace(track.VideoType)) track.VideoType = "MUSIC_VIDEO_TYPE_OMV";
            }
        }

        ArmVideoQueue();
        var index = start == null ? 0 : list.FindIndex(x => string.Equals(x.Id, start.Id, StringComparison.Ordinal));
        _queueService.SetQueue(list, index < 0 ? 0 : index);
    }

    /// <summary>Inserts a track right after the current one.</summary>
    public void PlayNext(SongItem song) => _queueService.InsertNext(new[] { song });

    /// <summary>Appends a track to the end of the queue.</summary>
    public void Enqueue(SongItem song) => _queueService.Enqueue(new[] { song });

    // ---- Offline downloads ----

    public IDownloadService Downloads => _downloads;

    /// <summary>Queues a catalogue song for offline download.</summary>
    public async Task DownloadTrackAsync(SongItem song)
    {
        if (song is null || song.IsLocal) return;
        ShowToast($"Downloading “{song.Title}”…");
        await _downloads.DownloadAsync(song);
    }

    /// <summary>Deletes a song's offline copy (the catalogue row is kept).</summary>
    public async Task RemoveDownloadAsync(SongItem song)
    {
        if (song is null) return;
        await _downloads.RemoveAsync(song.Id);
        StatusMessage = $"Removed download “{song.Title}”.";
    }

    /// <summary>True when the current track has an offline copy.</summary>
    public bool IsCurrentTrackDownloaded => CurrentTrack is { } t && _downloads.IsDownloaded(t.Id);

    /// <summary>True while the current track's offline copy is being fetched.</summary>
    public bool IsCurrentTrackDownloading => CurrentTrack is { } t && _downloads.IsBusy(t.Id);

    /// <summary>Player-bar button: download the current track, or remove its copy.</summary>
    [RelayCommand]
    private async Task ToggleDownloadCurrentAsync()
    {
        if (CurrentTrack is not { } track || track.IsLocal) return;
        if (_downloads.IsDownloaded(track.Id)) await RemoveDownloadAsync(track);
        else if (!_downloads.IsBusy(track.Id)) await DownloadTrackAsync(track);
    }

    /// <summary>Like/unlike an arbitrary track (used by the track menu).</summary>
    public async Task ToggleLikeAsync(SongItem song)
    {
        try
        {
            await _likeService.ToggleAsync(song);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Library] ToggleLike failed: {ex.Message}");
            ShowToast("Couldn't save the like.", isError: true);
        }
    }

    [RelayCommand]
    private async Task ToggleLikeCurrentTrackAsync()
    {
        var track = CurrentTrack;
        if (track == null) return;
        try
        {
            await _likeService.ToggleAsync(track);
            Dispatcher.UIThread.Post(() =>
            {
                OnPropertyChanged(nameof(IsCurrentTrackLiked));
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Library] Toggle like failed: {ex.Message}");
            ShowToast("Couldn't save the like.", isError: true);
        }
    }

    public bool IsCurrentTrackLiked => CurrentTrack?.IsLiked == true;

    /// <summary>
    /// Pushes a like toggle into every visible row with the same song id
    /// (Search, Library lists, queue, player track, open Artist/Collection
    /// page). <c>SongItem.IsLiked</c> notifies by itself; setting an already
    /// correct value is a no-op.
    /// </summary>
    private void ApplyLikeEverywhere(string songId, bool isLiked)
    {
        static void Sync(IEnumerable<SongItem> songs, string id, bool liked)
        {
            foreach (var s in songs)
                if (string.Equals(s.Id, id, StringComparison.Ordinal))
                    s.IsLiked = liked;
        }

        Sync(SearchViewModel.Songs, songId, isLiked);
        Sync(LibraryViewModel.LikedSongs, songId, isLiked);
        Sync(LibraryViewModel.RecentlyPlayed, songId, isLiked);
        Sync(LibraryViewModel.LocalSongs, songId, isLiked);
        Sync(LibraryViewModel.CollectionTracks, songId, isLiked);
        Sync(QueueItems, songId, isLiked);
        if (CurrentTrack != null && string.Equals(CurrentTrack.Id, songId, StringComparison.Ordinal))
            CurrentTrack.IsLiked = isLiked;
        foreach (var songs in OpenPageSongs())
            Sync(songs, songId, isLiked);
    }

    /// <summary>Song rows of the open page and of the pages waiting behind Back.</summary>
    private IEnumerable<IEnumerable<SongItem>> OpenPageSongs()
    {
        foreach (var page in _navStack.Append(CurrentView))
        {
            switch (page)
            {
                case ArtistViewModel artist: yield return artist.Songs; break;
                case CollectionViewModel collection: yield return collection.Songs; break;
                case VideoFeedViewModel videos: yield return videos.Videos; break;
            }
        }
    }

    /// <summary>Refreshes the "downloaded" badge on every visible row for a song.</summary>
    private void ApplyDownloadEverywhere(string songId, bool isDownloaded)
    {
        static void Sync(IEnumerable<SongItem> songs, string id, bool dl)
        {
            foreach (var s in songs)
                if (string.Equals(s.Id, id, StringComparison.Ordinal))
                    s.IsDownloaded = dl;
        }

        Sync(SearchViewModel.Songs, songId, isDownloaded);
        Sync(LibraryViewModel.LikedSongs, songId, isDownloaded);
        Sync(LibraryViewModel.RecentlyPlayed, songId, isDownloaded);
        Sync(LibraryViewModel.LocalSongs, songId, isDownloaded);
        Sync(LibraryViewModel.DownloadedSongs, songId, isDownloaded);
        Sync(LibraryViewModel.CollectionTracks, songId, isDownloaded);
        Sync(QueueItems, songId, isDownloaded);
        if (CurrentTrack != null && string.Equals(CurrentTrack.Id, songId, StringComparison.Ordinal))
            CurrentTrack.IsDownloaded = isDownloaded;
        foreach (var songs in OpenPageSongs())
            Sync(songs, songId, isDownloaded);
    }

    private void OnCurrentTrackLikedSourceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SongItem.IsLiked))
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(IsCurrentTrackLiked)));
    }

    // ---- Derived, presentation-only properties (no playback logic) ----

    // The sidebar keeps the SECTION highlighted on its detail pages (album,
    // artist, playlist… opened from Search stays under "Search"); before, no
    // item was active on any detail page.
    private ViewModelBase? _activeSection;
    public bool IsHomeActive => ReferenceEquals(_activeSection, HomeViewModel);
    public bool IsSearchActive => ReferenceEquals(_activeSection, SearchViewModel);
    public bool IsLibraryActive => ReferenceEquals(_activeSection, LibraryViewModel);
    public bool IsSettingsActive => ReferenceEquals(_activeSection, SettingsViewModel);

    public double ProgressFraction => Duration.TotalSeconds > 0
        ? Math.Clamp(Position.TotalSeconds / Duration.TotalSeconds, 0, 1)
        : 0;

    public string PositionText => FormatTime(Position);
    public string DurationText => FormatTime(Duration);

    /// <summary>Left time label: shows the drag preview while seeking, real position otherwise.</summary>
    public string ElapsedText => IsSeeking && Duration.TotalSeconds > 0
        ? FormatTime(TimeSpan.FromSeconds(Duration.TotalSeconds * SeekFraction))
        : FormatTime(Position);

    public bool HasTrack => CurrentTrack != null;

    public string TrackTitle => CurrentTrack?.Title ?? "Nothing playing";

    public string TrackArtist => CurrentTrack?.ArtistDisplay is { Length: > 0 } a
        ? a
        : "Pick a track to begin";

    /// <summary>
    /// Album line under the fullscreen card — hidden when it only repeats the
    /// title or is not an album at all (clip rows carry "209M views" there).
    /// </summary>
    public string? CurrentAlbumTitle
    {
        get
        {
            var album = CurrentTrack?.AlbumTitle?.Trim();
            if (string.IsNullOrEmpty(album)) return null;
            if (string.Equals(album, CurrentTrack?.Title?.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
            if (System.Text.RegularExpressions.Regex.IsMatch(album,
                    @"^[\d.,]+\s*[KMB]?\s*(views|plays|переглядів)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return null;
            return album;
        }
    }

    /// <summary>Title of the next queued track (for the fullscreen "Up next" line).</summary>
    public string? NextTrackTitle
    {
        get
        {
            var index = QueueCurrentIndex;
            return index >= 0 && index + 1 < QueueItems.Count ? QueueItems[index + 1].Title : null;
        }
    }

    /// <summary>"Next · Title — Artist" for the mini player, null at the queue end.</summary>
    public string? NextTrackDisplay
    {
        get
        {
            var index = QueueCurrentIndex;
            if (index < 0 || index + 1 >= QueueItems.Count) return null;
            var next = QueueItems[index + 1];
            var artist = next.ArtistDisplay;
            return string.IsNullOrWhiteSpace(artist)
                ? $"Next · {next.Title}"
                : $"Next · {next.Title} — {artist}";
        }
    }

    // ---- Under the song in the full player: what is like it, who made it, what else ----
    // Read-only section below the hero card. Loads lazily per track. Empty => collapsed.
    [ObservableProperty] private string? _fullscreenArtistName;
    [ObservableProperty] private string? _fullscreenArtistId;
    [ObservableProperty] private string? _fullscreenArtistImageUrl;
    [ObservableProperty] private string? _fullscreenArtistDescription;
    [ObservableProperty] private string? _fullscreenArtistPlays;
    [ObservableProperty] private bool _hasFullscreenArtist;
    [ObservableProperty] private bool _hasFullscreenLead;
    [ObservableProperty] private bool _hasFullscreenShelves;
    [ObservableProperty] private bool _hasFullscreenUpNext;

    /// <summary>Is there anything to scroll down to? The hint in the hero says so.</summary>
    public bool HasFullscreenDetails => HasFullscreenArtist || HasFullscreenLead || HasFullscreenShelves || HasFullscreenUpNext;

    partial void OnHasFullscreenArtistChanged(bool value) => OnPropertyChanged(nameof(HasFullscreenDetails));
    partial void OnHasFullscreenLeadChanged(bool value) => OnPropertyChanged(nameof(HasFullscreenDetails));
    partial void OnHasFullscreenShelvesChanged(bool value) => OnPropertyChanged(nameof(HasFullscreenDetails));
    partial void OnHasFullscreenUpNextChanged(bool value) => OnPropertyChanged(nameof(HasFullscreenDetails));

    /// <summary>The first thing under the song: what sounds like it.</summary>
    public ObservableCollection<ShelfVm> FullscreenLead { get; } = new();

    /// <summary>Further down: similar artists, more by the artist, playlists.</summary>
    public ObservableCollection<ShelfVm> FullscreenShelves { get; } = new();

    public ObservableCollection<SongItem> FullscreenUpNext { get; } = new();

    private int _fullscreenDetailsGeneration;
    private SongItem? _fullscreenDetailsTrack;

    /// <summary>
    /// Loads the details of the current track once its fullscreen player is open.
    /// They take several requests and sit below the fold there, so tracks that
    /// pass by while fullscreen is closed cost nothing.
    /// </summary>
    private void RefreshFullscreenDetails()
    {
        var track = CurrentTrack;
        if (!IsFullscreenPlayer || ReferenceEquals(_fullscreenDetailsTrack, track)) return;
        _fullscreenDetailsTrack = track;
        var generation = Interlocked.Increment(ref _fullscreenDetailsGeneration);
        HasFullscreenArtist = false;
        HasFullscreenLead = false;
        HasFullscreenShelves = false;
        FullscreenLead.Clear();
        FullscreenShelves.Clear();
        _ = LoadFullscreenDetailsAsync(track, generation);
    }

    private void SyncFullscreenUpNext()
    {
        try
        {
            var q = _queueService.Queue;
            var idx = _queueService.CurrentIndex;
            FullscreenUpNext.Clear();
            if (q.Count > 0 && idx >= 0)
            {
                for (var i = idx + 1; i < q.Count && FullscreenUpNext.Count < 5; i++)
                    FullscreenUpNext.Add(q[i]);
            }
            HasFullscreenUpNext = FullscreenUpNext.Count > 0;
        }
        catch { HasFullscreenUpNext = false; }
    }

    private async Task LoadFullscreenDetailsAsync(SongItem? track, int generation)
    {
        if (track == null || track.IsLocal) return;

        var trackId = track.Id;
        var artistName = track.Artists.Count > 0 ? track.Artists[0].Name : string.Empty;
        var artistId = track.Artists.Count > 0 ? track.Artists[0].Id : null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Resolve artist id when the row carries only a name (search rows often do).
        if (string.IsNullOrWhiteSpace(artistId) && !string.IsNullOrWhiteSpace(artistName))
        {
            try
            {
                var res = await _musicProvider.SearchAsync(artistName, SearchFilter.Artists, cts.Token);
                // The same name or nothing: the top result of a name search can be
                // somebody else, and a wrong "About the artist" is worse than none.
                var hit = res.Artists.FirstOrDefault(a =>
                        string.Equals(a.Name?.Trim(), artistName.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? res.Artists.Take(3).FirstOrDefault(a => ArtistNames.SoundTheSame(a.Name, artistName));
                artistId = hit?.Id;
            }
            catch { artistId = null; }
        }

        var pageTask = !string.IsNullOrWhiteSpace(artistId)
            ? _musicProvider.GetArtistPreviewAsync(artistId, cts.Token)
            : Task.FromResult<ArtistPage>(null!);
        var relatedTask = _musicProvider.GetSongRelatedAsync(trackId, cts.Token);

        ArtistPage? page = null;
        SongRelated? related = null;
        try { page = await pageTask; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Details] artist: {ex.Message}"); }
        try { related = await relatedTask; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Details] related: {ex.Message}"); }

        var shelves = (related?.Shelves ?? Array.Empty<Shelf>())
            .Select(s => TidyRelated(s, track))
            .Where(s => s.Items.Count > 0)
            .ToList();
        var lead = shelves.FirstOrDefault(s => s.Layout == ShelfLayout.Rows);

        // Without YouTube's related page the radio still knows what sounds like it.
        if (lead == null)
        {
            try
            {
                var radio = await _musicProvider.GetRelatedTracksAsync(trackId, cts.Token);
                var items = radio.Where(r => !string.Equals(r.Id, trackId, StringComparison.Ordinal)).Take(16)
                    .Select(r => new ShelfItem { Kind = ShelfItemKind.Song, Id = r.Id, Title = r.Title, Subtitle = r.ArtistDisplay, ThumbnailUrl = r.ThumbnailUrl, Song = r })
                    .ToList();
                if (items.Count > 0)
                    lead = new Shelf { Title = "You might also like", Layout = ShelfLayout.Rows, Items = items };
            }
            catch { /* the rest of the details still show */ }
        }

        var name = page?.Artist.Name is { Length: > 0 } known ? known : artistName;
        var image = page?.Artist.ThumbnailUrl;
        var story = page?.Description ?? related?.About;
        var stats = string.Join(" · ", new[] { page?.MonthlyAudienceText, page?.SubscribersText, page?.ViewsText }
            .Where(t => !string.IsNullOrWhiteSpace(t)));

        Dispatcher.UIThread.Post(() =>
        {
            if (generation != Volatile.Read(ref _fullscreenDetailsGeneration)) return;

            if (!string.IsNullOrWhiteSpace(name) && (!string.IsNullOrWhiteSpace(story) || !string.IsNullOrWhiteSpace(image)))
            {
                FullscreenArtistName = name;
                FullscreenArtistId = page?.Artist.Id ?? artistId;
                FullscreenArtistImageUrl = image;
                FullscreenArtistDescription = story;
                FullscreenArtistPlays = stats;
                HasFullscreenArtist = true;
            }
            else
            {
                HasFullscreenArtist = false;
            }

            FullscreenLead.Clear();
            if (lead != null) FullscreenLead.Add(ShelfVm.From(lead));
            HasFullscreenLead = FullscreenLead.Count > 0;

            FullscreenShelves.Clear();
            foreach (var shelf in shelves.Where(s => !ReferenceEquals(s, lead)))
                FullscreenShelves.Add(ShelfVm.From(shelf));
            HasFullscreenShelves = FullscreenShelves.Count > 0;
        });
    }

    /// <summary>
    /// Without the song itself (it also comes back from a compilation), and "Imagine Dragons"
    /// reads better as "More by Imagine Dragons".
    /// </summary>
    private static Shelf TidyRelated(Shelf shelf, SongItem track)
    {
        bool IsTheSong(ShelfItem item) =>
            string.Equals(item.Id, track.Id, StringComparison.Ordinal)
            || (item.Song != null
                && string.Equals(item.Title, track.Title, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Song.ArtistDisplay, track.ArtistDisplay, StringComparison.OrdinalIgnoreCase));

        var known = new[] { "you might also like", "recommended playlists", "other performances", "similar artists" };
        return new Shelf
        {
            Title = known.Contains(shelf.Title.ToLowerInvariant()) ? shelf.Title : $"More by {shelf.Title}",
            Layout = shelf.Layout,
            Items = shelf.Items.Where(i => !IsTheSong(i)).ToList(),
            MoreBrowseId = shelf.MoreBrowseId,
            MoreParams = shelf.MoreParams
        };
    }

    [RelayCommand]
    private async Task OpenFullscreenArtist()
    {
        if (string.IsNullOrWhiteSpace(FullscreenArtistId)) return;
        var id = FullscreenArtistId;
        var name = FullscreenArtistName ?? "Artist";
        IsFullscreenPlayer = false;
        await OpenBrowseAsync(id, name, FullscreenArtistImageUrl, null);
    }

    [RelayCommand]
    private void PlayFullscreenUpNext(SongItem? song)
    {
        if (song == null) return;
        var idx = _queueService.Queue.ToList().FindIndex(t => ReferenceEquals(t, song));
        if (idx < 0) idx = _queueService.Queue.ToList().FindIndex(t => string.Equals(t.Id, song.Id, StringComparison.Ordinal));
        if (idx >= 0) _queueService.SetCurrentIndex(idx);
    }

    private static string FormatTime(TimeSpan t)
        => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:D2}";

    partial void OnPositionChanged(TimeSpan value)
    {
#if WINDOWS
        UpdateTaskbarProgress();
#endif
        OnPropertyChanged(nameof(ProgressFraction));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(ElapsedText));
        if (!IsSeeking) SeekFraction = ProgressFraction;
        UpdateCurrentLyricIndex(value);
    }

    partial void OnDurationChanged(TimeSpan value)
    {
        OnPropertyChanged(nameof(ProgressFraction));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(ElapsedText));
        if (!IsSeeking) SeekFraction = ProgressFraction;
    }

    partial void OnSeekFractionChanged(double value)
    {
        OnPropertyChanged(nameof(ElapsedText));
        // Scrub preview (Velune pattern): while the finger drags, the lyrics
        // follow the preview time under the thumb — not the still-playing
        // audio behind it — so the highlight feels locked to the gesture.
        if (IsSeeking) UpdateCurrentLyricIndex(SeekPreviewPosition());
    }

    partial void OnIsSeekingChanged(bool value)
    {
        OnPropertyChanged(nameof(ElapsedText));
        // Snap to the preview as soon as the drag starts (before the first
        // thumb move) so there is no one-frame lag on the old line.
        if (value) UpdateCurrentLyricIndex(SeekPreviewPosition());
    }

    /// <summary>Playback time under the seek thumb (meaningful while seeking).</summary>
    private TimeSpan SeekPreviewPosition() => Duration.TotalSeconds > 0
        ? TimeSpan.FromSeconds(Duration.TotalSeconds * Math.Clamp(SeekFraction, 0, 1))
        : Position;

    partial void OnCurrentViewChanged(ViewModelBase value)
    {
        if (ReferenceEquals(value, HomeViewModel) || ReferenceEquals(value, SearchViewModel)
            || ReferenceEquals(value, LibraryViewModel) || ReferenceEquals(value, SettingsViewModel))
            _activeSection = value;
        NotifyNavigation();
        OnPropertyChanged(nameof(IsHomeActive));
        OnPropertyChanged(nameof(IsSearchActive));
        OnPropertyChanged(nameof(IsLibraryActive));
        OnPropertyChanged(nameof(IsSettingsActive));
    }

    // ---- Transport actions (use existing IAudioPlayer API; no engine changes) ----

    /// <summary>
    /// A restored session that has not started yet: nothing is loaded, so a
    /// seek only moves the point playback will start from.
    /// </summary>
    private bool TryMovePendingResume(TimeSpan target)
    {
        if (IsVideoMode || CurrentTrack is not { } track || _resumeTrackId != track.Id) return false;
        if (_audioPlayer.State is not (MediaPlayerState.Idle or MediaPlayerState.Stopped)) return false;
        _resumePosition = target;
        Position = target;
        return true;
    }

    /// <summary>Applies the dragged slider position to the player.</summary>
    [RelayCommand]
    private async Task CommitSeekAsync()
    {
        if (Duration.TotalSeconds <= 0) return;
        var target = TimeSpan.FromSeconds(Duration.TotalSeconds * Math.Clamp(SeekFraction, 0, 1));
        if (TryMovePendingResume(target)) return;
        var posBefore = Position;
        var indexBefore = CurrentLyricIndex;
        try
        {
            ArmSeekFence(target);
            // Before the wait: a seek into a part still downloading takes a moment.
            Position = target;
            if (IsVideoMode) _video?.Seek(target);
            else await _audioPlayer.SeekAsync(target);
            LogSeekDiagnostic("slider", target, posBefore, indexBefore);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Seek] Failed: {ex.Message}");
        }
    }

    /// <summary>Relative seek for keyboard shortcuts (e.g. ±5s).</summary>
    public async Task SeekByAsync(double seconds)
    {
        if (Duration.TotalSeconds <= 0) return;
        var target = Position + TimeSpan.FromSeconds(seconds);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (target > Duration) target = Duration;
        if (TryMovePendingResume(target)) return;
        var posBefore = Position;
        var indexBefore = CurrentLyricIndex;
        try
        {
            ArmSeekFence(target);
            Position = target;
            if (IsVideoMode) _video?.Seek(target);
            else await _audioPlayer.SeekAsync(target);
            LogSeekDiagnostic("keys", target, posBefore, indexBefore);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Seek] Failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ToggleMute()
    {
        IsMuted = !IsMuted;
    }

    [RelayCommand]
    private void ToggleFullscreenPlayer() => IsFullscreenPlayer = !IsFullscreenPlayer;

    [RelayCommand]
    private void CloseFullscreenPlayer() => IsFullscreenPlayer = false;

    [RelayCommand]
    private void ToggleLyrics() => ShowLyrics = !ShowLyrics;

    [RelayCommand]
    private void OpenLyrics()
    {
        IsFullscreenPlayer = true;
        ShowLyrics = true;
    }

    /// <summary>
    /// Loads lyrics for a track: DB cache first, then <see cref="ILyricsProvider"/>
    /// (LRCLIB). Generation-guarded like the artwork loader.
    /// </summary>
    // Recording ids whose cached (duration-matched) lyrics were already
    // re-checked against exact YouTube Music timing this session.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _exactLyricsChecked = new();

    private async Task LoadLyricsAsync(SongItem? track, TimeSpan? decodedDuration = null, string? playbackId = null)
    {
        var generation = Interlocked.Increment(ref _lyricsGeneration);
        var playGeneration = Volatile.Read(ref _playGeneration);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != Volatile.Read(ref _lyricsGeneration)) return;
            Lyrics.Clear();
            HasLyrics = false;
            IsLoadingLyrics = false;
            CurrentLyricIndex = -1;
            Volatile.Write(ref _syncedLyrics, null);
        });

        if (track == null) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (generation == Volatile.Read(ref _lyricsGeneration)) IsLoadingLyrics = true;
        });
        try
        {
            var durationSeconds = LyricsPlaybackPolicy.DurationSeconds(track.DurationSeconds, decodedDuration ?? TimeSpan.Zero);
            var recordingId = playbackId ?? track.Id;
            // Lyrics belong to the recording that actually plays: an artist-page
            // row can be a clip/lyric video that plays the song id, and the
            // song's cached lyrics live under the song id.
            var lyricsId = recordingId;
            var knownRecording = decodedDuration is { TotalSeconds: >= 1 and <= int.MaxValue };
            var cacheKey = knownRecording
                ? LyricsPlaybackPolicy.CacheKey(lyricsId, recordingId, durationSeconds)
                : lyricsId;
            string? text = await _musicDatabase.GetLyricsAsync(cacheKey);
            var legacyFallback = knownRecording && string.IsNullOrWhiteSpace(text);
            if (legacyFallback) text = await _musicDatabase.GetLyricsAsync(lyricsId);
            var parsed = SyncedLyricsParser.Parse(text);
            // Reuse legacy timing only for the same ID with matching duration.
            // A different version or stale catalogue length needs new timing;
            // its cached words remain available if the lookup is offline.
            if (legacyFallback && parsed.SyncType != LyricsSyncType.None
                // Exact per-video timing is valid for that id at any length.
                && !(LyricsPlaybackPolicy.IsExactSource(text) && lyricsId == recordingId)
                && !LyricsPlaybackPolicy.CanUseLegacyTiming(lyricsId, recordingId, track.DurationSeconds, decodedDuration!.Value))
            {
                text = string.Join(Environment.NewLine, parsed.Lines.Select(line => line.Text));
                parsed = SyncedLyricsParser.Parse(text);
            }
            System.Diagnostics.Trace.WriteLine(
                $"[Lyrics] Load id={track.Id} title=\"{track.Title}\" artist=\"{track.ArtistDisplay}\" " +
                $"catalogueSeconds={track.DurationSeconds} durationSeconds={durationSeconds} playbackId={recordingId} cacheKey={cacheKey} playbackReady={decodedDuration.HasValue} legacyFallback={legacyFallback} cachedChars={text?.Length ?? 0} " +
                $"sync={parsed.SyncType} rows={parsed.Lines.Count}");
            // Track selection can precede opening the decoder. Preload cached
            // words then; resolve timestamps only after this playback is ready.
            // Missing words are fetched right away, even before the decoder is
            // ready (a restored, paused session used to show "No lyrics" until
            // Play) — but only for the id that will actually play, never a
            // clip row that is about to be swapped for its song.
            var missing = string.IsNullOrWhiteSpace(text) || parsed.SyncType == LyricsSyncType.None;
            var canResolveNow = decodedDuration.HasValue
                || (!IsSelfPinnedClip(track)
                    && string.Equals(recordingId, GetPlaybackVideoId(track), StringComparison.Ordinal));
            if (!missing && decodedDuration.HasValue
                && !LyricsPlaybackPolicy.IsExactSource(text)
                && _exactLyricsChecked.TryAdd(recordingId, 0))
            {
                // Cached timing came from a duration match (LRCLIB) and can
                // drift for the whole song: prefer exact per-video timing.
                try
                {
                    var exact = await _lyricsProvider.GetExactTimedLyricsAsync(recordingId);
                    if (!string.IsNullOrWhiteSpace(exact))
                    {
                        if (generation != Volatile.Read(ref _lyricsGeneration)
                            || playGeneration != Volatile.Read(ref _playGeneration)) return;
                        text = exact;
                        parsed = SyncedLyricsParser.Parse(exact);
                        await _musicDatabase.UpsertLyricsAsync(cacheKey, text);
                        System.Diagnostics.Trace.WriteLine($"[Lyrics] upgraded {recordingId} to exact timing ({parsed.Lines.Count} rows)");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The cached lyrics below are still perfectly usable.
                    System.Diagnostics.Trace.WriteLine($"[Lyrics] exact timing lookup failed: {ex.Message}");
                }
            }
            if (canResolveNow && missing)
            {
                try
                {
                    var resolved = await _lyricsProvider.GetLyricsAsync(
                        recordingId, track.Title, track.ArtistDisplay,
                        track.AlbumTitle, durationSeconds);
                    var replacement = SyncedLyricsParser.Parse(resolved);
                    System.Diagnostics.Trace.WriteLine(
                        $"[Lyrics] Resolve id={track.Id} chars={resolved?.Length ?? 0} " +
                        $"sync={replacement.SyncType} rows={replacement.Lines.Count}");
                    // Keep cached words on a miss/offline; only upgrade a plain hit
                    // when real timestamps are available.
                    if (!string.IsNullOrWhiteSpace(resolved) &&
                        (string.IsNullOrWhiteSpace(text) || replacement.SyncType != LyricsSyncType.None))
                    {
                        if (generation != Volatile.Read(ref _lyricsGeneration)
                            || playGeneration != Volatile.Read(ref _playGeneration)) return;
                        text = resolved;
                        parsed = replacement;
                        await _musicDatabase.UpsertLyricsAsync(cacheKey, text);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    System.Diagnostics.Trace.WriteLine($"[Lyrics] lookup failed: {ex.Message}");
                }
            }

            if (generation != Volatile.Read(ref _lyricsGeneration)
                || playGeneration != Volatile.Read(ref _playGeneration)) return;

            var timed = parsed.SyncType != LyricsSyncType.None;
            Dispatcher.UIThread.Post(() =>
            {
                if (generation != Volatile.Read(ref _lyricsGeneration)
                    || playGeneration != Volatile.Read(ref _playGeneration)) return;
                CurrentLyricIndex = -1;
                Lyrics.Clear();
                foreach (var line in parsed.Lines)
                    Lyrics.Add(new LyricLine { Time = line.Start, Text = line.Text, IsTimed = timed });
                Volatile.Write(ref _syncedLyrics, parsed);
                HasLyrics = Lyrics.Count > 0;
                UpdateCurrentLyricIndex(Position);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Lyrics] Load failed: {ex.Message}");
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (generation == Volatile.Read(ref _lyricsGeneration)) IsLoadingLyrics = false;
            });
        }
    }

    private void RefreshLyricsForPlayback(SongItem track, int playGeneration, TimeSpan decodedDuration, string playbackId)
        => Dispatcher.UIThread.Post(() =>
        {
            if (playGeneration != Volatile.Read(ref _playGeneration)
                || !ReferenceEquals(track, CurrentTrack) || IsVideoMode
                || !_appSettings.LyricsEnabled) return;
            _ = LoadLyricsAsync(track, decodedDuration, playbackId);
        });

    /// <summary>
    /// Line-highlight lead in milliseconds. Spotify / YouTube / SoundCloud all
    /// highlight exactly at the cue start (no positive lead): LRC timestamps
    /// already mark the vocal onset (Musixmatch stamps them up to ~0.5 s early),
    /// so any positive lead fires the highlight BEFORE the singing and reads
    /// as "rushing". Kept as a named constant (not inlined) for fine-tuning.
    /// </summary>
    private const int LyricLeadMs = 0;

    /// <summary>
    /// Returns the id that should actually be played. In Video mode this is the
    /// official music video (OMV); otherwise the official song (ATV) — exactly
    /// like YouTube Music's Song / Video switch.
    /// </summary>
    // Also album rows whose id is the OMV clip (YouTube sometimes lists the
    // clip as the album track, e.g. Sharks — Imagine Dragons).
    private static bool IsSelfPinnedClip(SongItem track) =>
        (string.Equals(track.VideoVersionId, track.Id, StringComparison.Ordinal)
         || string.Equals(track.VideoType, "MUSIC_VIDEO_TYPE_OMV", StringComparison.Ordinal))
        && (string.IsNullOrEmpty(track.SongVersionId)
            || string.Equals(track.SongVersionId, track.Id, StringComparison.Ordinal));

    private async Task ResolveSongForClipAsync(SongItem track)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var versions = await _musicProvider.GetTrackVersionsAsync(track, cts.Token);
            if (string.IsNullOrWhiteSpace(versions?.SongId)
                || string.Equals(versions.SongId, track.Id, StringComparison.Ordinal)) return;

            track.SongVersionId = versions.SongId;
            if (!string.IsNullOrWhiteSpace(versions.VideoId)) track.VideoVersionId = versions.VideoId;
            _ = UpsertOnlyAsync(track);
            // Song and clip now differ: show the Song/Video switch.
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(CurrentTrack, track)) HasVideoCounterpart = CanToggleVersions(track);
            });
            System.Diagnostics.Trace.WriteLine($"[Versions] clip {track.Id} → song {track.SongVersionId}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Versions] clip→song lookup failed for {track.Id}: {ex.Message}");
        }
    }

    private string GetPlaybackVideoId(SongItem track)
    {
        if (IsVideoMode && !string.IsNullOrEmpty(track.VideoVersionId))
            return track.VideoVersionId;
        return track.SongVersionId ?? track.Id;
    }

    /// <summary>
    /// Switches between the song audio and the music-video audio while keeping
    /// the same playback position and play/pause state.
    /// </summary>
    private async Task SwitchVideoModeAsync(bool toVideo)
    {
        var track = CurrentTrack;
        if (track == null) return;

        if (toVideo && string.IsNullOrEmpty(track.VideoVersionId))
        {
            IsVideoMode = false;
            StatusMessage = "No official video for this track.";
            return;
        }

        var wasPlaying = IsPlaying;

        // Switching to Video restarts everything from the very beginning; coming
        // back to Song continues from where the clip is.
        _resumePosition = toVideo ? TimeSpan.Zero : Position;
        _resumeTrackId = track.Id;

        // Silence the clip first: the song takes a moment to start, and until
        // then both would be heard over each other.
        if (!toVideo) _video?.Stop();

        var generation = Interlocked.Increment(ref _playGeneration);
        await PlayCurrentTrackAsync(generation);
        if (generation != Volatile.Read(ref _playGeneration)) return;

        if (IsVideoMode)
        {
            // PlayVideoAsync already started the clip (with sound); honour the
            // play/pause state the player was in before the switch.
            if (!wasPlaying)
            {
                _video?.Pause();
                IsPlaying = false;
            }
        }
        else
        {
            if (wasPlaying && _audioPlayer.State != MediaPlayerState.Playing)
                await _audioPlayer.ResumeAsync();
            else if (!wasPlaying && _audioPlayer.State == MediaPlayerState.Playing)
                await _audioPlayer.PauseAsync();

            // Re-arm the muted Canvas snippet.
            if (IsCanvasEnabled && IsFullscreenPlayer)
                _ = LoadCanvasAsync(track);
        }

        StatusMessage = IsVideoMode ? "Video mode on." : "Song mode on.";
    }

    /// <summary>Arms the seek fence so pre-seek timer ticks cannot echo back.</summary>
    private void ArmSeekFence(TimeSpan target)
    {
        // Queued UI ticks stay obsolete even after the time fence expires.
        Interlocked.Increment(ref _seekFenceGeneration);
        _seekFenceTarget = target;
        _seekFenceUntilUtc = DateTime.UtcNow + SeekFenceWindow;
    }

    /// <summary>True when a position tick is a pre-seek echo (stale capture).</summary>
    private bool IsStaleSeekEcho(TimeSpan pos)
        => DateTime.UtcNow < _seekFenceUntilUtc
           && Math.Abs((pos - _seekFenceTarget).TotalSeconds) > SeekFenceToleranceSeconds;

    /// <summary>One-line seek diagnostic: target vs highlighted line (see logs).</summary>
    private void LogSeekDiagnostic(string origin, TimeSpan target, TimeSpan posBefore, int indexBefore)
    {
        var indexAfter = LyricIndexFor(target);
        string LineOf(int i) => i >= 0 && i < Lyrics.Count
            ? $"{Lyrics[i].Time:mm\\:ss\\.fff} \"{Lyrics[i].Text}\""
            : "<none>";
        System.Diagnostics.Trace.WriteLine(
            $"[Lyrics] Seek commit ({origin}) target={target:mm\\:ss\\.fff} " +
            $"posBefore={posBefore:mm\\:ss\\.fff} idx {indexBefore}->{indexAfter} " +
            $"before=[{LineOf(indexBefore)}] after=[{LineOf(indexAfter)}]");
    }

    /// <summary>
    /// Index of the current line for an audible position. Thread-safe: reads the
    /// immutable <see cref="_syncedLyrics"/> snapshot, so the audio thread can call
    /// it directly. Lookup itself lives in <see cref="LyricsSyncEngine"/>.
    /// </summary>
    private int LyricIndexFor(TimeSpan position)
    {
        var adjusted = position + TimeSpan.FromMilliseconds(LyricLeadMs);
        if (adjusted < TimeSpan.Zero) adjusted = TimeSpan.Zero;
        return LyricsSyncEngine.FindLineIndex(Volatile.Read(ref _syncedLyrics), adjusted);
    }

    private void UpdateCurrentLyricIndex(TimeSpan position)
    {
        var index = LyricIndexFor(position);
        if (index != CurrentLyricIndex)
        {
            CurrentLyricIndex = index;
            if (DateTime.UtcNow < _seekFenceUntilUtc && index >= 0 && index < Lyrics.Count)
                System.Diagnostics.Trace.WriteLine(
                    $"[Lyrics] Seek highlight positionMs={position.TotalMilliseconds:F1} " +
                    $"lineMs={Lyrics[index].Time.TotalMilliseconds:F1} " +
                    $"sinceCueMs={(position - Lyrics[index].Time).TotalMilliseconds:F1}");
        }
    }

    /// <summary>Adjusts volume from keyboard shortcuts/tests.</summary>
    public void AdjustVolume(double delta)
    {
        Volume = Math.Clamp(Volume + delta, 0, 1);
    }

    [RelayCommand]
    private async Task PlayPauseAsync()
    {
        if (IsVideoMode)
        {
            // A restored video queue has no VLC media yet: start the current
            // clip instead of toggling an empty player (which silently did
            // nothing). NOTE: the engine is pre-warmed at startup, so _video is
            // non-null but has no media — check HasMedia, not a null _video.
            if ((_video == null || !_video.HasMedia) && CurrentTrack?.VideoVersionId is { Length: > 0 })
            {
                var generation = Interlocked.Increment(ref _playGeneration);
                await PlayCurrentTrackAsync(generation);
                return;
            }
            _video?.TogglePause();
            IsPlaying = _video?.IsPlaying ?? false;
            return;
        }

        switch (_audioPlayer.State)
        {
            case MediaPlayerState.Playing:
                await _audioPlayer.PauseAsync();
                break;
            case MediaPlayerState.Paused:
                await _audioPlayer.ResumeAsync();
                break;
            default:
                // Stopped / never loaded (e.g. a restored session) — (re)start
                // the current track instead of doing nothing.
                if (_queueService.CurrentTrack is { } current)
                {
                    if (_audioPlayer.State == MediaPlayerState.Error && Position > TimeSpan.Zero)
                    {
                        _resumePosition = Position;
                        _resumeTrackId = current.Id;
                    }
                    var generation = Interlocked.Increment(ref _playGeneration);
                    await PlayCurrentTrackAsync(generation);
                }
                break;
        }
    }

    [RelayCommand]
    private void NextTrack()
    {
        // Temporary origin log: catches which tap moves the queue.
        System.Diagnostics.Debug.WriteLine($"[Queue] NextTrack from index={_queueService.CurrentIndex}");
        _consecutivePlayFailures = 0;
        SkipForward();
    }

    [RelayCommand]
    private async Task PreviousTrack()
    {
        // Spotify / YouTube Music parity: ⏮ restarts the current track when
        // past the first seconds, and only steps to the previous track near
        // the start. A bare Previous() here made every restart attempt look
        // like the app "switching songs by itself".
        if (Position > TimeSpan.FromSeconds(3))
        {
            // Temporary origin log: catches which tap moves the queue.
            System.Diagnostics.Debug.WriteLine($"[Queue] PreviousTrack restarts current at {Position:mm\\:ss}");
            if (TryMovePendingResume(TimeSpan.Zero)) return;
            try
            {
                var target = TimeSpan.Zero;
                ArmSeekFence(target);
                if (IsVideoMode) _video?.Seek(target);
                else await _audioPlayer.SeekAsync(target);
                Position = target;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Seek] Restart failed: {ex.Message}");
            }
            return;
        }
        // Temporary origin log: catches which tap moves the queue.
        System.Diagnostics.Debug.WriteLine($"[Queue] PreviousTrack from index={_queueService.CurrentIndex}");
        _queueService.Previous();
    }

    [RelayCommand]
    private void ToggleShuffle()
    {
        _queueService.ToggleShuffle();
        IsShuffled = _queueService.IsShuffled;
        // Single source of truth: the player toggle IS the persisted default,
        // otherwise "Start shuffled" re-arms it on every launch after the
        // user turned it off (the toggle only flipped runtime state).
        _appSettings.ShuffleOnByDefault = IsShuffled;
    }

    [RelayCommand]
    private void ToggleRepeat()
    {
        _queueService.CycleRepeatMode();
        IsRepeating = _queueService.RepeatMode != RepeatMode.Off;
        OnPropertyChanged(nameof(RepeatIcon));
        _appSettings.RepeatMode = _queueService.RepeatMode.ToString();
    }

    public IconKind RepeatIcon => _queueService.RepeatMode == RepeatMode.One
        ? IconKind.RepeatOne
        : IconKind.Repeat;

    /// <summary>The speaker follows the level.</summary>
    public IconKind VolumeIcon => IsMuted || Volume <= 0.001
        ? IconKind.VolumeMute
        : Volume < 0.5 ? IconKind.VolumeLow : IconKind.VolumeHigh;

    private DispatcherTimer? _sleepTimer;
    private DispatcherTimer? _sleepUiTimer;
    private DateTime _sleepTimerEndUtc;

    /// <summary>True while a sleep timer is armed (drives the menu/tooltip state).</summary>
    [ObservableProperty]
    private bool _isSleepTimerActive;

    /// <summary>Menu/tooltip text: live countdown or off state.</summary>
    public string SleepTimerText
    {
        get
        {
            if (!IsSleepTimerActive) return "Sleep timer off";
            var left = _sleepTimerEndUtc - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return "Sleeping…";
            var mins = Math.Max(1, (int)Math.Ceiling(left.TotalMinutes));
            return $"Sleep in {mins} min";
        }
    }

    partial void OnIsSleepTimerActiveChanged(bool value) => OnPropertyChanged(nameof(SleepTimerText));

    /// <summary>Sleep timer: pauses playback after N minutes (0 = cancel).</summary>
    [RelayCommand]
    private void StartSleepTimer(string minutes)
    {
        StopSleepTimer(silent: true);

        if (!int.TryParse(minutes, out var m) || m <= 0)
        {
            StatusMessage = "Sleep timer off.";
            return;
        }

        m = Math.Min(m, 480);
        _sleepTimerEndUtc = DateTime.UtcNow + TimeSpan.FromMinutes(m);
        _sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(m) };
        _sleepTimer.Tick += async (_, _) => await FireSleepTimerAsync();
        _sleepTimer.Start();
        // Refresh the countdown text while armed.
        _sleepUiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _sleepUiTimer.Tick += (_, _) => OnPropertyChanged(nameof(SleepTimerText));
        _sleepUiTimer.Start();

        IsSleepTimerActive = true;
        StatusMessage = $"Sleep timer set: {m} min.";
        System.Diagnostics.Debug.WriteLine($"[SleepTimer] set for {m} min");
    }

    /// <summary>Stops the timer; the UI state follows unless silenced.</summary>
    private void StopSleepTimer(bool silent = false)
    {
        _sleepTimer?.Stop();
        _sleepTimer = null;
        _sleepUiTimer?.Stop();
        _sleepUiTimer = null;
        if (IsSleepTimerActive)
        {
            IsSleepTimerActive = false;
            if (!silent) StatusMessage = "Sleep timer off.";
        }
        else OnPropertyChanged(nameof(SleepTimerText));
    }

    /// <summary>Fires once: pauses whatever is actually playing (audio, video
    /// or both — the mode may have changed since the timer was set).</summary>
    private async Task FireSleepTimerAsync()
    {
        StopSleepTimer(silent: true);
        try
        {
            if (_video?.IsPlaying == true) _video.Pause();
            if (_audioPlayer.State == MediaPlayerState.Playing)
                await _audioPlayer.PauseAsync();
            IsPlaying = false;
            StatusMessage = "Sleep timer fired — playback paused.";
            System.Diagnostics.Debug.WriteLine("[SleepTimer] fired, playback paused");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[SleepTimer] fire failed: {ex.Message}");
        }
    }

    partial void OnCurrentTrackChanged(SongItem? value)
    {
        // Persist the listen for the track that just ended/was replaced.
        FlushPlaySession();

        _recoveredTrack = null;
        
        // Reset playback position immediately so the UI (and lyrics) don't jump to the old track's time.
        Position = TimeSpan.Zero;

        // Highlight the now-playing item in whatever list it came from.
        if (_highlightedTrack != null) _highlightedTrack.IsCurrent = false;
        if (value != null) value.IsCurrent = true;
        _highlightedTrack = value;

        // The player-bar / fullscreen hearts bind IsCurrentTrackLiked (not the
        // row's own IsLiked), so follow the current track's like flag: without
        // this a toggle from any list leaves those hearts stale.
        if (!ReferenceEquals(_likeObservedTrack, value))
        {
            if (_likeObservedTrack != null)
                _likeObservedTrack.PropertyChanged -= OnCurrentTrackLikedSourceChanged;
            _likeObservedTrack = value;
            if (_likeObservedTrack != null)
                _likeObservedTrack.PropertyChanged += OnCurrentTrackLikedSourceChanged;
        }

        // A new track has no known versions yet: drop the old Canvas and let the
        // official version lookup (below) restart it only if a clip exists.
        StopCanvas();
        // A saved canvas resume belongs to the previous clip only — carrying
        // it over would restart the new track from the old offset (or resurrect
        // it when the same track replays later). Deliberately NOT cleared
        // inside StopCanvas itself: LoadCanvasAsync still needs it as the
        // re-entry fallback for the SAME clip.
        _canvasResumeVideoId = null;
        _canvasResumeSeconds = 0;
        if (value != null)
        {
            _ = PrimeTrackVersionsAsync(value);
            _ = WarmNextCanvasAsync(value);
        }

        OnPropertyChanged(nameof(IsCurrentTrackLiked));
        OnPropertyChanged(nameof(IsCurrentTrackDownloaded));
        OnPropertyChanged(nameof(IsCurrentTrackDownloading));
        OnPropertyChanged(nameof(HasTrack));
#if WINDOWS
        UpdateSmtcMetadata();
        _smtc?.SetPlaybackState(IsPlaying, value != null);
#endif
        OnPropertyChanged(nameof(TrackTitle));
        OnPropertyChanged(nameof(TrackArtist));
        OnPropertyChanged(nameof(CurrentAlbumTitle));
        WindowTitle = value == null
            ? "SoundLibert"
            : $"{value.Title} — {value.ArtistDisplay} · SoundLibert";
        _ = LoadArtworkAsync(value);
        SyncFullscreenUpNext();
        // Whatever is still loading for the previous track no longer applies.
        Interlocked.Increment(ref _fullscreenDetailsGeneration);
        _fullscreenDetailsTrack = null;
        RefreshFullscreenDetails();
        if (_appSettings.LyricsEnabled)
        {
            _ = LoadLyricsAsync(value);
        }
        else
        {
            Interlocked.Increment(ref _lyricsGeneration);
            Volatile.Write(ref _syncedLyrics, null);
            Lyrics.Clear();
            HasLyrics = false;
            CurrentLyricIndex = -1;
        }
    }

    private void ApplyAccent(Color c) => AmbientColor = c;

    private void ClearAccent() => AmbientColor = RestingAmbient;

    /// <summary>
    /// Pulls the cover art + its dominant colour for the Dynamic Now Playing
    /// tint. Generation-guarded so a slow download for a previous track can
    /// never repaint the UI after the user has moved on.
    /// </summary>
    private async Task LoadArtworkAsync(SongItem? track)
    {
        var generation = Interlocked.Increment(ref _artworkGeneration);

        if (track == null || string.IsNullOrWhiteSpace(track.ThumbnailUrl))
        {
            Dispatcher.UIThread.Post(() =>
            {
                ArtworkImage = null;
                ClearAccent();
            });
            return;
        }

        var info = await _artwork.GetAsync(track.ThumbnailUrl);
        if (generation != Volatile.Read(ref _artworkGeneration)) return;

        Dispatcher.UIThread.Post(() =>
        {
            ArtworkImage = info.Image;
            ApplyAccent(info.Color);
        });
    }

    private void RecordHistoryAsync(SongItem track)
    {
        // Ensure the song row exists; the play is recorded when the session ends
        // (so the DB gets the real listened time, not 0).
        _ = UpsertOnlyAsync(track);
        BeginPlaySession(track);
    }

    private async Task UpsertOnlyAsync(SongItem track)
    {
        try
        {
            await _musicDatabase.UpsertSongAsync(track.ToEntity());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Library] Upsert song failed: {ex.Message}");
        }
    }

    private SongItem? _pendingTrack;
    // Runs only while the track plays: an hour on pause is not an hour listened.
    private readonly System.Diagnostics.Stopwatch _pendingListenClock = new();

    private void BeginPlaySession(SongItem track)
    {
        FlushPlaySession();
        _pendingTrack = track;
        _pendingListenClock.Restart();
    }

    /// <summary>Persists the finished listen (time actually played) and clears the session.</summary>
    private void FlushPlaySession()
    {
        if (_pendingTrack == null) return;

        var track = _pendingTrack;
        var elapsed = _pendingListenClock.Elapsed;
        _pendingTrack = null;
        _pendingListenClock.Reset();

        if (elapsed.TotalSeconds < 3) return; // ignore skips

        var cap = Math.Max(1, track.DurationSeconds) * 1000.0;
        var ms = (int)Math.Min(elapsed.TotalMilliseconds, cap);
        _ = PersistPlayAsync(track, ms);
    }

    private async Task PersistPlayAsync(SongItem track, int playedMs)
    {
        try
        {
            await _musicDatabase.RecordPlayAsync(track.Id, playedMs, null, _account.AccountScopeKey);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Library] Record history failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Flushes the current listen on shutdown. Runs the DB write on the thread
    /// pool and blocks briefly so it completes before the process exits
    /// (awaiting on the UI thread would deadlock — the DB calls resume on the
    /// captured context).
    /// </summary>
    public void EndPlaybackSession()
    {
        if (_pendingTrack == null) return;

        var track = _pendingTrack;
        var elapsed = _pendingListenClock.Elapsed;
        _pendingTrack = null;
        _pendingListenClock.Reset();

        if (elapsed.TotalSeconds < 3) return;

        var cap = Math.Max(1, track.DurationSeconds) * 1000.0;
        var ms = (int)Math.Min(elapsed.TotalMilliseconds, cap);

        try
        {
            // Bounded wait: an unbounded GetResult() could hang the UI thread on
            // shutdown if SQLite was busy, leaving the app unresponsive on exit.
            var scope = _account.AccountScopeKey;
            var write = Task.Run(() => _musicDatabase.RecordPlayAsync(track.Id, ms, null, scope));
            if (!write.Wait(TimeSpan.FromSeconds(2)))
                System.Diagnostics.Trace.WriteLine("[Library] Final record timed out (non-fatal).");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Library] Final record failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Takes the remembered position for the first play after a restore (once).
    /// It is passed INTO PlayAsync as the start offset — seeking a just-started
    /// output via SeekAsync collides with its priming buffers (WaveStillPlaying
    /// → dead output → phantom skip to the next track), so the resume path
    /// never seeks anymore.
    /// </summary>
    private TimeSpan TakeResumePosition(SongItem? track)
    {
        if (_resumePosition <= TimeSpan.Zero) return TimeSpan.Zero;

        // Only resume the track we restored — a different track must start at 0.
        if (_resumeTrackId != null && track != null && _resumeTrackId != track.Id)
        {
            _resumePosition = TimeSpan.Zero;
            _resumeTrackId = null;
            return TimeSpan.Zero;
        }

        var resume = _resumePosition;
        _resumePosition = TimeSpan.Zero;
        _resumeTrackId = null;
        return resume;
    }

    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    private async Task PlayCurrentTrackAsync(int generation)
    {
        var track = _queueService.CurrentTrack;
        if (track == null)
        {
            Dispatcher.UIThread.Post(() => IsTrackLoading = false);
            return;
        }
        Dispatcher.UIThread.Post(() => IsTrackLoading = true);

        try
        {
            // Video mode: a single player (VLC) plays the clip with its sound.
            if (IsVideoMode && !string.IsNullOrEmpty(track.VideoVersionId))
            {
                await PlayVideoAsync(track, generation);
                return;
            }

            // Local files skip stream resolution entirely and play from disk.
            if (track.IsLocal && !string.IsNullOrWhiteSpace(track.LocalPath))
            {
                await PlayFromDiskAsync(track, track.LocalPath!, "Opening local file…", generation);
                return;
            }

            // Downloaded catalogue songs play from the offline copy (no network).
            if (_downloads.TryGetPath(track.Id, out var downloadPath)
                && !string.IsNullOrWhiteSpace(downloadPath))
            {
                await PlayFromDiskAsync(track, downloadPath!, "Playing downloaded copy…", generation);
                return;
            }

            // A row saved from a video card pins its song version to the clip
            // itself, so Song mode played the clip's audio (intro/outro →
            // lyrics out of sync). Look up the real song once before playing.
            if (!IsVideoMode && IsSelfPinnedClip(track))
                await ResolveSongForClipAsync(track);
            if (generation != Volatile.Read(ref _playGeneration)) return;

            var playbackId = GetPlaybackVideoId(track);
            System.Diagnostics.Debug.WriteLine($"Resolving stream for: {track.Title} (ID: {playbackId})");
            Dispatcher.UIThread.Post(() => StatusMessage = "Fetching stream…");
            var streamInfo = await _musicProvider.GetStreamInfoAsync(playbackId, null, _appSettings.AudioQuality);

            // A newer track was selected while we were resolving — drop this
            // stale result instead of clobbering the new playback.
            if (generation != Volatile.Read(ref _playGeneration)) return;

            if (streamInfo == null)
            {
                System.Diagnostics.Trace.WriteLine($"[Playback] no stream for {playbackId}: every client refused it");
                await SkipFailedTrackAsync(track, generation);
                return;
            }

            System.Diagnostics.Trace.WriteLine($"[Playback] {playbackId} via {streamInfo.ClientName}, {streamInfo.MimeType}");
            var headers = _musicProvider.GetStreamHeaders(streamInfo.Url, streamInfo.ClientName);

            _audioPlayer.NormalizationGain = _audioSettings.NormalizeVolume
                ? LoudnessNormalization.GainFor(streamInfo.LoudnessDb)
                : 1.0;

            await _audioPlayer.PlayAsync(streamInfo.Url, headers, TakeResumePosition(track));

            // Superseded while starting: whoever is newer owns the player now, so
            // this must not touch it (a Stop here killed the next track).
            if (generation != Volatile.Read(ref _playGeneration)) return;
            OnPlaybackStarted(track, generation, playbackId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Playback] {track.Id} failed: {ex.GetType().Name}: {ex.Message}");
            if (generation != Volatile.Read(ref _playGeneration)) return;

            // Windows N editions ship without Media Foundation: no track can play.
            if (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    StatusMessage = string.Empty;
                    IsTrackLoading = false;
                });
                ShowToast("Playback needs the Media Feature Pack for your edition of Windows.", isError: true);
                return;
            }
            await SkipFailedTrackAsync(track, generation);
        }
    }

    private async Task PlayFromDiskAsync(SongItem track, string path, string status, int generation)
    {
        Dispatcher.UIThread.Post(() => StatusMessage = status);
        // No loudness information for files: the previous stream's must not stick.
        _audioPlayer.NormalizationGain = 1.0;
        await _audioPlayer.PlayAsync(path, NoHeaders, TakeResumePosition(track));
        if (generation != Volatile.Read(ref _playGeneration)) return;
        OnPlaybackStarted(track, generation, GetPlaybackVideoId(track));
    }

    private void OnPlaybackStarted(SongItem track, int generation, string playbackId)
    {
        _consecutivePlayFailures = 0;

        // Prefer the real decoded duration; fall back to search metadata.
        var decoded = _audioPlayer.Duration;
        var duration = decoded > TimeSpan.Zero ? decoded : TimeSpan.FromSeconds(track.DurationSeconds);
        Dispatcher.UIThread.Post(() =>
        {
            Duration = duration;
            StatusMessage = string.Empty;
            IsTrackLoading = false;
        });

        RefreshLyricsForPlayback(track, generation, decoded, playbackId);

        // Persist this listen (additive side-effect only). Fire-and-forget so a
        // slow disk can never delay audio.
        RecordHistoryAsync(track);
    }

    // ---- When a track cannot play ----

    private const int MaxConsecutivePlayFailures = 3;
    private int _consecutivePlayFailures;

    /// <summary>The track that already got its one retry after failing mid-way.</summary>
    private SongItem? _recoveredTrack;

    /// <summary>
    /// One unavailable track must not end the listening session: say so and move
    /// on. Several in a row point at something bigger (no connection, say), so
    /// the queue is not run through for nothing.
    /// </summary>
    private async Task SkipFailedTrackAsync(SongItem track, int generation)
    {
        _consecutivePlayFailures++;
        Dispatcher.UIThread.Post(() =>
        {
            StatusMessage = string.Empty;
            IsTrackLoading = false;
        });

        if (_consecutivePlayFailures >= MaxConsecutivePlayFailures)
        {
            ShowToast("Several tracks in a row couldn't play. Check your connection.", isError: true);
            return;
        }

        ShowToast($"Couldn't play “{track.Title}”. Skipping it.", isError: true);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        if (generation != Volatile.Read(ref _playGeneration)) return;
        await Dispatcher.UIThread.InvokeAsync(SkipForward);
    }

    /// <summary>
    /// The track stopped mid-way (a failed download, a lost output device). The
    /// usual causes pass, so a fresh stream first continues from the same spot;
    /// only a second failure gives up on the track. UI thread.
    /// </summary>
    private void OnPlaybackFailed(Exception error)
    {
        if (IsVideoMode || CurrentTrack is not { } track) return;
        var generation = Interlocked.Increment(ref _playGeneration);

        if (!ReferenceEquals(_recoveredTrack, track))
        {
            System.Diagnostics.Trace.WriteLine($"[Playback] {track.Id} stopped at {Position:mm\\:ss} ({error.Message}), retrying");
            _recoveredTrack = track;
            _resumePosition = Position;
            _resumeTrackId = track.Id;
            _ = PlayCurrentTrackAsync(generation);
            return;
        }

        _ = SkipFailedTrackAsync(track, generation);
    }
}
