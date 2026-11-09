using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;
using SoundLibert.UI.Controls;
using SoundLibert.UI.Services;
using SoundLibert.UI.ViewModels;

namespace SoundLibert.UI.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private TrayIcon? _tray;
    private bool _exitRequested;

    private static IAppSettings? AppSettings =>
        App.ServiceProvider?.GetService(typeof(IAppSettings)) as IAppSettings;

    public MainWindow()
    {
        Motion.Enabled = AppSettings?.ReduceMotion != true && Motion.SystemAnimationsOn();

        InitializeComponent();
        StartupClock.Mark("window xaml");

        DataContext = App.ServiceProvider?.GetService(typeof(MainWindowViewModel));
        _viewModel = DataContext as MainWindowViewModel;
        _viewModel?.Initialize();

        // One wheel behaviour for every list in the window.
        SmoothScroller.Attach(this);

        if (_viewModel != null)
        {
            // The full player's controls stay while the pointer is on them, a
            // detail below the fold is being read, or the queue is open.
            _viewModel.KeepFullscreenControls = () =>
                FullscreenToolbarFade.IsPointerOver || FullscreenTransportFade.IsPointerOver ||
                FullscreenScroll.Offset.Y > 4 || _viewModel.ShowQueue;
            UpdateNavPill();
        }
        SizeChanged += (_, _) => UpdateFullscreenScale();
        UpdateFullscreenScale();

        // Below the song the page fades into bands around the controls. An opacity mask
        // looked the same and cost close to half of the frames over a playing clip (87 > 47 fps).
        FullscreenScroll.ScrollChanged += (_, _) => UpdatePageBands();
        FullscreenTransportFade.SizeChanged += (_, _) => PlacePageBand();

        // Remember how far each page was scrolled, for the way back.
        ContentHost.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            if (_viewModel?.CurrentView is { } page && e.Source is ScrollViewer { Offset.Y: var y } sv &&
                sv.VerticalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled &&
                sv.Extent.Height > sv.Viewport.Height + 1)
            {
                _scrollMemory[page] = y;
            }
        });

        // Warm the libvlc engine in the background so the first Canvas/Video
        // open is not slowed down by one-off native init.
        if (_viewModel != null) _ = _viewModel.PrepareVideoEngineAsync();

        if (_viewModel != null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Seek slider: the Thumb marks pointer events Handled, so a bubbling
        // PointerPressed/Released on the Slider never fires. Handlers must be
        // registered on the TUNNEL phase (top-down) to run before the Thumb.
        foreach (var slider in new[] { SeekSlider, SeekSliderFull })
        {
            slider.AddHandler(PointerPressedEvent, (_, _) => SeekStart(), RoutingStrategies.Tunnel);
            slider.AddHandler(PointerReleasedEvent, (_, _) => SeekEnd(), RoutingStrategies.Tunnel);
        }

        // Reveal + restart the fullscreen Video-mode controls (auto-hide) on any
        // pointer or keyboard activity.
        AddHandler(PointerMovedEvent, (_, _) => _viewModel?.NotifyFullscreenActivity(), RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, _) => _viewModel?.NotifyFullscreenActivity(), RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, _) => _viewModel?.NotifyFullscreenActivity(), RoutingStrategies.Tunnel);
        // Keyboard-shortcut help (F1 / ?); Esc closes it first.
        AddHandler(KeyDownEvent, OnShortcutKey, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            // Clicking outside a text field leaves it, so Space is play / pause again.
            if (FocusManager?.GetFocusedElement() is TextBox && !IsInsideTextBox(e.Source))
                Root.Focus();

            // The mouse's back button goes back, anywhere in the window.
            if (e.GetCurrentPoint(this).Properties.IsXButton1Pressed && _viewModel?.CanGoBack == true)
            {
                if (_viewModel.IsFullscreenPlayer) _viewModel.CloseFullscreenPlayerCommand.Execute(null);
                else _viewModel.GoBackCommand.Execute(null);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        // A second launch of the app asks this window to come forward.
        SoundLibert.UI.Services.SingleInstance.ListenForActivation(
            () => Dispatcher.UIThread.Post(RestoreFromTray));

        // One right-click handler for the whole app (rows, cards, tiles).
        AddHandler(ContextRequestedEvent, OnItemContextRequested, RoutingStrategies.Bubble);
        // ...and one handler for every card's hover play button.
        AddHandler(Button.ClickEvent, OnCardPlayClick, RoutingStrategies.Bubble);

        // Media keys (Play/Pause, Next, Previous) while the window is active.
        if (OperatingSystem.IsWindows())
            Win32Properties.AddWndProcHookCallback(this, MediaKeyHook);

        // Tray icon (Settings → Notifications & Window). Created hidden; shown
        // only when the app is minimized/closed to tray.
        SetupTray();
        // Respect "Start minimized": hide straight to the tray on first open.
        Opened += (_, _) => { if (AppSettings?.StartMinimized == true) HideToTray(); };
        Opened += (_, _) =>
        {
            StartupClock.Mark("opened");
            RequestAnimationFrame(_ => Dispatcher.UIThread.Post(() =>
            {
                StartupClock.Finish();
                ContentShown?.Invoke();
                // The welcome enters with the opening's fade; alone, at once. Should the
                // opening never say it leaves, the welcome does not wait for ever.
                if (!_openingPlays) _welcome?.Enter(null);
                else DispatcherTimer.RunOnce(() => _welcome?.Enter(null), TimeSpan.FromSeconds(6));
            }, DispatcherPriority.Background));
        };
        if (WelcomeWanted()) ShowWelcome(atStart: true);

        // A downloaded version says so in a card; someone who keeps coming back is asked,
        // rarely, for a star.
        if (_viewModel != null) _viewModel.Updates.UpdateReady += ShowUpdateNotice;
        CountLaunch();
        DispatcherTimer.RunOnce(MaybeAskForStar, TimeSpan.FromMinutes(3));
#if WINDOWS
        // Publish the now-playing track to the Windows media overlay once the
        // native window handle exists.
        Opened += (_, _) => _viewModel?.InitializeSmtc(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
#endif
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                OnWindowStateChanged();
                UpdateCaptionButtons();
            }
            else if (e.Property == OffScreenMarginProperty)
            {
                // A maximised borderless window hangs slightly off the screen.
                Root.Margin = OffScreenMargin;
            }
        };
    }

    /// <summary>The window's first full frame is on screen (the opening hands over then).</summary>
    public event Action? ContentShown;

    // ---- Welcome ----

    private SoundLibert.UI.Welcome.WelcomeView? _welcome;
    private bool _openingPlays;

    /// <summary>The opening plays over this window: the welcome waits for it to leave.</summary>
    public void WaitForOpening()
    {
        _openingPlays = true;
        _welcome?.HoldForOpening();
    }

    /// <summary>The opening has begun to fade: the welcome takes the bird over from where it was drawn.</summary>
    public void OpeningLeaves((PixelPoint Centre, double Width)? logo) => _welcome?.Enter(logo);

    // ---- Notices and feedback ----

    /// <summary>A new version has downloaded: a card says so, with what it changes.</summary>
    private void ShowUpdateNotice(string version)
    {
        if (_viewModel?.Updates is not { } updates) return;
        // Never over the welcome: after it.
        if (_welcome is { } welcome)
        {
            welcome.Gone += () => ShowUpdateNotice(version);
            return;
        }
        Notice.Show(new Notice(
            $"SoundLibert {version} is ready",
            "It has downloaded. Restart to use it now, or it installs the next time you open SoundLibert.",
            "Restart now", updates.RestartToUpdate,
            "Later",
            Notes: updates.ReleaseNotes));
    }

    private static void CountLaunch()
    {
        if (Environment.GetEnvironmentVariable("SL_SHOT") != null || AppSettings is not { } settings) return;
        var nudges = settings.Nudges;
        nudges.FirstLaunchUtc ??= DateTime.UtcNow;
        nudges.Launches++;
        settings.Save();
    }

    /// <summary>Someone who keeps coming back is asked, once in a long while, for a star on GitHub.</summary>
    private void MaybeAskForStar()
    {
        if (AppSettings is not { } settings || !settings.Nudges.ShouldAskForStar(DateTime.UtcNow)) return;
        // Not over anything else, and not while the full player has the screen.
        if (_welcome != null || Notice.IsShowing || _viewModel?.IsFullscreenPlayer == true ||
            !IsVisible || WindowState == WindowState.Minimized)
        {
            DispatcherTimer.RunOnce(MaybeAskForStar, TimeSpan.FromMinutes(10));
            return;
        }

        var nudges = settings.Nudges;
        nudges.StarAsks++;
        nudges.LastStarAskUtc = DateTime.UtcNow;
        settings.Save();
        Notice.Show(new Notice(
            "Enjoying SoundLibert?",
            "It's free and open source. A star on GitHub helps other people find it.",
            "Star on GitHub",
            () =>
            {
                nudges.StarGiven = true;
                settings.Save();
                AppLinks.Open(AppLinks.Repository);
            },
            "Not now",
            Symbol: IconKind.Star));
    }

    private void ReportBug_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.SettingsViewModel is not { } settings) return;
        _ = BugReportWindow.ShowAsync(this, settings.Updates, settings.SettingsForReports, settings.Notify ?? (_ => { }));
    }

    /// <summary>A new listener sees the welcome once. QA asks for it with SL_SHOT=welcome.</summary>
    private static bool WelcomeWanted()
    {
        var shot = Environment.GetEnvironmentVariable("SL_SHOT");
        if (shot != null) return shot == "welcome";
        return AppSettings is { WelcomeSeen: false, StartMinimized: false };
    }

    /// <summary>Lays the welcome over the window: at a first start, and from Settings, About.</summary>
    public void ShowWelcome(bool atStart = false)
    {
        if (_welcome != null) return;
        if (App.ServiceProvider?.GetService(typeof(IMusicProvider)) is not IMusicProvider music) return;
        var settings = AppSettings;
        // Written down as unseen, so a first start closed halfway shows it again.
        if (atStart && settings != null && Environment.GetEnvironmentVariable("SL_SHOT") == null)
            settings.WelcomeSeen = false;

        var welcome = new SoundLibert.UI.Welcome.WelcomeView(new SoundLibert.UI.Welcome.WelcomeViewModel(music, settings))
        {
            ZIndex = 25
        };
        welcome.Finished += () =>
        {
            if (_viewModel == null) return;
            if (!ReferenceEquals(_viewModel.CurrentView, _viewModel.HomeViewModel))
                _viewModel.NavigateToHomeCommand.Execute(null);
            _viewModel.HomeViewModel.TasteChanged();
        };
        welcome.Gone += () =>
        {
            Root.Children.Remove(welcome);
            if (ReferenceEquals(_welcome, welcome)) _welcome = null;
            Root.Focus();
        };
        _welcome = welcome;
        Root.Children.Add(welcome);
        if (!atStart) Dispatcher.UIThread.Post(() => welcome.Enter(null), DispatcherPriority.Loaded);
    }

    private static bool IsInsideTextBox(object? source) =>
        source is Visual visual && (visual is TextBox || visual.FindAncestorOfType<TextBox>() != null);

    private void SetupTray()
    {
        try
        {
            _tray = new TrayIcon
            {
                IsVisible = false,
                ToolTipText = "SoundLibert",
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SoundLibert.UI/Assets/app.ico")))
            };
            var openItem = new NativeMenuItem { Header = "Open SoundLibert" };
            openItem.Click += (_, _) => RestoreFromTray();
            var exitItem = new NativeMenuItem { Header = "Exit" };
            exitItem.Click += (_, _) => ExitFromTray();
            _tray.Menu = new NativeMenu { Items = { openItem, exitItem } };
            _tray.Clicked += (_, _) => RestoreFromTray();
            TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Tray] setup failed: {ex.Message}");
        }
    }

    private void OnWindowStateChanged()
    {
        if (WindowState == WindowState.Minimized && (AppSettings?.MinimizeToTray ?? false))
            HideToTray();
    }

    private void HideToTray()
    {
        if (_tray != null) _tray.IsVisible = true;
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (_tray != null) _tray.IsVisible = false;
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        if (_tray != null) _tray.IsVisible = false;
        Close();
    }

    private void OnShortcutKey(object? sender, KeyEventArgs e)
    {
        if (_viewModel == null) return;
        // Space is play / pause everywhere outside a text field. Without this it
        // repeats whichever button was clicked last.
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None &&
            FocusManager?.GetFocusedElement() is not TextBox)
        {
            _viewModel.PlayPauseCommand.Execute(null);
            e.Handled = true;
            return;
        }
        // Alt + Left goes back; plain arrows seek.
        if (e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt && _viewModel.CanGoBack)
        {
            _viewModel.GoBackCommand.Execute(null);
            e.Handled = true;
            return;
        }

        var isHelp = e.Key == Key.F1 ||
                     (e.Key == Key.Oem2 && e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        if (isHelp && e.Source is not TextBox)
        {
            _viewModel.ShowShortcuts = !_viewModel.ShowShortcuts;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _viewModel.ShowShortcuts)
        {
            _viewModel.ShowShortcuts = false;
            e.Handled = true;
        }
    }

    private void ShortcutsBackdrop_Tapped(object? sender, TappedEventArgs e)
    {
        // Clicking the dim backdrop (not the card) closes the overlay.
        if (ReferenceEquals(e.Source, sender) && _viewModel != null)
            _viewModel.ShowShortcuts = false;
    }

    private void ShortcutsClose_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel != null) _viewModel.ShowShortcuts = false;
    }

    /// <summary>
    /// Handles the keyboard/headset media keys (delivered as WM_APPCOMMAND to the
    /// focused window). Local only — no global hotkey registration, so it neither
    /// steals the keys from other apps nor requires elevated privileges.
    /// </summary>
    private IntPtr MediaKeyHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const uint WM_APPCOMMAND = 0x0319;
        const int APPCOMMAND_MEDIA_NEXTTRACK = 11;
        const int APPCOMMAND_MEDIA_PREVIOUSTRACK = 12;
        const int APPCOMMAND_MEDIA_STOP = 13;
        const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;
        const int APPCOMMAND_MEDIA_PLAY = 46;
        const int APPCOMMAND_MEDIA_PAUSE = 47;

        if (msg == WM_APPCOMMAND && _viewModel is { } vm)
        {
            var cmd = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
            switch (cmd)
            {
                case APPCOMMAND_MEDIA_PLAY_PAUSE:
                    vm.PlayPauseCommand.Execute(null);
                    handled = true;
                    break;
                case APPCOMMAND_MEDIA_NEXTTRACK:
                    vm.NextTrackCommand.Execute(null);
                    handled = true;
                    break;
                case APPCOMMAND_MEDIA_PREVIOUSTRACK:
                    vm.PreviousTrackCommand.Execute(null);
                    handled = true;
                    break;
                case APPCOMMAND_MEDIA_STOP:
                    if (vm.IsPlaying) vm.PlayPauseCommand.Execute(null);
                    handled = true;
                    break;
                case APPCOMMAND_MEDIA_PLAY:
                    if (!vm.IsPlaying) vm.PlayPauseCommand.Execute(null);
                    handled = true;
                    break;
                case APPCOMMAND_MEDIA_PAUSE:
                    if (vm.IsPlaying) vm.PlayPauseCommand.Execute(null);
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    private void SeekStart()
    {
        if (_viewModel != null) _viewModel.IsSeeking = true;
    }

    private void SeekEnd()
    {
        if (_viewModel == null) return;
        _viewModel.IsSeeking = false;
        _viewModel.CommitSeekCommand.Execute(null);
    }

    // ---- Queue drag-reorder (manual pointer capture; rows follow the pointer) ----

    private SongItem? _queueDragSong;
    private Avalonia.Controls.Presenters.ContentPresenter? _queueDragContainer;
    private int _queueDragFrom = -1;
    private double _queueDragStartY;
    private double _queueDragRowHeight = 48;
    private double _queueDragOffset;
    private bool _suppressQueueTap;

    private void QueueDrag_Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel == null || QueueList == null) return;
        if (sender is not Control { DataContext: SongItem song } handle) return;
        if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;

        _queueDragFrom = _viewModel.QueueItems.IndexOf(song);
        if (_queueDragFrom < 0) return;
        _queueDragSong = song;
        _queueDragContainer = handle.FindAncestorOfType<Avalonia.Controls.Presenters.ContentPresenter>();
        _queueDragStartY = e.GetPosition(QueueList).Y;
        _queueDragOffset = 0;
        if (_queueDragContainer != null)
        {
            _queueDragRowHeight = Math.Max(24, _queueDragContainer.Bounds.Height);
            _queueDragContainer.ZIndex = 10;
            _queueDragContainer.Classes.Add("queueDragging");
        }
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void QueueDrag_Moved(object? sender, PointerEventArgs e)
    {
        if (_queueDragSong == null || QueueList == null || _viewModel == null) return;
        var count = _viewModel.QueueItems.Count;
        // Keep the row inside the list while it follows the pointer.
        var min = -_queueDragFrom * _queueDragRowHeight;
        var max = (count - 1 - _queueDragFrom) * _queueDragRowHeight;
        _queueDragOffset = Math.Clamp(e.GetPosition(QueueList).Y - _queueDragStartY, min, max);
        if (Math.Abs(_queueDragOffset) > 4) _suppressQueueTap = true;
        if (_queueDragContainer != null)
            _queueDragContainer.RenderTransform = new Avalonia.Media.TranslateTransform(0, _queueDragOffset);
        e.Handled = true;
    }

    private void QueueDrag_Released(object? sender, PointerReleasedEventArgs e)
    {
        if (_queueDragSong == null) return;
        var from = _queueDragFrom;
        var to = from + (int)Math.Round(_queueDragOffset / _queueDragRowHeight);
        EndQueueDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
        // The row's Tapped (if any) fires during this release; clear the
        // guard right after so it can never swallow a later real click.
        Dispatcher.UIThread.Post(() => _suppressQueueTap = false, DispatcherPriority.Background);
        if (_viewModel != null && to != from)
            _viewModel.MoveQueueItem(from, Math.Clamp(to, 0, _viewModel.QueueItems.Count - 1));
    }

    private void QueueDrag_CaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndQueueDrag();

    private void EndQueueDrag()
    {
        if (_queueDragContainer != null)
        {
            _queueDragContainer.RenderTransform = null;
            _queueDragContainer.ZIndex = 0;
            _queueDragContainer.Classes.Remove("queueDragging");
        }
        _queueDragContainer = null;
        _queueDragSong = null;
        _queueDragFrom = -1;
        _queueDragOffset = 0;
    }

    private void QueueItem_Tapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel == null) return;
        if (_suppressQueueTap) { _suppressQueueTap = false; return; }
        if (SoundLibert.UI.Controls.InputGuards.FromButton(e)) return;
        if (sender is Border { DataContext: SongItem song })
        {
            var index = _viewModel.QueueItems.IndexOf(song);
            if (index >= 0) _viewModel.PlayQueueItemCommand.Execute(index);
        }
    }

    private void PlayerArtist_Click(object? sender, RoutedEventArgs e)
    {
        var track = _viewModel?.CurrentTrack;
        if (track == null) return;
        var name = track.Artists.Count > 0 ? track.Artists[0].Name : null;
        var id = track.Artists.Count > 0 ? track.Artists[0].Id : null;
        _viewModel!.OpenArtist(id, name);
    }

    private void QueueRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        if (sender is Button { Tag: SongItem song })
        {
            var index = _viewModel.QueueItems.IndexOf(song);
            if (index >= 0) _viewModel.RemoveQueueItemCommand.Execute(index);
        }
    }

    private void FullscreenUpNext_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { Tag: SongItem song })
            _viewModel?.PlayFullscreenUpNextCommand.Execute(song);
    }

    private void DetailsHint_Click(object? sender, RoutedEventArgs e) =>
        SmoothScroller.ScrollTo(FullscreenScroll, new Vector(0, FullscreenHero.Bounds.Height));

    private void UpdatePageBands()
    {
        var shown = Math.Clamp(FullscreenScroll.Offset.Y / 120, 0, 1);
        PageBandTop.Opacity = shown;
        PageFadeTop.Opacity = shown;
        PageBandBottom.Opacity = shown;
    }

    /// <summary>The page is gone by the top of the controls, however tall they are.</summary>
    private void PlacePageBand()
    {
        const double FadeHeight = 56;
        const double BelowControls = 26;

        var solid = FullscreenTransportFade.Bounds.Height + BelowControls;
        PageBandBottom.Height = FadeHeight + solid;
        if (PageBandBottom.Background is Avalonia.Media.LinearGradientBrush brush)
            brush.GradientStops[1].Offset = FadeHeight / (FadeHeight + solid);
    }

    private DispatcherTimer? _detailsHintBob;
    private bool _detailsHintLow;

    /// <summary>The hint's arrow nods slowly while the hint is on screen, so the eye finds it.</summary>
    private void UpdateDetailsHintBob()
    {
        var showing = Motion.Enabled && _viewModel is { IsFullscreenPlayer: true, ShowFullscreenControls: true, HasFullscreenDetails: true };
        if (!showing)
        {
            _detailsHintBob?.Stop();
            return;
        }

        if (_detailsHintBob == null)
        {
            DetailsHintChevron.Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(760),
                    Easing = new Avalonia.Animation.Easings.SineEaseInOut()
                }
            };
            _detailsHintBob = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _detailsHintBob.Tick += (_, _) =>
            {
                _detailsHintLow = !_detailsHintLow;
                DetailsHintChevron.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse(
                    _detailsHintLow ? "translateY(3px)" : "translateY(-1px)");
            };
        }
        _detailsHintBob.Start();
    }

    /// <summary>Tapping a timed lyric line jumps there.</summary>
    private void LyricLine_Tapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel == null || sender is not Control { DataContext: LyricLine { IsTimed: true } line }) return;
        var total = _viewModel.Duration.TotalSeconds;
        if (total <= 0) return;
        _viewModel.SeekFraction = Math.Clamp(line.Time.TotalSeconds / total, 0, 1);
        _viewModel.CommitSeekCommand.Execute(null);
    }

    private void SongMode_Click(object? sender, RoutedEventArgs e) => _viewModel?.SetVideoMode(false);

    private void VideoMode_Click(object? sender, RoutedEventArgs e) => _viewModel?.SetVideoMode(true);

    // ---- Sidebar ----

    private void SidebarLiked_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        if (_viewModel.IsFullscreenPlayer) _viewModel.CloseFullscreenPlayerCommand.Execute(null);
        _viewModel.NavigateToLibraryTab(0);
    }

    private void SidebarPlaylist_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PlaylistEntity playlist } && _viewModel != null)
            _ = _viewModel.OpenBrowseAsync(playlist.Id, playlist.Title, playlist.ThumbnailUrl, "Playlist");
    }

    private void SearchBox_GotFocus(object? sender, FocusChangedEventArgs e)
    {
        // The field is the way to the search page.
        if (_viewModel != null && _viewModel.CurrentView is not SearchViewModel)
            _viewModel.NavigateToSearchCommand.Execute(null);
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_viewModel != null && SearchBox.IsFocused)
            _viewModel.OnSearchTyped(SearchBox.Text ?? string.Empty);
    }

    private void ClearSearch_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        _viewModel.SearchQuery = string.Empty;
        _viewModel.SearchViewModel.Query = string.Empty;
        SearchBox.Focus();
    }

    // ---- Player bar ----

    private void PlayerMore_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control anchor && _viewModel != null)
            TrackMenu.ShowNowPlaying(anchor, _viewModel, () => MiniPlayer_Click(null, new RoutedEventArgs()),
                () => SleepCustom_Click(null, new RoutedEventArgs()));
    }

    /// <summary>The wheel over the volume changes the volume.</summary>
    private void Volume_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        _viewModel?.AdjustVolume(e.Delta.Y * 0.05);
        e.Handled = true;
    }

    // ---- Window buttons ----

    private void CaptionMinimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CaptionMaximize_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CaptionClose_Click(object? sender, RoutedEventArgs e) => Close();

    private void UpdateCaptionButtons()
    {
        MaximizeGlyph.Kind = WindowState == WindowState.Maximized ? IconKind.WindowRestore : IconKind.WindowMaximize;
        CaptionButtons.IsVisible = WindowState != WindowState.FullScreen;
    }

    // ---- Motion of the shell ----

    /// <summary>The soft pill slides to the active sidebar row.</summary>
    private void UpdateNavPill()
    {
        if (_viewModel == null) return;
        var row = _viewModel.IsHomeActive ? 0 : _viewModel.IsLibraryActive ? 1 : -1;
        NavPill.Opacity = row < 0 ? 0 : 1;
        if (row >= 0)
            NavPill.RenderTransform = TransformOperations.Parse(
                FormattableString.Invariant($"translateY({row * 42}px)"));
    }

    /// <summary>The full player's cover and title grow with the window.</summary>
    private void UpdateFullscreenScale()
    {
        var height = Bounds.Height > 0 ? Bounds.Height : Height;
        var side = Math.Clamp(height * 0.3, 176, 380);
        HeroCover.Width = side;
        HeroCover.Height = side;
        HeroTitle.FontSize = Math.Clamp(height * 0.044, 28, 54);
    }

    private int _flightGen;

    /// <summary>
    /// The cover flies between the player bar and the hero of the full player, so
    /// the two screens read as one object.
    /// </summary>
    private void FlyCover(bool opening)
    {
        var gen = ++_flightGen;
        HeroCover.Opacity = 1;
        FlyingCover.IsVisible = false;
        if (!Motion.Enabled || _viewModel is not { ArtworkImage: not null, IsVideoMode: false }) return;

        var bar = PlayerCover.TranslatePoint(default, Root);
        var hero = HeroCover.TranslatePoint(default, FullscreenRoot);
        if (bar is not { } barAt || hero is not { } heroAt || HeroCover.Bounds.Width <= 0) return;
        if (!opening && FullscreenScroll.Offset.Y > 40) return; // the hero has scrolled out of view

        var heroSize = HeroCover.Bounds.Width;
        var barScale = PlayerCover.Bounds.Width / heroSize;

        // The first operation applies first here (the reverse of CSS): scale, then move.
        string At(Point p, double scale) => FormattableString.Invariant(
            $"scale({scale}) translateX({p.X}px) translateY({p.Y}px)");
        var atBar = TransformOperations.Parse(At(barAt, barScale));
        var atHero = TransformOperations.Parse(At(heroAt, 1));

        FlyingCover.Transitions = null;
        FlyingCover.Width = heroSize;
        FlyingCover.Height = heroSize;
        FlyingCover.CornerRadius = new CornerRadius(18);
        FlyingCover.RenderTransform = opening ? atBar : atHero;
        FlyingCover.IsVisible = true;
        HeroCover.Opacity = 0;

        var duration = opening ? 460 : 380;
        FlyingCover.Transitions = new Avalonia.Animation.Transitions
        {
            new Avalonia.Animation.TransformOperationsTransition
            {
                Property = RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(duration),
                Easing = Motion.Emphasized
            }
        };

        Dispatcher.UIThread.Post(() =>
        {
            if (gen == _flightGen) FlyingCover.RenderTransform = opening ? atHero : atBar;
        }, DispatcherPriority.Background);

        DispatcherTimer.RunOnce(() =>
        {
            if (gen != _flightGen) return;
            FlyingCover.IsVisible = false;
            HeroCover.Opacity = 1;
        }, TimeSpan.FromMilliseconds(duration + 30));
    }

    private int _queueGen;

    /// <summary>The queue slides in from the right and back out again.</summary>
    private void UpdateQueuePanel()
    {
        if (_viewModel == null) return;
        var gen = ++_queueGen;
        if (_viewModel.ShowQueue)
        {
            QueuePanel.IsVisible = true;
            Motion.SlideIn(QueuePanel, 30, 0, 320);
            return;
        }

        QueuePanel.Opacity = 0;
        QueuePanel.RenderTransform = TransformOperations.Parse("translateX(30px) translateY(0px)");
        DispatcherTimer.RunOnce(() =>
        {
            if (gen == _queueGen) QueuePanel.IsVisible = false;
        }, TimeSpan.FromMilliseconds(Motion.Enabled ? 240 : 0));
    }

    private async void LikeCurrent_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.CurrentTrack is not { } track) return;

        var glyph = ReferenceEquals(sender, HeroLike) ? HeroLikeGlyph : LikeGlyph;
        Motion.Pop(glyph);
        if (!track.IsLiked) Motion.Burst(glyph);
        var wasLiked = track.IsLiked;
        await _viewModel.ToggleLikeAsync(track);

        // Just liked it for the first time → offer to file it into a playlist.
        if (!wasLiked && track.IsLiked && sender is Button button)
            SoundLibert.UI.Services.TrackMenu.ShowAddToPlaylist(button, track, _viewModel);
    }

    private int _heroFadeGen;
    private int _lyricFadeGen;
    private int _artFadeGen;
    private int _canvasFadeGen;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentView))
        {
            FadeContentIn();
        }
        if (e.PropertyName is nameof(MainWindowViewModel.IsHomeActive) or nameof(MainWindowViewModel.IsLibraryActive))
        {
            UpdateNavPill();
        }
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentTrack))
        {
            // The title and artist settle in with the new track.
            Dispatcher.UIThread.Post(() => Motion.SlideIn(PlayerInfo, 0, 8, 300));
        }
        if (e.PropertyName == nameof(MainWindowViewModel.ShowLyricsPanel) && _viewModel?.ShowLyricsPanel == true)
        {
            Dispatcher.UIThread.Post(() => Motion.SlideIn(LyricsPanel, 40, 0, 380));
        }
        if (e.PropertyName == nameof(MainWindowViewModel.ShowFullscreenControls) && _viewModel != null)
        {
            // Nothing on screen asks for the pointer: it steps aside too.
            FullscreenRoot.Cursor = _viewModel.ShowFullscreenControls ? Cursor.Default : new Cursor(StandardCursorType.None);
        }
        if (e.PropertyName is nameof(MainWindowViewModel.ShowFullscreenControls)
            or nameof(MainWindowViewModel.IsFullscreenPlayer)
            or nameof(MainWindowViewModel.HasFullscreenDetails))
        {
            UpdateDetailsHintBob();
        }
        if (e.PropertyName == nameof(MainWindowViewModel.IsFullscreenPlayer) && _viewModel != null)
        {
            var opening = _viewModel.IsFullscreenPlayer;
            Dispatcher.UIThread.Post(() => FlyCover(opening));
        }
        if (e.PropertyName == nameof(MainWindowViewModel.ArtworkImage))
        {
            // Player-bar cover crossfades in when a new track's art arrives.
            Dispatcher.UIThread.Post(FadePlayerArtIn);
        }
        if (e.PropertyName == nameof(MainWindowViewModel.ShowQueue))
        {
            Dispatcher.UIThread.Post(UpdateQueuePanel);
        }
        if (e.PropertyName == nameof(MainWindowViewModel.IsVideoMode))
        {
            // Song <-> Video switch: the video layer crossfades in.
            Dispatcher.UIThread.Post(FadeCanvasIn);
        }

        // Fullscreen details live BELOW the fold: always reopen / switch at the
        // top so the hero looks exactly as before until the user scrolls down.
        if (e.PropertyName == nameof(MainWindowViewModel.IsFullscreenPlayer) ||
            e.PropertyName == nameof(MainWindowViewModel.CurrentTrack))
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (FullscreenScroll != null)
                    FullscreenScroll.Offset = FullscreenScroll.Offset.WithY(0);
                FadeHeroIn();
                // Old canvas frame fades out; the new clip fades back in when
                // IsCanvasActive flips (below). If the canvas is ALREADY active
                // (e.g. re-entering fullscreen while a Video-mode clip keeps
                // playing), keep it visible instead of leaving it at 0.
                if (CanvasSurface != null)
                    CanvasSurface.Opacity = _viewModel is { IsCanvasActive: true, IsCanvasFrameReady: true } ? 1 : 0;
            });
        }
        // Fade the clip in on its FIRST FRAME (not when playback was merely
        // requested: that faded in an empty surface, then the picture popped).
        if (e.PropertyName is nameof(MainWindowViewModel.IsCanvasActive)
                or nameof(MainWindowViewModel.IsCanvasFrameReady))
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (CanvasSurface != null)
                    CanvasSurface.Opacity = _viewModel is { IsCanvasActive: true, IsCanvasFrameReady: true } ? 1 : 0;
            });
        }
        if (e.PropertyName == nameof(MainWindowViewModel.IsCanvasLoading))
            Dispatcher.UIThread.Post(UpdateCanvasLoader);
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentLyricText))
        {
            Dispatcher.UIThread.Post(FadeSingleLyricIn);
            return;
        }
        if (e.PropertyName == nameof(MainWindowViewModel.SingleLyricShown) && SingleLyricLine != null)
        {
            // The VM delays the collapse ~180 ms so this fade-out stays visible;
            // reappearing fades back in the same way. The opacity transition
            // animates both directions.
            var show = _viewModel?.SingleLyricShown == true;
            Dispatcher.UIThread.Post(() =>
            {
                if (SingleLyricLine != null) SingleLyricLine.Opacity = show ? 1 : 0;
            });
        }
        if (e.PropertyName != nameof(MainWindowViewModel.CurrentLyricIndex)) return;
        var index = _viewModel?.CurrentLyricIndex ?? -1;
        if (index >= 0)
            Dispatcher.UIThread.Post(() => CenterLyric(index), DispatcherPriority.Background);
    }

    /// <summary>
    /// Right-click anywhere on a song row or a collection card opens the same
    /// menu as its "…" button (tracks) or the shared card menu (albums,
    /// playlists, artists). Resolved from the DataContext under the pointer, so
    /// every list and grid gets it without per-view wiring.
    /// </summary>
    private void OnItemContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var host = _viewModel;
        if (host == null || e.Handled || e.Source is TextBox) return;

        for (var visual = e.Source as Visual; visual != null && visual != this; visual = visual.GetVisualParent())
        {
            if (visual is not Control control || control.DataContext is null) continue;
            if (ReferenceEquals(control.DataContext, host) || control.DataContext is ViewModelBase) return;

            switch (control.DataContext)
            {
                case SongItem song:
                    Func<SongItem, System.Threading.Tasks.Task>? remove = null;
                    if (song.IsLocalPlaylist && host.CurrentView is CollectionViewModel { IsLocalPlaylist: true } local)
                        remove = s => local.RemoveFromPlaylistCommand.ExecuteAsync(s);
                    SoundLibert.UI.Services.TrackMenu.Show(control, song, host, remove);
                    break;
                case ShelfCard { Item.Song: { } cardSong }:
                    SoundLibert.UI.Services.TrackMenu.Show(control, cardSong, host);
                    break;
                case ShelfCard card:
                    SoundLibert.UI.Services.CardMenu.Show(control, host, card.Item.Id, card.Title, card.ThumbnailUrl, card.Subtitle);
                    break;
                case AlbumItem album:
                    SoundLibert.UI.Services.CardMenu.Show(control, host, album.Id, album.Title, album.ThumbnailUrl, album.ArtistDisplay);
                    break;
                case PlaylistItem playlist:
                    SoundLibert.UI.Services.CardMenu.Show(control, host, playlist.Id, playlist.Title, playlist.ThumbnailUrl, null);
                    break;
                case ArtistItem artist when !string.IsNullOrWhiteSpace(artist.Id):
                    SoundLibert.UI.Services.CardMenu.Show(control, host, artist.Id, artist.Name, artist.ThumbnailUrl, "Artist");
                    break;
                case HomeArtistItem topArtist when !string.IsNullOrWhiteSpace(topArtist.Id):
                    SoundLibert.UI.Services.CardMenu.Show(control, host, topArtist.Id, topArtist.Name, topArtist.ThumbnailUrl, "Artist");
                    break;
                case DiscographyCard disc:
                    SoundLibert.UI.Services.CardMenu.Show(control, host, disc.Id, disc.Title, disc.CoverUrl, disc.Subtitle);
                    break;
                case LibraryCard { Playlist: { } entity }:
                    SoundLibert.UI.Services.CardMenu.Show(control, host, entity.Id, entity.Title, entity.ThumbnailUrl, null);
                    break;
                case PlaylistEntity entity:
                    SoundLibert.UI.Services.CardMenu.Show(control, host, entity.Id, entity.Title, entity.ThumbnailUrl, null);
                    break;
                default:
                    continue;
            }
            e.Handled = true;
            return;
        }
    }

    /// <summary>
    /// The hover ▶ on any album / playlist / artist card plays it right away
    /// (views only add the button; Home handles its own song cards first).
    /// </summary>
    private void OnCardPlayClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (e.Handled || _viewModel is not { } host) return;
        if (e.Source is not Button { Classes: var classes } button || !classes.Contains("cardPlay")) return;

        (string Id, string Title, string? Thumb)? target = button.DataContext switch
        {
            AlbumItem album => (album.Id, album.Title, album.ThumbnailUrl),
            PlaylistItem playlist => (playlist.Id, playlist.Title, playlist.ThumbnailUrl),
            DiscographyCard disc => (disc.Id, disc.Title, disc.CoverUrl),
            ArtistItem artist => (artist.Id, artist.Name, artist.ThumbnailUrl),
            LibraryCard { Playlist: { } entity } => (entity.Id, entity.Title, entity.ThumbnailUrl),
            _ => null
        };
        if (target is not { } t || string.IsNullOrWhiteSpace(t.Id)) return;
        e.Handled = true;
        _ = host.PlayCollectionAsync(t.Id, t.Title, t.Thumb);
    }

    private int _lastNavDepth;

    /// <summary>
    /// Page entrance with a direction: going deeper comes in from the right, going
    /// back from the left, switching section rises gently.
    /// </summary>
    private void FadeContentIn()
    {
        if (ContentHost == null || _viewModel == null) return;
        var depth = _viewModel.NavigationDepth;
        var cameBack = depth < _lastNavDepth;
        var (x, y) = depth > _lastNavDepth ? (34, 0) : cameBack ? (-34, 0) : (0, 12);
        _lastNavDepth = depth;
        Motion.SlideIn(ContentHost, x, y, 340);

        var page = _viewModel.CurrentView;
        if (!cameBack)
        {
            // Switching section empties the way back; a new page starts at the top.
            if (depth == 0) _scrollMemory.Clear();
            else _scrollMemory.Remove(page);
            return;
        }

        // Back lands where you left the page. Its rows may still be arriving, so
        // the position is asked for again a moment later.
        if (_scrollMemory.TryGetValue(page, out var offset) && offset > 1)
        {
            void Restore()
            {
                if (ReferenceEquals(_viewModel?.CurrentView, page))
                    ContentHost.GetVisualDescendants().OfType<ScrollViewer>()
                        .FirstOrDefault(s => s.VerticalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled)
                        ?.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(0, offset));
            }

            DispatcherTimer.RunOnce(Restore, TimeSpan.FromMilliseconds(40));
            DispatcherTimer.RunOnce(Restore, TimeSpan.FromMilliseconds(320));
        }
    }

    private readonly Dictionary<ViewModelBase, double> _scrollMemory = new();

    /// <summary>Player-bar cover entrance: fades in when new artwork arrives.</summary>
    private void FadePlayerArtIn()
    {
        if (PlayerArt == null) return;
        var gen = ++_artFadeGen;
        var transitions = PlayerArt.Transitions;
        PlayerArt.Transitions = null;
        PlayerArt.Opacity = 0;
        PlayerArt.Transitions = transitions;
        Dispatcher.UIThread.Post(() =>
        {
            if (gen == _artFadeGen) PlayerArt.Opacity = 1;
        }, DispatcherPriority.Background);
    }

    private int _canvasLoaderGen;

    /// <summary>Loader fades in/out via its Opacity transition; IsVisible is the final state.</summary>
    private void UpdateCanvasLoader()
    {
        if (CanvasLoader == null || _viewModel == null) return;
        var gen = ++_canvasLoaderGen;
        if (_viewModel.IsCanvasLoading)
        {
            CanvasLoader.IsVisible = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (gen == _canvasLoaderGen) CanvasLoader.Opacity = 1;
            }, DispatcherPriority.Background);
            return;
        }
        CanvasLoader.Opacity = 0;
        DispatcherTimer.RunOnce(() =>
        {
            if (gen == _canvasLoaderGen) CanvasLoader.IsVisible = false;
        }, TimeSpan.FromMilliseconds(480));
    }

    /// <summary>Song ↔ Video switch: the video layer crossfades in.</summary>
    private void FadeCanvasIn()
    {
        if (CanvasSurface == null) return;
        var gen = ++_canvasFadeGen;
        var transitions = CanvasSurface.Transitions;
        CanvasSurface.Transitions = null;
        CanvasSurface.Opacity = 0;
        CanvasSurface.Transitions = transitions;
        Dispatcher.UIThread.Post(() =>
        {
            if (gen == _canvasFadeGen) CanvasSurface.Opacity = 1;
        }, DispatcherPriority.Background);
    }

    /// <summary>Track-change entrance: the (already swapped) hero fades in.</summary>
    private void FadeHeroIn()
    {
        if (FullscreenHero == null) return;
        var gen = ++_heroFadeGen;
        var transitions = FullscreenHero.Transitions;
        FullscreenHero.Transitions = null;
        FullscreenHero.Opacity = 0;
        FullscreenHero.Transitions = transitions;
        Dispatcher.UIThread.Post(() =>
        {
            if (gen == _heroFadeGen && _viewModel?.CurrentTrack != null)
                FullscreenHero.Opacity = 1;
        }, DispatcherPriority.Background);
    }

    /// <summary>Single lyric line dip-fade so line switches don't snap.</summary>
    private void FadeSingleLyricIn()
    {
        if (SingleLyricLine == null || _viewModel?.ShowSingleLyric != true) return;
        var gen = ++_lyricFadeGen;
        var transitions = SingleLyricLine.Transitions;
        SingleLyricLine.Transitions = null;
        SingleLyricLine.Opacity = 0;
        SingleLyricLine.Transitions = transitions;
        Dispatcher.UIThread.Post(() =>
        {
            if (gen == _lyricFadeGen) SingleLyricLine.Opacity = 1;
        }, DispatcherPriority.Background);
    }

    private void CenterLyric(int index)
    {
        if (LyricsList == null || index < 0 || index >= LyricsList.ItemCount) return;
        LyricsList.ScrollIntoView(index);

        if (LyricsList.ContainerFromIndex(index) is not Control container) return;
        var sv = LyricsList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (sv == null) return;

        var top = container.TranslatePoint(new Point(0, 0), sv);
        if (top == null) return;

        var targetY = sv.Offset.Y + top.Value.Y - (sv.Viewport.Height - container.Bounds.Height) / 2;
        SmoothScroller.ScrollTo(sv, sv.Offset.WithY(Math.Max(0, targetY)));
    }

    private MiniPlayerWindow? _mini;

    private async void SleepCustom_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        var input = await PromptWindow.ShowAsync(this, "Sleep timer", placeholder: "Minutes (1–480)");
        if (int.TryParse(input, out var minutes) && minutes >= 1 && minutes <= 480)
            _viewModel.StartSleepTimerCommand.Execute(minutes.ToString());
    }

    private void MiniPlayer_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;

        if (_mini is { IsVisible: true })
        {
            _mini.Activate();
            return;
        }

        // A closed window cannot be re-shown, so always create a fresh one.
        _mini = new MiniPlayerWindow { DataContext = _viewModel };
        _mini.Position = new PixelPoint(Position.X + 60, Position.Y + 60);
        _mini.Closed += OnMiniClosed;
        _mini.Show();
        Hide();
    }

    private void OnMiniClosed(object? sender, System.EventArgs e)
    {
        if (_mini != null) _mini.Closed -= OnMiniClosed;
        _mini = null;
        Show();
    }



    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Root.Focus();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && _viewModel != null)
        {
            Root.Focus();
            _viewModel.NavigateToSearchCommand.Execute(null);
            if (_viewModel.CurrentView is SearchViewModel searchVm)
            {
                searchVm.Query = _viewModel.SearchQuery;
                searchVm.SearchCommand.Execute(null);
            }
        }
    }

    // ---- Keyboard shortcuts (ignored while typing in the search box) ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var vm = _viewModel;
        if (vm == null) return;

        // F11 toggles true (borderless) window fullscreen — works even while typing.
        if (e.Key == Key.F11)
        {
            ToggleWindowFullscreen();
            e.Handled = true;
            return;
        }

        // Escape closes the fullscreen player, even while typing.
        if (e.Key == Key.Escape && vm.IsFullscreenPlayer)
        {
            vm.CloseFullscreenPlayerCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Escape also leaves window fullscreen when the player overlay is closed.
        if (e.Key == Key.Escape && WindowState == WindowState.FullScreen)
        {
            ToggleWindowFullscreen();
            e.Handled = true;
            return;
        }

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        // Ctrl+K / Ctrl+F jump to search from anywhere (also out of fullscreen).
        if (ctrl && e.Key is Key.K or Key.F && SearchBox != null)
        {
            if (vm.IsFullscreenPlayer) vm.CloseFullscreenPlayerCommand.Execute(null);
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is TextBox) { base.OnKeyDown(e); return; }

        switch (e.Key)
        {
            case Key.Left when ctrl:
                vm.PreviousTrackCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right when ctrl:
                vm.NextTrackCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Left:
                _ = vm.SeekByAsync(-5);
                e.Handled = true;
                break;
            case Key.Right:
                _ = vm.SeekByAsync(5);
                e.Handled = true;
                break;
            case Key.Up:
                vm.AdjustVolume(0.05);
                e.Handled = true;
                break;
            case Key.Down:
                vm.AdjustVolume(-0.05);
                e.Handled = true;
                break;
            case Key.M:
                vm.ToggleMuteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F when !ctrl:
                vm.ToggleFullscreenPlayerCommand.Execute(null);
                e.Handled = true;
                break;
        }

        base.OnKeyDown(e);
    }

    // ---- Window fullscreen (F11) ----

    private WindowState _preFullscreenState = WindowState.Normal;

    /// <summary>Toggles true (borderless) window fullscreen, remembering the prior state.</summary>
    private void ToggleWindowFullscreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _preFullscreenState == WindowState.FullScreen
                ? WindowState.Normal
                : _preFullscreenState;
        }
        else
        {
            _preFullscreenState = WindowState;
            WindowState = WindowState.FullScreen;
        }
    }

    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        // QA-only: with SL_SHOT=1 render each screen to PNG under %TEMP%\sl_shots
        // and exit; SL_SHOT=lyrics runs only the fast lyrics check,
        // SL_SHOT=report only the feedback UI (About + "Report a bug"),
        // SL_SHOT=icons only the sheet of all icons, SL_SHOT=rich only the screens for finding music.
        var shot = Environment.GetEnvironmentVariable("SL_SHOT");
        if (shot == "splash")
        {
            // QA: the opening plays and hands over; then a moment of the app, and out.
            ContentShown += () => DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(4));
            return;
        }
        if (shot == "welcome")
        {
            _ = WelcomeTourAsync();
            return;
        }
        if (shot is "1" or "lyrics" or "report" or "icons" or "rich" or "fps" or "mem" or "clip" or "readme")
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            System.Diagnostics.Trace.WriteLine($"[QA] the window opened {(DateTime.Now - process.StartTime).TotalMilliseconds:0} ms after the start");
            _ = CaptureScreensAsync();
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Close-to-tray: keep the app (and playback) alive in the tray unless
        // the user explicitly chose Exit from the tray menu.
        if ((AppSettings?.CloseToTray ?? false) && !_exitRequested)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // Persist the in-progress listen and the session (queue + position).
        _viewModel?.EndPlaybackSession();
        _viewModel?.SaveSession();
        _viewModel?.ShutdownPlayers();
        if (_tray != null) _tray.IsVisible = false;
        // The mini player cannot bring a closed window back, and left open it
        // would keep the process alive with its players already shut down.
        if (_mini != null)
        {
            _mini.Closed -= OnMiniClosed;
            _mini.Close();
            _mini = null;
        }
        base.OnClosing(e);
    }

    private async System.Threading.Tasks.Task CaptureScreensAsync()
    {
        // Fast, focused mode for iterating on the lyrics: play one track, open
        // the fullscreen lyrics, capture a couple of frames and exit.
        if (Environment.GetEnvironmentVariable("SL_SHOT") == "lyrics")
        {
            await CaptureLyricsOnlyAsync();
            return;
        }
        if (Environment.GetEnvironmentVariable("SL_SHOT") == "report")
        {
            await CaptureReportOnlyAsync();
            return;
        }
        if (Environment.GetEnvironmentVariable("SL_SHOT") == "readme")
        {
            await CaptureForReadmeAsync();
            return;
        }
        if (Environment.GetEnvironmentVariable("SL_SHOT") == "clip")
        {
            await WatchClipAsync();
            return;
        }
        if (Environment.GetEnvironmentVariable("SL_SHOT") == "mem")
        {
            await MeasureMemoryAsync();
            return;
        }
        if (Environment.GetEnvironmentVariable("SL_SHOT") == "fps")
        {
            await MeasureSmoothnessAsync();
            return;
        }
        if (Environment.GetEnvironmentVariable("SL_SHOT") == "rich")
        {
            await CaptureDiscoveryOnlyAsync();
            return;
        }
        if (Environment.GetEnvironmentVariable("SL_SHOT") == "icons")
        {
            await CaptureIconSheetAsync();
            return;
        }

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sl_shots");
        System.IO.Directory.CreateDirectory(dir);

        // QA playback is deliberately QUIET: somebody may be listening to the real
        // player next to this run.
        if (_viewModel != null) _viewModel.Volume = 0.02;

        await System.Threading.Tasks.Task.Delay(6000);
        await CaptureAsync(System.IO.Path.Combine(dir, "home.png"));

        // Home discovery shelves and horizontal video rails.
        this.GetVisualDescendants().OfType<HomeView>().FirstOrDefault()?.ScrollToOffset(760);
        await System.Threading.Tasks.Task.Delay(3000);
        await CaptureAsync(System.IO.Path.Combine(dir, "home_taste.png"));

        // Home recommendation shelves: scroll down to prove they render.
        this.GetVisualDescendants().OfType<HomeView>().FirstOrDefault()?.ScrollToBottom();
        await System.Threading.Tasks.Task.Delay(600);
        await CaptureAsync(System.IO.Path.Combine(dir, "home_shelves.png"));

        if (_viewModel != null)
        {
            _viewModel.NavigateToSearchCommand.Execute(null);
            var qaCalmSong = "soft piano calm";
            _viewModel.SearchQuery = qaCalmSong;
            _viewModel.SearchViewModel.Query = qaCalmSong;
            _viewModel.SearchViewModel.SearchCommand.Execute(null);
            await System.Threading.Tasks.Task.Delay(5000);
            await CaptureAsync(System.IO.Path.Combine(dir, "search.png"));

            _viewModel.NavigateToLibraryCommand.Execute(null);
            await System.Threading.Tasks.Task.Delay(2000);
            await CaptureAsync(System.IO.Path.Combine(dir, "library.png"));

            // Library drill-in with the text filter + sort applied.
            var lib = _viewModel.LibraryViewModel;
            lib.OpenSection(SoundLibert.UI.ViewModels.LibrarySection.Liked);
            await System.Threading.Tasks.Task.Delay(800);
            var firstLiked = lib.CollectionTracks.FirstOrDefault();
            var rowsBefore = lib.CollectionTracks.Count;
            if (firstLiked != null)
                lib.CollectionFilter = firstLiked.Title.Split(' ')[0];
            lib.CollectionSortIndex = 1;
            await System.Threading.Tasks.Task.Delay(800);
            System.Diagnostics.Trace.WriteLine(
                $"[QA] library filter '{lib.CollectionFilter}': {rowsBefore} -> {lib.CollectionTracks.Count} rows");
            await CaptureAsync(System.IO.Path.Combine(dir, "library_filter.png"));
            lib.CloseCollectionCommand.Execute(null);

            // Play the first result so the Dynamic Now Playing (artwork +
            // album-colour ambient tint) is visible in the capture.
            _viewModel.NavigateToSearchCommand.Execute(null);
            if (_viewModel.SearchViewModel.Songs.Count > 0)
            {
                // The second result: a colourful cover shows the ambient better.
                _viewModel.SearchViewModel.PlaySongCommand.Execute(
                    _viewModel.SearchViewModel.Songs[Math.Min(1, _viewModel.SearchViewModel.Songs.Count - 1)]);
                await System.Threading.Tasks.Task.Delay(9000);
                await CaptureAsync(System.IO.Path.Combine(dir, "playing.png"));

                // Hover states: a row, the player bar, and the bar's menu.
                var row = this.GetVisualDescendants().OfType<SongRow>().Skip(1).FirstOrDefault();
                var rowSurface = row?.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("hoverHost"));
                if (rowSurface != null) Hover(rowSurface, true);
                Hover(PlayerBar, true);
                await System.Threading.Tasks.Task.Delay(500);
                await CaptureAsync(System.IO.Path.Combine(dir, "hover_row_player.png"));
                if (rowSurface != null) Hover(rowSurface, false);
                Hover(PlayerBar, false);

                var menu = TrackMenu.ShowNowPlaying(PlayerBar, _viewModel, () => { }, () => { });
                await System.Threading.Tasks.Task.Delay(500);
                if (menu.Items.Count > 0 && menu.Items[0] is Visual menuItem && TopLevel.GetTopLevel(menuItem) is { } popup)
                    await CaptureVisualAsync(popup, (int)popup.ClientSize.Width, (int)popup.ClientSize.Height,
                        System.IO.Path.Combine(dir, "menu_now_playing.png"));
                menu.Hide();

                // Live "Now Playing" tile on the Start board (mirrors the track).
                _viewModel.NavigateToHomeCommand.Execute(null);
                await System.Threading.Tasks.Task.Delay(1500);
                await CaptureAsync(System.IO.Path.Combine(dir, "home_live.png"));

                // A shelf and a card under the pointer.
                var rail = this.GetVisualDescendants().OfType<Rail>().FirstOrDefault(r => r.Bounds.Height > 100);
                var card = rail?.GetVisualDescendants().OfType<CoverCard>().Skip(1).FirstOrDefault();
                if (rail != null) Hover(rail, true);
                if (card != null) Hover(card, true);
                await System.Threading.Tasks.Task.Delay(800);
                await CaptureAsync(System.IO.Path.Combine(dir, "hover_card.png"));
                if (rail != null) Hover(rail, false);
                if (card != null) Hover(card, false);

                // Seek self-test: jump to 50% and confirm the player reports it.
                var before = _viewModel.Position;
                _viewModel.SeekFraction = 0.5;
                _viewModel.CommitSeekCommand.Execute(null);
                await System.Threading.Tasks.Task.Delay(1500);
                System.Diagnostics.Debug.WriteLine(
                    $"[SeekTest] before={before:mm\\:ss} after={_viewModel.Position:mm\\:ss} " +
                    $"fraction={_viewModel.SeekFraction:F2} duration={_viewModel.Duration:mm\\:ss}");

                _viewModel.ToggleFullscreenPlayerCommand.Execute(null);
                await System.Threading.Tasks.Task.Delay(1500);
                await CaptureAsync(System.IO.Path.Combine(dir, "fullscreen.png"));

                // Canvas auto-loads with the fullscreen player (short looping muted video).
                await System.Threading.Tasks.Task.Delay(14000);
                await CaptureAsync(System.IO.Path.Combine(dir, "canvas.png"));
                System.Diagnostics.Debug.WriteLine($"[QA] canvas active: {_viewModel.IsCanvasActive} · {_viewModel.CanvasDebugState}");

                _viewModel.ShowLyrics = true;
                await System.Threading.Tasks.Task.Delay(6000);
                await CaptureAsync(System.IO.Path.Combine(dir, "lyrics.png"));

                // Queue panel (populated from the played queue).
                _viewModel.CloseFullscreenPlayerCommand.Execute(null);
                _viewModel.ShowLyrics = false;
                _viewModel.ToggleQueueCommand.Execute(null);
                await System.Threading.Tasks.Task.Delay(1500);
                await CaptureAsync(System.IO.Path.Combine(dir, "queue.png"));

                // Queue reorder self-test (same call the drag handle makes).
                if (_viewModel.QueueItems.Count > 4)
                {
                    var movingItem = _viewModel.QueueItems[1];
                    var moving = movingItem.Title;
                    var current = _viewModel.CurrentTrack?.Title;
                    _viewModel.MoveQueueItem(1, 3);
                    await System.Threading.Tasks.Task.Delay(300);
                    System.Diagnostics.Trace.WriteLine(
                        $"[QA] queue move: '{moving}' now at {_viewModel.QueueItems.IndexOf(movingItem)}, " +
                        $"current still '{_viewModel.CurrentTrack?.Title}' (was '{current}') at {_viewModel.QueueCurrentIndex}");
                }

                // Close the queue panel.
                _viewModel.ToggleQueueCommand.Execute(null);
            }

            // Album navigation (CollectionView). The queue must be closed here:
            // an unconditional toggle re-opened it over every later capture.
            if (_viewModel.ShowQueue) _viewModel.ToggleQueueCommand.Execute(null);
            _viewModel.NavigateToSearchCommand.Execute(null);
            _viewModel.SearchViewModel.Query = "smooth chill jazz";
            _viewModel.SearchViewModel.SelectedFilter = SoundLibert.Domain.Enums.SearchFilter.Albums;
            _viewModel.SearchViewModel.SearchCommand.Execute(null);
            await System.Threading.Tasks.Task.Delay(6000);
            if (_viewModel.SearchViewModel.Albums.Count > 0)
            {
                var album = _viewModel.SearchViewModel.Albums[0];
                await _viewModel.OpenBrowseAsync(album.Id, album.Title, album.ThumbnailUrl, album.ArtistDisplay);
                await System.Threading.Tasks.Task.Delay(6000);
                await CaptureAsync(System.IO.Path.Combine(dir, "collection.png"));
                _viewModel.GoBackCommand.Execute(null);
            }

            // Artist page (real YouTube Music artist browse).
            _viewModel.NavigateToSearchCommand.Execute(null);
            _viewModel.SearchViewModel.Query = "spa relaxation music";
            _viewModel.SearchViewModel.SelectedFilter = SoundLibert.Domain.Enums.SearchFilter.Artists;
            _viewModel.SearchViewModel.SearchCommand.Execute(null);
            await System.Threading.Tasks.Task.Delay(5000);
            if (_viewModel.SearchViewModel.Artists.Count > 0)
            {
                var artist = _viewModel.SearchViewModel.Artists[0];
                await _viewModel.OpenBrowseAsync(artist.Id, artist.Name, artist.ThumbnailUrl, "Artist");
                await System.Threading.Tasks.Task.Delay(6000);
                await CaptureAsync(System.IO.Path.Combine(dir, "artist.png"));

                // Full discography + channel videos pages (shared card styles).
                if (_viewModel.CurrentView is SoundLibert.UI.ViewModels.ArtistViewModel artistVm)
                {
                    if (artistVm.HasDiscography)
                    {
                        _viewModel.OpenArtistDiscography(artistVm);
                        await System.Threading.Tasks.Task.Delay(2500);
                        await CaptureAsync(System.IO.Path.Combine(dir, "discography.png"));
                        _viewModel.GoBackCommand.Execute(null);
                        await System.Threading.Tasks.Task.Delay(800);
                    }
                    await _viewModel.OpenArtistVideosAsync(artist.Id, artist.Name, artist.ThumbnailUrl);
                    await System.Threading.Tasks.Task.Delay(5000);
                    await CaptureAsync(System.IO.Path.Combine(dir, "artist_videos.png"));
                    await CheckVideoPageGrowsAsync("artist");
                    await CaptureAsync(System.IO.Path.Combine(dir, "artist_videos_more.png"));
                    _viewModel.GoBackCommand.Execute(null);
                    await System.Threading.Tasks.Task.Delay(800);
                }
                _viewModel.GoBackCommand.Execute(null);
            }

            // Playlist navigation (verifies the VL-prefix fix).
            _viewModel.NavigateToSearchCommand.Execute(null);
            _viewModel.SearchViewModel.Query = "quiet evening acoustic";
            _viewModel.SearchViewModel.SelectedFilter = SoundLibert.Domain.Enums.SearchFilter.CommunityPlaylists;
            _viewModel.SearchViewModel.SearchCommand.Execute(null);
            await System.Threading.Tasks.Task.Delay(6000);
            if (_viewModel.SearchViewModel.Playlists.Count > 0)
            {
                var playlist = _viewModel.SearchViewModel.Playlists[0];
                await _viewModel.OpenBrowseAsync(playlist.Id, playlist.Title, playlist.ThumbnailUrl, null);
                await System.Threading.Tasks.Task.Delay(6000);
                await CaptureAsync(System.IO.Path.Combine(dir, "playlist.png"));
                _viewModel.GoBackCommand.Execute(null);
            }

            // User playlists (G3): create → add the current track → view → delete.
            if (_viewModel.CurrentTrack is { } playlistSong)
            {
                var created = await _viewModel.CreatePlaylistAsync("[QA] Local playlist");
                if (created != null)
                {
                    await _viewModel.AddToPlaylistAsync(created, playlistSong);
                    // Three more tracks so the 2×2 cover mosaic shows.
                    foreach (var extra in _viewModel.QueueItems.Where(s => s.Id != playlistSong.Id).Take(3).ToList())
                        await _viewModel.AddToPlaylistAsync(created, extra);

                    _viewModel.NavigateToLibraryCommand.Execute(null);
                    await System.Threading.Tasks.Task.Delay(1500);
                    await CaptureAsync(System.IO.Path.Combine(dir, "library_playlists.png"));

                    await _viewModel.OpenBrowseAsync(created.Id, created.Title, created.ThumbnailUrl, "Playlist");
                    await System.Threading.Tasks.Task.Delay(1500);
                    await CaptureAsync(System.IO.Path.Combine(dir, "playlist_local.png"));

                    // Remove the track and confirm the list becomes empty.
                    if (_viewModel.CurrentView is SoundLibert.UI.ViewModels.CollectionViewModel localVm &&
                        localVm.Songs.Count > 0)
                    {
                        localVm.RemoveFromPlaylistCommand.Execute(localVm.Songs[0]);
                        await System.Threading.Tasks.Task.Delay(1200);
                        System.Diagnostics.Debug.WriteLine(
                            $"[QA] local playlist songs after remove: {localVm.Songs.Count}");
                        await CaptureAsync(System.IO.Path.Combine(dir, "playlist_local_removed.png"));
                    }

                    await _viewModel.DeletePlaylistAsync(created);
                    await System.Threading.Tasks.Task.Delay(500);
                }
            }

            _viewModel.EndPlaybackSession();

            // Settings.
            _viewModel.NavigateToSettingsCommand.Execute(null);
            await System.Threading.Tasks.Task.Delay(1200);
            await CaptureAsync(System.IO.Path.Combine(dir, "settings.png"));
            var settingTabs = this.GetVisualDescendants().OfType<SettingsView>().FirstOrDefault()?
                .GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
            if (settingTabs != null)
            {
                for (var i = 1; i < settingTabs.ItemCount; i++)
                {
                    settingTabs.SelectedIndex = i;
                    await System.Threading.Tasks.Task.Delay(500);
                    await CaptureAsync(System.IO.Path.Combine(dir, $"settings_{i}.png"));
                }
            }

            _viewModel.ShowShortcuts = true;
            await System.Threading.Tasks.Task.Delay(300);
            await CaptureAsync(System.IO.Path.Combine(dir, "shortcuts.png"));
            _viewModel.ShowShortcuts = false;

            // The smallest window and a large one.
            var (normalWidth, normalHeight) = (Width, Height);
            _viewModel.NavigateToHomeCommand.Execute(null);
            Width = MinWidth;
            Height = MinHeight;
            await System.Threading.Tasks.Task.Delay(1800);
            await CaptureAsync(System.IO.Path.Combine(dir, "size_min.png"));
            Width = 1800;
            Height = 1040;
            await System.Threading.Tasks.Task.Delay(1800);
            await CaptureAsync(System.IO.Path.Combine(dir, "size_large.png"));
            _viewModel.ToggleFullscreenPlayerCommand.Execute(null);
            await System.Threading.Tasks.Task.Delay(1800);
            await CaptureAsync(System.IO.Path.Combine(dir, "size_large_fullscreen.png"));
            _viewModel.CloseFullscreenPlayerCommand.Execute(null);
            Width = normalWidth;
            Height = normalHeight;
            await System.Threading.Tasks.Task.Delay(800);
            WindowState = WindowState.Maximized;
            await System.Threading.Tasks.Task.Delay(1500);
            System.Diagnostics.Trace.WriteLine($"[QA] maximised: client {ClientSize}, off-screen margin {OffScreenMargin}, root margin {Root.Margin}");
            await CaptureAsync(System.IO.Path.Combine(dir, "size_maximized.png"));
            await System.Threading.Tasks.Task.Delay(13000);
            WindowState = WindowState.Normal;
            await System.Threading.Tasks.Task.Delay(800);

            // Video mode through LibVLC (http access → demux → decode → vmem):
            // catches a broken or over-pruned VLC that the song-only steps miss.
            await _viewModel.PlayVideoCardAsync(new SoundLibert.UI.ViewModels.VideoCard
            {
                Id = "7wtfhZwyrcc",
                Title = "Believer",
                Subtitle = "Imagine Dragons"
            });
            await System.Threading.Tasks.Task.Delay(14000);
            System.Diagnostics.Trace.WriteLine(
                $"[QA] video mode: frame={_viewModel.IsCanvasFrameReady} active={_viewModel.IsCanvasActive} · {_viewModel.CanvasDebugState}");
            await CaptureAsync(System.IO.Path.Combine(dir, "video.png"));
            _viewModel.CloseFullscreenPlayerCommand.Execute(null);

            // Lyrics with a song that has them.
            _viewModel.NavigateToSearchCommand.Execute(null);
            _viewModel.SearchViewModel.SelectedFilter = SoundLibert.Domain.Enums.SearchFilter.Songs;
            _viewModel.SearchViewModel.Query = "imagine dragons believer";
            _viewModel.SearchViewModel.SearchCommand.Execute(null);
            await System.Threading.Tasks.Task.Delay(5000);
            if (_viewModel.SearchViewModel.Songs.Count > 0)
            {
                _viewModel.SearchViewModel.PlaySongCommand.Execute(_viewModel.SearchViewModel.Songs[0]);
                await System.Threading.Tasks.Task.Delay(5000);
                _viewModel.SeekFraction = 0.2;
                _viewModel.CommitSeekCommand.Execute(null);
                _viewModel.ToggleFullscreenPlayerCommand.Execute(null);
                await System.Threading.Tasks.Task.Delay(3000);
                await CaptureAsync(System.IO.Path.Combine(dir, "fullscreen_lyric_line.png"));
                _viewModel.ShowLyrics = true;
                await System.Threading.Tasks.Task.Delay(4000);
                await CaptureAsync(System.IO.Path.Combine(dir, "lyrics_real.png"));
                _viewModel.ShowLyrics = false;
                _viewModel.CloseFullscreenPlayerCommand.Execute(null);
            }

            await CaptureDiscoveryScreensAsync(dir);

            // Login window (QA: reproduces WebView2 faults into the log).
            try
            {
                var login = new LoginWindow();
                login.Closed += (_, _) => System.Diagnostics.Debug.WriteLine("[QA] LoginWindow closed");
                login.Show();
                await System.Threading.Tasks.Task.Delay(8000);
                login.Close();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[QA] LoginWindow failed: {ex}");
            }

            // Mini player (QA: verify the seek bar spans the full width).
            try
            {
                var mini = new MiniPlayerWindow { DataContext = _viewModel };
                mini.Show();
                await System.Threading.Tasks.Task.Delay(1500);
                if (mini.Content is Visual miniContent)
                {
                    await CaptureVisualAsync(miniContent,
                        (int)mini.ClientSize.Width, (int)mini.ClientSize.Height,
                        System.IO.Path.Combine(dir, "mini.png"));
                }
                mini.Close();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[QA] MiniPlayer failed: {ex}");
            }

            // Playlist-name prompt (QA: verifies the modal XAML loads).
            try
            {
                var prompt = new PromptWindow();
                prompt.Show();
                await System.Threading.Tasks.Task.Delay(800);
                await CaptureVisualAsync(prompt, (int)prompt.ClientSize.Width, (int)prompt.ClientSize.Height,
                    System.IO.Path.Combine(dir, "prompt.png"));
                prompt.Close();
                System.Diagnostics.Debug.WriteLine("[QA] PromptWindow shown/closed");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[QA] PromptWindow failed: {ex}");
            }
        }

        // QA ran quiet (low volume) so nothing blasts out of the speaker;
        // restore the normal level before exiting so settings.json keeps it.
        if (_viewModel != null) _viewModel.Volume = 1.0;

        Dispatcher.UIThread.Post(Close);
    }

    /// <summary>
    /// Fast QA path (<c>SL_SHOT=rich</c>): the first run, then a listener with some taste
    /// (liked songs count as seeds) and every screen for finding music.
    /// </summary>
    private async System.Threading.Tasks.Task CaptureDiscoveryOnlyAsync()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sl_shots");
        System.IO.Directory.CreateDirectory(dir);
        if (_viewModel == null) { Dispatcher.UIThread.Post(Close); return; }

        // Somebody may be listening to the real player next to this run.
        _viewModel.Volume = 0.02;

        await System.Threading.Tasks.Task.Delay(12000);
        await CaptureAsync(System.IO.Path.Combine(dir, "home_first_run.png"));
        this.GetVisualDescendants().OfType<HomeView>().FirstOrDefault()?.ScrollToOffset(760);
        await System.Threading.Tasks.Task.Delay(1500);
        await CaptureAsync(System.IO.Path.Combine(dir, "home_first_run_2.png"));
        this.GetVisualDescendants().OfType<HomeView>().FirstOrDefault()?.ScrollToOffset(0);

        _viewModel.NavigateToSearchCommand.Execute(null);
        _viewModel.SearchViewModel.SelectedFilter = SoundLibert.Domain.Enums.SearchFilter.Songs;
        foreach (var query in new[] { "okean elzy", "coldplay yellow", "the weeknd blinding lights", "skryabin", "imagine dragons believer" })
        {
            _viewModel.SearchViewModel.Query = query;
            _viewModel.SearchViewModel.SearchCommand.Execute(null);
            await System.Threading.Tasks.Task.Delay(3500);
            if (_viewModel.SearchViewModel.Songs.FirstOrDefault() is not { } song) continue;
            await _viewModel.ToggleLikeAsync(song);
            _viewModel.SearchViewModel.PlaySongCommand.Execute(song);
            await System.Threading.Tasks.Task.Delay(6500);
        }

        await CaptureDiscoveryScreensAsync(dir);

        _viewModel.EndPlaybackSession();
        Dispatcher.UIThread.Post(Close);
    }

    /// <summary>QA: how many frames the window manages, while <paramref name="during"/> runs a few times a second.</summary>
    private async System.Threading.Tasks.Task<double> MeasureFramesAsync(string what, Action? during = null)
    {
        const double Seconds = 3;
        var intervals = new List<double>(512);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var last = clock.Elapsed.TotalMilliseconds;
        var running = true;
        void Frame(TimeSpan _)
        {
            var now = clock.Elapsed.TotalMilliseconds;
            intervals.Add(now - last);
            last = now;
            if (running) RequestAnimationFrame(Frame);
        }
        RequestAnimationFrame(Frame);

        while (clock.Elapsed.TotalSeconds < Seconds)
        {
            during?.Invoke();
            await System.Threading.Tasks.Task.Delay(120);
        }
        running = false;

        // The average hides what the eye sees: one long frame is a jerk.
        intervals.Sort();
        var perSecond = intervals.Count / clock.Elapsed.TotalSeconds;
        var typical = intervals.Count > 0 ? intervals[(int)(intervals.Count * 0.95)] : 0;
        var worst = intervals.Count > 0 ? intervals[^1] : 0;
        System.Diagnostics.Trace.WriteLine(
            $"[QA] frames per second, {what}: {perSecond:0} (95% of frames within {typical:0} ms, " +
            $"worst {worst:0} ms, {intervals.Count(i => i > 50)} over 50 ms)");
        return perSecond;
    }

    /// <summary>
    /// Fast QA path (<c>SL_SHOT=readme</c>): the raw pictures for the project's page. Liked songs
    /// give the page a taste without a listening history, so nothing on it reads like a test run.
    /// Plain size on purpose: a bitmap at a higher DPI loses the glyph of the play button.
    /// </summary>
    private async System.Threading.Tasks.Task CaptureForReadmeAsync()
    {
        if (_viewModel == null) { Dispatcher.UIThread.Post(Close); return; }

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sl_shots");
        System.IO.Directory.CreateDirectory(dir);
        System.Threading.Tasks.Task ShotAsync(string name) => CaptureAsync(System.IO.Path.Combine(dir, name + ".png"));

        // Somebody may be listening to the real player next to this run.
        _viewModel.Volume = 0.02;

        // Song lyrics belong to their authors: they stay out of pictures that get published.
        var lyricLine = AppSettings?.ShowSingleLyric ?? true;
        if (AppSettings != null) AppSettings.ShowSingleLyric = false;
        await System.Threading.Tasks.Task.Delay(6000);

        _viewModel.NavigateToSearchCommand.Execute(null);
        _viewModel.SearchViewModel.SelectedFilter = SoundLibert.Domain.Enums.SearchFilter.Songs;
        SongItem? playing = null;
        foreach (var query in new[] { "okean elzy", "coldplay yellow", "the weeknd blinding lights", "skryabin", "imagine dragons believer" })
        {
            _viewModel.SearchViewModel.Query = query;
            _viewModel.SearchViewModel.SearchCommand.Execute(null);
            await System.Threading.Tasks.Task.Delay(3500);
            if (_viewModel.SearchViewModel.Songs.FirstOrDefault() is not { } song) continue;
            await _viewModel.ToggleLikeAsync(song);
            playing = song;
        }
        if (playing != null) _viewModel.SearchViewModel.PlaySongCommand.Execute(playing);
        await System.Threading.Tasks.Task.Delay(3000);

        _viewModel.SearchViewModel.SelectedFilter = SoundLibert.Domain.Enums.SearchFilter.All;
        _viewModel.SearchViewModel.Query = string.Empty;
        _viewModel.SearchQuery = string.Empty;

        // Home.
        _viewModel.NavigateToHomeCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(800);
        _viewModel.HomeViewModel.RefreshCommand.Execute(null);
        for (var i = 0; i < 45 && _viewModel.HomeViewModel.IsRefreshing; i++)
            await System.Threading.Tasks.Task.Delay(1000);
        await System.Threading.Tasks.Task.Delay(2500);
        await ShotAsync("rd_home");
        for (var i = 1; i <= 4; i++)
        {
            this.GetVisualDescendants().OfType<HomeView>().FirstOrDefault()?.ScrollToOffset(i * 620);
            await System.Threading.Tasks.Task.Delay(1800);
            await ShotAsync($"rd_home_{i}");
        }
        this.GetVisualDescendants().OfType<HomeView>().FirstOrDefault()?.ScrollToOffset(0);
        await System.Threading.Tasks.Task.Delay(600);

        // A mood.
        var tiles = _viewModel.HomeViewModel.Shelves.OfType<SoundLibert.UI.ViewModels.TileRailVm>().FirstOrDefault()?.Tiles;
        if (tiles?.FirstOrDefault(t => t.Name == "Party") is { } party)
        {
            _viewModel.OpenMood(party.Tile);
            await System.Threading.Tasks.Task.Delay(4000);
            await ShotAsync("rd_mood");
            _viewModel.GoBackCommand.Execute(null);
            await System.Threading.Tasks.Task.Delay(800);
        }

        // Search, before and after.
        _viewModel.NavigateToSearchCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(3500);
        await ShotAsync("rd_search");
        ScrollViewer? SearchScroll() => this.GetVisualDescendants().OfType<SearchView>().FirstOrDefault()
            ?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (SearchScroll() is { } discover)
        {
            discover.Offset = new Vector(0, 560);
            await System.Threading.Tasks.Task.Delay(1800);
            await ShotAsync("rd_search_2");
            discover.Offset = default;
        }
        _viewModel.SearchViewModel.Query = "imagine dragons";
        _viewModel.SearchViewModel.SearchCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(5000);
        await ShotAsync("rd_search_all");
        _viewModel.SearchViewModel.Query = string.Empty;
        _viewModel.SearchQuery = string.Empty;
        _viewModel.NavigateToHomeCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(800);

        // An artist, and an album of theirs to its end.
        await _viewModel.OpenBrowseAsync("UC0aXrjVxG5pZr99v77wZdPQ", "Imagine Dragons", null, null);
        await System.Threading.Tasks.Task.Delay(5000);
        await ShotAsync("rd_artist");
        if (this.GetVisualDescendants().OfType<ArtistView>().FirstOrDefault()
                ?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } artistScroll)
        {
            artistScroll.Offset = new Vector(0, 620);
            await System.Threading.Tasks.Task.Delay(1800);
            await ShotAsync("rd_artist_2");
        }
        if (_viewModel.CurrentView is SoundLibert.UI.ViewModels.ArtistViewModel artist &&
            artist.Shelves.OfType<SoundLibert.UI.ViewModels.CardShelfVm>().FirstOrDefault(s => s.Title.Contains("Album")) is { } albums)
        {
            _viewModel.OpenShelfCard(albums.Items.FirstOrDefault(i => i.Title.StartsWith("Evolve", StringComparison.Ordinal)) ?? albums.Items[0]);
            await System.Threading.Tasks.Task.Delay(5000);
            await ShotAsync("rd_album");
            if (this.GetVisualDescendants().OfType<CollectionView>().FirstOrDefault()
                    ?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } rows)
            {
                rows.Offset = new Vector(0, double.MaxValue);
                await System.Threading.Tasks.Task.Delay(2000);
                rows.Offset = new Vector(0, double.MaxValue);
                await System.Threading.Tasks.Task.Delay(1800);
                await ShotAsync("rd_album_end");
            }
            _viewModel.GoBackCommand.Execute(null);
            await System.Threading.Tasks.Task.Delay(800);
        }

        // A queue that reads well under the song: the artist's top songs.
        if (_viewModel.CurrentView is SoundLibert.UI.ViewModels.ArtistViewModel topSongs)
        {
            topSongs.PlayAllCommand.Execute(null);
            await System.Threading.Tasks.Task.Delay(4500);
        }
        _viewModel.GoBackCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(800);

        // The full player. On top, because a covered window draws nothing.
        Topmost = true;
        await System.Threading.Tasks.Task.Delay(400);
        _viewModel.ToggleFullscreenPlayerCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(8000);
        for (var i = 1; i <= 8; i++)
        {
            _viewModel.NotifyFullscreenActivity();
            await System.Threading.Tasks.Task.Delay(700);
            await ShotAsync($"rd_player_{i}");
            System.Diagnostics.Trace.WriteLine($"[QA] rd_player_{i}: lyric line shown={SingleLyricLine.IsEffectivelyVisible && SingleLyricLine.Opacity > 0}");
            await System.Threading.Tasks.Task.Delay(2600);
        }
        FullscreenScroll.Offset = new Vector(0, FullscreenHero.Bounds.Height);
        await System.Threading.Tasks.Task.Delay(2000);
        await ShotAsync("rd_below");
        FullscreenScroll.Offset = new Vector(0, FullscreenHero.Bounds.Height + 620);
        await System.Threading.Tasks.Task.Delay(2000);
        await ShotAsync("rd_below_2");
        FullscreenScroll.Offset = new Vector(0, 0);
        await System.Threading.Tasks.Task.Delay(500);
        VideoMode_Click(null, new RoutedEventArgs());
        await System.Threading.Tasks.Task.Delay(8000);
        for (var i = 1; i <= 5; i++)
        {
            _viewModel.NotifyFullscreenActivity();
            await System.Threading.Tasks.Task.Delay(700);
            await ShotAsync($"rd_video_{i}");
            await System.Threading.Tasks.Task.Delay(2600);
        }
        SongMode_Click(null, new RoutedEventArgs());
        await System.Threading.Tasks.Task.Delay(1500);
        _viewModel.CloseFullscreenPlayerCommand.Execute(null);
        Topmost = false;
        if (AppSettings != null) AppSettings.ShowSingleLyric = lyricLine;

        _viewModel.EndPlaybackSession();
        Dispatcher.UIThread.Post(Close);
    }

    /// <summary>
    /// Fast QA path (<c>SL_SHOT=clip</c>): does the clip behind the song keep moving? It counts
    /// what the video engine hands over against what reaches the screen, opened at once and
    /// again later. The window stays on top: Windows does not ask a covered window to draw, so
    /// a covered one stops at the first frame, and a frame-rate count there says 120 fps of nothing.
    /// </summary>
    private async System.Threading.Tasks.Task WatchClipAsync()
    {
        if (_viewModel == null) { Dispatcher.UIThread.Post(Close); return; }
        _viewModel.Volume = 0.02;

        await System.Threading.Tasks.Task.Delay(6000);
        _viewModel.NavigateToSearchCommand.Execute(null);
        _viewModel.SearchViewModel.SelectedFilter = SoundLibert.Domain.Enums.SearchFilter.Songs;
        _viewModel.SearchViewModel.Query = "imagine dragons believer";
        _viewModel.SearchViewModel.SearchCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(3500);
        if (_viewModel.SearchViewModel.Songs.FirstOrDefault() is { } song)
            _viewModel.SearchViewModel.PlaySongCommand.Execute(song);
        await System.Threading.Tasks.Task.Delay(4000);

        Topmost = true;
        await System.Threading.Tasks.Task.Delay(500);
        var clip = this.GetVisualDescendants().OfType<SoundLibert.UI.Controls.CanvasVideoView>().FirstOrDefault();

        async System.Threading.Tasks.Task WatchAsync(string when, int seconds)
        {
            var delivered = clip?.FramesDelivered ?? 0;
            var drawn = clip?.FramesDrawn ?? 0;
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(seconds));
            System.Diagnostics.Trace.WriteLine(
                $"[QA] clip, {when}: handed over {(clip?.FramesDelivered ?? 0) - delivered}, drawn {(clip?.FramesDrawn ?? 0) - drawn} in {seconds} s");
        }

        _viewModel.ToggleFullscreenPlayerCommand.Execute(null);
        await WatchAsync("opened soon after the song began", 12);

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sl_shots");
        System.IO.Directory.CreateDirectory(dir);
        _viewModel.NotifyFullscreenActivity();
        await System.Threading.Tasks.Task.Delay(700);
        await CaptureAsync(System.IO.Path.Combine(dir, "clip_hero.png"));

        _viewModel.CloseFullscreenPlayerCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(5000);
        _viewModel.ToggleFullscreenPlayerCommand.Execute(null);
        await WatchAsync("opened again", 12);

        _viewModel.EndPlaybackSession();
        Dispatcher.UIThread.Post(Close);
    }

    /// <summary>QA: how much the process holds right now, and how much of it the visuals on screen explain.</summary>
    private void LogMemory(string when)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var visuals = this.GetVisualDescendants().ToList();
        var images = visuals.OfType<SoundLibert.UI.Controls.RemoteImage>().ToList();
        System.Diagnostics.Trace.WriteLine(
            $"[QA] memory, {when}: working set {process.WorkingSet64 / (1024 * 1024)} MB, " +
            $"private {process.PrivateMemorySize64 / (1024 * 1024)} MB, managed {GC.GetTotalMemory(false) / (1024 * 1024)} MB; " +
            $"{visuals.Count} visuals, {images.Count} covers, {images.Count(i => i.Source != null)} of them loaded");
    }

    /// <summary>
    /// Fast QA path (<c>SL_SHOT=mem</c>): what a long Home costs. No music, so the video
    /// engine is not part of the number.
    /// </summary>
    private async System.Threading.Tasks.Task MeasureMemoryAsync()
    {
        if (_viewModel == null) { Dispatcher.UIThread.Post(Close); return; }

        await System.Threading.Tasks.Task.Delay(12000);
        LogMemory("first feed, top of Home");

        HomeView? Home() => this.GetVisualDescendants().OfType<HomeView>().FirstOrDefault();

        // A rail draws what fits, and the rest as it is scrolled.
        Home()?.ScrollToOffset(500);
        await System.Threading.Tasks.Task.Delay(1500);
        var rail = Home()?.GetVisualDescendants().OfType<SoundLibert.UI.Controls.Rail>()
            .FirstOrDefault(r => r.DataContext is SoundLibert.UI.ViewModels.CardShelfVm { Items.Count: > 10 });
        if (rail?.DataContext is SoundLibert.UI.ViewModels.CardShelfVm cards)
        {
            var drawnAtFirst = cards.Shown.Count;
            for (var i = 0; i < 6; i++)
            {
                rail.ScrollOffset = double.MaxValue;
                await System.Threading.Tasks.Task.Delay(300);
            }
            System.Diagnostics.Trace.WriteLine(
                $"[QA] \"{cards.Title}\": {drawnAtFirst} covers drawn at first, {cards.Shown.Count} of {cards.Items.Count} after scrolling the rail");
            rail.ScrollOffset = 0;
        }

        for (var i = 1; i <= 60; i++)
        {
            Home()?.ScrollToOffset(i * 620);
            await System.Threading.Tasks.Task.Delay(700);
            if (i % 20 == 0) LogMemory($"after {i} screens, {_viewModel.HomeViewModel.Shelves.Count} shelves");
        }
        await System.Threading.Tasks.Task.Delay(3000);
        LogMemory($"scrolled through, {_viewModel.HomeViewModel.Shelves.Count} shelves");

        // All the way back: everything let go on the way down is built again in its place.
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sl_shots");
        System.IO.Directory.CreateDirectory(dir);
        await CaptureAsync(System.IO.Path.Combine(dir, "mem_bottom.png"));
        for (var i = 60; i >= 0; i -= 2)
        {
            Home()?.ScrollToOffset(i * 620);
            await System.Threading.Tasks.Task.Delay(350);
            if (i == 20) await CaptureAsync(System.IO.Path.Combine(dir, "mem_on_the_way_up.png"));
        }
        await System.Threading.Tasks.Task.Delay(1500);
        await CaptureAsync(System.IO.Path.Combine(dir, "mem_top.png"));
        LogMemory("back at the top");

        _viewModel.NavigateToSearchCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(5000);
        LogMemory("Search open, Home gone");

        _viewModel.NavigateToHomeCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(5000);
        LogMemory("back on Home");

        // What music and the video engine add.
        _viewModel.Volume = 0.02;
        _viewModel.NavigateToSearchCommand.Execute(null);
        _viewModel.SearchViewModel.SelectedFilter = SoundLibert.Domain.Enums.SearchFilter.Songs;
        _viewModel.SearchViewModel.Query = "imagine dragons believer";
        _viewModel.SearchViewModel.SearchCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(3500);
        if (_viewModel.SearchViewModel.Songs.FirstOrDefault() is { } song)
            _viewModel.SearchViewModel.PlaySongCommand.Execute(song);
        await System.Threading.Tasks.Task.Delay(6000);
        LogMemory("a song plays");

        _viewModel.ToggleFullscreenPlayerCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(15000);
        LogMemory("the full player, its clip behind the song");

        VideoMode_Click(null, new RoutedEventArgs());
        await System.Threading.Tasks.Task.Delay(15000);
        LogMemory("Video mode");

        SongMode_Click(null, new RoutedEventArgs());
        await System.Threading.Tasks.Task.Delay(5000);
        _viewModel.CloseFullscreenPlayerCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(5000);
        LogMemory("the full player closed, the song plays on");

        _viewModel.EndPlaybackSession();
        Dispatcher.UIThread.Post(Close);
    }

    /// <summary>
    /// Fast QA path (<c>SL_SHOT=fps</c>): frames per second on the busiest screens. The window
    /// stays on top meanwhile (without taking the focus): one covered by others is held back
    /// by Windows, and says nothing about the code.
    /// </summary>
    private async System.Threading.Tasks.Task MeasureSmoothnessAsync()
    {
        if (_viewModel == null) { Dispatcher.UIThread.Post(Close); return; }
        _viewModel.Volume = 0.02;

        await System.Threading.Tasks.Task.Delay(9000);
        Topmost = true;
        await System.Threading.Tasks.Task.Delay(600);

        await MeasureFramesAsync("Home, resting");
        if (this.GetVisualDescendants().OfType<HomeView>().FirstOrDefault() is { } home)
        {
            await MeasureFramesAsync("Home, down through new shelves", () => SmoothScroller.ScrollBy(home.BoardScroll, new Vector(0, 110)));
            await MeasureFramesAsync("Home, resting far down");
            await MeasureFramesAsync("Home, back up over what was seen", () => SmoothScroller.ScrollBy(home.BoardScroll, new Vector(0, -110)));
            home.ScrollToOffset(0);
        }

        _viewModel.NavigateToSearchCommand.Execute(null);
        _viewModel.SearchViewModel.Query = "imagine dragons believer";
        _viewModel.SearchViewModel.SelectedFilter = SoundLibert.Domain.Enums.SearchFilter.Songs;
        _viewModel.SearchViewModel.SearchCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(3500);
        if (_viewModel.SearchViewModel.Songs.FirstOrDefault() is { } song)
            _viewModel.SearchViewModel.PlaySongCommand.Execute(song);
        await System.Threading.Tasks.Task.Delay(4000);
        _viewModel.ToggleFullscreenPlayerCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(8000);

        await MeasureFramesAsync("full player, resting");
        DetailsHint_Click(null, new RoutedEventArgs());
        await System.Threading.Tasks.Task.Delay(1000);
        System.Diagnostics.Trace.WriteLine($"[QA] after a second the hint has scrolled to {FullscreenScroll.Offset.Y:0} of {FullscreenHero.Bounds.Height:0}");
        await MeasureFramesAsync("under the song");
        var scrollDirection = 1;
        await MeasureFramesAsync("under the song, scrolling", () =>
        {
            if (!SmoothScroller.ScrollBy(FullscreenScroll, new Vector(0, scrollDirection * 110)))
                scrollDirection = -scrollDirection;
        });

        Topmost = false;
        await System.Threading.Tasks.Task.Delay(600);
        await MeasureFramesAsync("no longer on top");

        _viewModel.EndPlaybackSession();
        Dispatcher.UIThread.Post(Close);
    }

    /// <summary>QA: Home with everything on it, moods, search, an artist, under the song, the video pages.</summary>
    private async System.Threading.Tasks.Task CaptureDiscoveryScreensAsync(string dir)
    {
        if (_viewModel == null) return;

        // The video rails and the pages behind them, now that there is a listening history.
        _viewModel.NavigateToHomeCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(800);
        _viewModel.HomeViewModel.RefreshCommand.Execute(null);
        for (var i = 0; i < 45 && _viewModel.HomeViewModel.IsRefreshing; i++)
            await System.Threading.Tasks.Task.Delay(1000);
        await System.Threading.Tasks.Task.Delay(1500);
        await CaptureAsync(System.IO.Path.Combine(dir, "home_rich_top.png"));

        // The block at the top moves on by itself, and what it says is what it shows.
        var spotlight = this.GetVisualDescendants().OfType<SoundLibert.UI.Controls.Spotlight>().FirstOrDefault();
        for (var i = 0; i < 4 && spotlight != null; i++)
        {
            System.Diagnostics.Trace.WriteLine($"[QA] spotlight: {spotlight.EyebrowText.Text} / {spotlight.TitleText.Text}");
            await System.Threading.Tasks.Task.Delay(8300);
        }
        await CaptureAsync(System.IO.Path.Combine(dir, "home_spotlight.png"));
        await MeasureFramesAsync("Home, resting");

        for (var i = 1; i <= 8; i++)
        {
            this.GetVisualDescendants().OfType<HomeView>().FirstOrDefault()?.ScrollToOffset(i * 640);
            await System.Threading.Tasks.Task.Delay(1400);
            await CaptureAsync(System.IO.Path.Combine(dir, $"home_rich_{i}.png"));
        }
        if (this.GetVisualDescendants().OfType<HomeView>().FirstOrDefault() is { } homeView)
        {
            var direction = 1;
            await MeasureFramesAsync("Home, scrolling", () =>
            {
                if (!SmoothScroller.ScrollBy(homeView.BoardScroll, new Vector(0, direction * 110)))
                    direction = -direction;
            });
        }
        var shelvesBefore = _viewModel.HomeViewModel.Shelves.Count;
        for (var i = 0; i < 6; i++)
        {
            this.GetVisualDescendants().OfType<HomeView>().FirstOrDefault()?.ScrollToBottom();
            await System.Threading.Tasks.Task.Delay(1500);
        }
        System.Diagnostics.Trace.WriteLine($"[QA] home grows: {shelvesBefore} shelves, then {_viewModel.HomeViewModel.Shelves.Count}");
        System.Diagnostics.Trace.WriteLine($"[QA] memory with the long Home: {Environment.WorkingSet / (1024 * 1024)} MB");
        await CaptureAsync(System.IO.Path.Combine(dir, "home_rich_end.png"));
        this.GetVisualDescendants().OfType<HomeView>().FirstOrDefault()?.ScrollToOffset(0);
        await System.Threading.Tasks.Task.Delay(800);

        // A mood, a chart page, the search before and after typing.
        var firstChip = _viewModel.HomeViewModel.Chips.FirstOrDefault();
        if (firstChip != null)
        {
            _viewModel.HomeViewModel.SelectChipCommand.Execute(firstChip);
            await System.Threading.Tasks.Task.Delay(3500);
            await CaptureAsync(System.IO.Path.Combine(dir, "home_chip.png"));
            _viewModel.HomeViewModel.SelectChipCommand.Execute(firstChip);
            await System.Threading.Tasks.Task.Delay(800);
        }
        var tiles = _viewModel.HomeViewModel.Shelves.OfType<SoundLibert.UI.ViewModels.TileRailVm>().FirstOrDefault()?.Tiles;
        if (tiles is { Count: > 0 })
        {
            _viewModel.OpenMood(tiles[0].Tile);
            await System.Threading.Tasks.Task.Delay(4000);
            await CaptureAsync(System.IO.Path.Combine(dir, "mood_page.png"));
            _viewModel.GoBackCommand.Execute(null);
            await System.Threading.Tasks.Task.Delay(800);
        }
        _viewModel.NavigateToSearchCommand.Execute(null);
        _viewModel.SearchViewModel.SelectedFilter = SoundLibert.Domain.Enums.SearchFilter.All;
        _viewModel.SearchViewModel.Query = string.Empty;
        _viewModel.SearchQuery = string.Empty;
        await System.Threading.Tasks.Task.Delay(3500);
        await CaptureAsync(System.IO.Path.Combine(dir, "search_discover.png"));
        ScrollViewer? SearchScroll() => this.GetVisualDescendants().OfType<SearchView>().FirstOrDefault()
            ?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        for (var i = 1; i <= 3; i++)
        {
            if (SearchScroll() is { } scroll) scroll.Offset = new Vector(0, i * 620);
            await System.Threading.Tasks.Task.Delay(1400);
            await CaptureAsync(System.IO.Path.Combine(dir, $"search_discover_{i + 1}.png"));
        }
        if (SearchScroll() is { } searchTop) searchTop.Offset = default;
        _viewModel.SearchViewModel.Query = "imagine";
        await System.Threading.Tasks.Task.Delay(2000);
        await CaptureAsync(System.IO.Path.Combine(dir, "search_suggest.png"));
        _viewModel.SearchViewModel.Query = "imagine dragons";
        _viewModel.SearchViewModel.SelectedFilter = SoundLibert.Domain.Enums.SearchFilter.All;
        _viewModel.SearchViewModel.SearchCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(5000);
        await CaptureAsync(System.IO.Path.Combine(dir, "search_all.png"));
        for (var i = 1; i <= 2; i++)
        {
            if (SearchScroll() is { } scroll) scroll.Offset = new Vector(0, i * 620);
            await System.Threading.Tasks.Task.Delay(1400);
            await CaptureAsync(System.IO.Path.Combine(dir, $"search_all_{i + 1}.png"));
        }
        _viewModel.NavigateToHomeCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(800);

        // A real artist's page.
        await _viewModel.OpenBrowseAsync("UC0aXrjVxG5pZr99v77wZdPQ", "Imagine Dragons", null, null);
        await System.Threading.Tasks.Task.Delay(5000);
        await CaptureAsync(System.IO.Path.Combine(dir, "artist_real.png"));
        var artistScroll = this.GetVisualDescendants().OfType<ArtistView>().FirstOrDefault()
            ?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (artistScroll != null)
        {
            artistScroll.Offset = new Vector(0, 620);
            await System.Threading.Tasks.Task.Delay(1500);
            await CaptureAsync(System.IO.Path.Combine(dir, "artist_real_2.png"));
            artistScroll.Offset = new Vector(0, 1400);
            await System.Threading.Tasks.Task.Delay(1500);
            await CaptureAsync(System.IO.Path.Combine(dir, "artist_real_3.png"));
            artistScroll.Offset = new Vector(0, 2200);
            await System.Threading.Tasks.Task.Delay(1500);
            await CaptureAsync(System.IO.Path.Combine(dir, "artist_real_4.png"));
            artistScroll.Offset = new Vector(0, double.MaxValue);
            await System.Threading.Tasks.Task.Delay(1500);
            await CaptureAsync(System.IO.Path.Combine(dir, "artist_real_end.png"));
        }
        _viewModel.GoBackCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(800);

        // After the last track of an album and of a playlist.
        foreach (var (name, id, title) in new[]
        {
            ("album", "MPREb_q16Gzaa1WK8", "Album"),
            ("playlist", "VLRDCLAK5uy_nDL8KeBrUagwyISwNmyEiSfYgz1gVCesg", "Playlist")
        })
        {
            await _viewModel.OpenBrowseAsync(id, title, null, null);
            await System.Threading.Tasks.Task.Delay(5000);
            await CaptureAsync(System.IO.Path.Combine(dir, $"{name}_top.png"));
            var rows = this.GetVisualDescendants().OfType<CollectionView>().FirstOrDefault()
                ?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (rows != null)
            {
                rows.Offset = new Vector(0, double.MaxValue);
                await System.Threading.Tasks.Task.Delay(2000);
                rows.Offset = new Vector(0, double.MaxValue);
                await System.Threading.Tasks.Task.Delay(1500);
                await CaptureAsync(System.IO.Path.Combine(dir, $"{name}_end.png"));
            }
            _viewModel.GoBackCommand.Execute(null);
            await System.Threading.Tasks.Task.Delay(800);
        }

        // Under the song in the full player, and the hint that leads there.
        _viewModel.ToggleFullscreenPlayerCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(6000);
        _viewModel.NotifyFullscreenActivity();
        await System.Threading.Tasks.Task.Delay(700);
        await CaptureAsync(System.IO.Path.Combine(dir, "fullscreen_hint.png"));
        var clip = this.GetVisualDescendants().OfType<SoundLibert.UI.Controls.CanvasVideoView>().FirstOrDefault(v => v.IsEffectivelyVisible);
        var clipFramesBefore = clip?.FramesDrawn ?? 0;
        await MeasureFramesAsync("full player, resting");
        System.Diagnostics.Trace.WriteLine(
            $"[QA] the clip: {(clip?.FramesDrawn ?? 0) - clipFramesBefore} new pictures in 3 s, {clipFramesBefore} before, " +
            $"{clip?.FramesDelivered} handed over in all; active={_viewModel.IsCanvasActive} · {_viewModel.CanvasDebugState}");
        DetailsHint_Click(null, new RoutedEventArgs());
        await System.Threading.Tasks.Task.Delay(4000);
        System.Diagnostics.Trace.WriteLine($"[QA] the hint scrolled to {FullscreenScroll.Offset.Y:0}, the hero is {FullscreenHero.Bounds.Height:0} tall");
        await CaptureAsync(System.IO.Path.Combine(dir, "fullscreen_below.png"));
        await MeasureFramesAsync("under the song");
        var scrollDirection = 1;
        await MeasureFramesAsync("under the song, scrolling", () =>
        {
            if (!SmoothScroller.ScrollBy(FullscreenScroll, new Vector(0, scrollDirection * 110)))
                scrollDirection = -scrollDirection;
        });
        await System.Threading.Tasks.Task.Delay(1000);
        FullscreenScroll.Offset = new Vector(0, FullscreenHero.Bounds.Height + 620);
        await System.Threading.Tasks.Task.Delay(1800);
        await CaptureAsync(System.IO.Path.Combine(dir, "fullscreen_below_2.png"));
        FullscreenScroll.Offset = new Vector(0, FullscreenHero.Bounds.Height + 1240);
        await System.Threading.Tasks.Task.Delay(1800);
        await CaptureAsync(System.IO.Path.Combine(dir, "fullscreen_below_3.png"));
        FullscreenScroll.Offset = new Vector(0, 0);
        _viewModel.CloseFullscreenPlayerCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(800);

        _viewModel.NavigateToHomeCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(1500);
        await System.Threading.Tasks.Task.Delay(3000);
        await CaptureAsync(System.IO.Path.Combine(dir, "home_videos.png"));
        System.Diagnostics.Trace.WriteLine(
            $"[QA] home shelves: {_viewModel.HomeViewModel.Shelves.Count}: " +
            string.Join(" | ", _viewModel.HomeViewModel.Shelves.Select(v => v.Title)));

        await _viewModel.OpenVideoFeedAsync("Music videos", _viewModel.HomeViewModel.OpenVideosFeed);
        await System.Threading.Tasks.Task.Delay(5000);
        await CaptureAsync(System.IO.Path.Combine(dir, "home_videos_page.png"));
        await CheckVideoPageGrowsAsync("home");
        _viewModel.GoBackCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(800);

        await _viewModel.OpenVideoFeedAsync("New music videos", _viewModel.HomeViewModel.OpenNewVideosFeed);
        await System.Threading.Tasks.Task.Delay(5000);
        await CaptureAsync(System.IO.Path.Combine(dir, "new_videos_page.png"));
        await CheckVideoPageGrowsAsync("new");
        _viewModel.GoBackCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(800);

        await _viewModel.OpenVideoFeedAsync("Concerts & live", _viewModel.HomeViewModel.OpenLiveFeed);
        await System.Threading.Tasks.Task.Delay(6000);
        await CaptureAsync(System.IO.Path.Combine(dir, "live_videos_page.png"));
        await CheckVideoPageGrowsAsync("live");
        _viewModel.GoBackCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(800);

        // A real artist, with the full list behind the shelf.
        await _viewModel.OpenArtistVideosAsync("UC0aXrjVxG5pZr99v77wZdPQ", "Imagine Dragons", null);
        await System.Threading.Tasks.Task.Delay(6000);
        await CaptureAsync(System.IO.Path.Combine(dir, "artist_videos_real.png"));
        await CheckVideoPageGrowsAsync("real artist");
        await CaptureAsync(System.IO.Path.Combine(dir, "artist_videos_real_more.png"));
        _viewModel.GoBackCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(800);
    }

    /// <summary>
    /// Fast QA path (<c>SL_SHOT=lyrics</c>): play one track, open the fullscreen
    /// lyrics, log position-vs-highlighted-line for a few seconds.
    /// </summary>
    private async System.Threading.Tasks.Task CaptureLyricsOnlyAsync()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sl_shots");
        System.IO.Directory.CreateDirectory(dir);

        await System.Threading.Tasks.Task.Delay(4000);
        if (_viewModel == null) { Dispatcher.UIThread.Post(Close); return; }

        _viewModel.NavigateToSearchCommand.Execute(null);
        _viewModel.SearchViewModel.Query = "gentle drowsy lofi";
        _viewModel.SearchViewModel.SearchCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(4000);

        if (_viewModel.SearchViewModel.Songs.Count > 0)
            _viewModel.SearchViewModel.PlaySongCommand.Execute(_viewModel.SearchViewModel.Songs[0]);

        await System.Threading.Tasks.Task.Delay(9000);

        _viewModel.ToggleFullscreenPlayerCommand.Execute(null);
        _viewModel.ShowLyrics = true;
        await System.Threading.Tasks.Task.Delay(8000);

        for (int i = 0; i < 3; i++)
        {
            var idx = _viewModel.CurrentLyricIndex;
            var has = idx >= 0 && idx < _viewModel.Lyrics.Count;
            var line = has ? _viewModel.Lyrics[idx].Text : "<none>";
            var lineTime = has ? _viewModel.Lyrics[idx].Time : System.TimeSpan.Zero;
            System.Diagnostics.Debug.WriteLine(
                $"[LyricsQA] pos={_viewModel.Position:mm\\:ss\\.fff} index={idx} " +
                $"lineTime={lineTime:mm\\:ss\\.fff} line=\"{line}\"");
            await CaptureAsync(System.IO.Path.Combine(dir, $"lyrics_qa_{i}.png"));
            await System.Threading.Tasks.Task.Delay(2500);
        }

        _viewModel.EndPlaybackSession();
        Dispatcher.UIThread.Post(Close);
    }

    /// <summary>
    /// Fast QA path (<c>SL_SHOT=report</c>): the sidebar feedback buttons,
    /// Settings → About and the "Report a bug" dialog with its preview. The
    /// exact report text lands next to the shots to check the redaction.
    /// Plays nothing.
    /// </summary>
    private async System.Threading.Tasks.Task CaptureReportOnlyAsync()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sl_shots");
        System.IO.Directory.CreateDirectory(dir);

        await System.Threading.Tasks.Task.Delay(4000);
        if (_viewModel == null || AppSettings is not { } settings) { Dispatcher.UIThread.Post(Close); return; }

        _viewModel.NavigateToSettingsCommand.Execute(null);
        await System.Threading.Tasks.Task.Delay(1200);
        var tabs = this.GetVisualDescendants().OfType<SettingsView>().FirstOrDefault()?
            .GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        if (tabs?.Items.OfType<TabItem>().FirstOrDefault(t => Equals(t.Header, "About")) is { } about)
            tabs.SelectedItem = about;
        await System.Threading.Tasks.Task.Delay(1000);
        await CaptureAsync(System.IO.Path.Combine(dir, "settings_about.png"));
        tabs?.GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(s => s.HorizontalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled)
            ?.ScrollToEnd();
        await System.Threading.Tasks.Task.Delay(600);
        await CaptureAsync(System.IO.Path.Combine(dir, "settings_about_end.png"));

        try
        {
            var report = BugReportWindow.Create(_viewModel.Updates, settings, null);
            report.Show(this);
            await System.Threading.Tasks.Task.Delay(800);
            await CaptureVisualAsync(report, (int)report.ClientSize.Width, (int)report.ClientSize.Height,
                System.IO.Path.Combine(dir, "bugreport.png"));
            report.QaFill("[QA] Lyrics stop syncing after a seek",
                "1. Play a song with synced lyrics\n2. Seek to 1:30\nThe highlighted line lags behind the music.");
            await System.Threading.Tasks.Task.Delay(800);
            await CaptureVisualAsync(report, (int)report.ClientSize.Width, (int)report.ClientSize.Height,
                System.IO.Path.Combine(dir, "bugreport_preview.png"));
            var (url, paste) = report.BuildIssueLink();
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "bugreport.txt"), report.FullReport());
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "bugreport_url.txt"), url);
            System.Diagnostics.Trace.WriteLine(
                $"[QA] bug report: link {url.Length} chars, paste {paste?.Length ?? 0} chars");
            report.Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[QA] BugReportWindow failed: {ex}");
        }

        Dispatcher.UIThread.Post(Close);
    }

    /// <summary>
    /// QA (<c>SL_SHOT=welcome</c>): the opening hands over to the welcome, its steps are
    /// photographed with a choice and a search, and Home is read after the choice.
    /// </summary>
    private async System.Threading.Tasks.Task WelcomeTourAsync()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sl_shots");
        System.IO.Directory.CreateDirectory(dir);
        string Shot(string name) => System.IO.Path.Combine(dir, name + ".png");
        // On top, so grabs of the live screen see the motion.
        Topmost = true;
        var welcome = _welcome;
        if (welcome == null)
        {
            System.Diagnostics.Trace.WriteLine("[QA] welcome: not shown");
            Dispatcher.UIThread.Post(Close);
            return;
        }

        await System.Threading.Tasks.Task.Delay(4500);
        await CaptureAsync(Shot("welcome_hello"));
        for (var i = 0; i < 40 && welcome.IsLoadingArtists; i++) await System.Threading.Tasks.Task.Delay(500);

        welcome.QaNext();
        await System.Threading.Tasks.Task.Delay(2000);
        await CaptureAsync(Shot("welcome_taste"));
        var offered = welcome.QaShownNames();
        System.Diagnostics.Trace.WriteLine($"[QA] welcome offers {offered.Count}: {string.Join(", ", offered.Take(14))}");

        welcome.QaChoose(3);
        await System.Threading.Tasks.Task.Delay(900);
        await CaptureAsync(Shot("welcome_chosen"));

        welcome.QaSearch("Океан Ельзи");
        await System.Threading.Tasks.Task.Delay(4000);
        await CaptureAsync(Shot("welcome_search"));
        var found = welcome.QaShownNames();
        System.Diagnostics.Trace.WriteLine($"[QA] welcome search found {found.Count}: {string.Join(", ", found.Take(6))}");
        welcome.QaChoose(1);
        welcome.QaSearch(string.Empty);
        await System.Threading.Tasks.Task.Delay(1600);
        await CaptureAsync(Shot("welcome_back"));

        welcome.QaDone();
        await System.Threading.Tasks.Task.Delay(260);
        await CaptureAsync(Shot("welcome_leaving"));

        // Home, started from the choice.
        await System.Threading.Tasks.Task.Delay(1500);
        var home = _viewModel?.HomeViewModel;
        for (var i = 0; i < 60 && home is { IsRefreshing: true }; i++) await System.Threading.Tasks.Task.Delay(500);
        await System.Threading.Tasks.Task.Delay(1500);
        await CaptureAsync(Shot("welcome_home"));
        var settings = AppSettings;
        System.Diagnostics.Trace.WriteLine(
            $"[QA] welcome kept {settings?.FavouriteArtists.Count ?? -1} artists ({string.Join(", ", settings?.FavouriteArtists.Select(f => f.Name) ?? Array.Empty<string>())}), seen {settings?.WelcomeSeen}");
        System.Diagnostics.Trace.WriteLine(
            $"[QA] Home after the welcome: {string.Join(" | ", home?.Shelves.Take(16).Select(s => s.Title) ?? Array.Empty<string>())}");
        Dispatcher.UIThread.Post(Close);
    }

    /// <summary>QA: shows a control in its hover state without moving the real pointer.</summary>
    private static void Hover(Control control, bool on) =>
        ((IPseudoClasses)control.Classes).Set(":pointerover", on);

    /// <summary>Fast QA path (<c>SL_SHOT=icons</c>): every icon, large and at use size, outline and solid.</summary>
    private async System.Threading.Tasks.Task CaptureIconSheetAsync()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sl_shots");
        System.IO.Directory.CreateDirectory(dir);

        var panel = new WrapPanel { Width = 1500, Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#14161D")) };
        foreach (var kind in Enum.GetValues<IconKind>().Skip(1))
        {
            var cell = new StackPanel { Width = 124, Margin = new Thickness(6, 12), Spacing = 6 };
            var row1 = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            row1.Children.Add(new Icon { Kind = kind, Size = 44, Foreground = Avalonia.Media.Brushes.White });
            row1.Children.Add(new Icon { Kind = kind, Size = 44, IsFilled = true, Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF7A33")) });
            var row2 = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            foreach (var size in new[] { 16.0, 20.0, 24.0 })
                row2.Children.Add(new Icon { Kind = kind, Size = size, Foreground = Avalonia.Media.Brushes.White });
            cell.Children.Add(row1);
            cell.Children.Add(row2);
            cell.Children.Add(new TextBlock { Text = kind.ToString(), FontSize = 11, Foreground = Avalonia.Media.Brushes.Gray, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center });
            panel.Children.Add(cell);
        }
        panel.Measure(new Size(1500, double.PositiveInfinity));
        panel.Arrange(new Rect(panel.DesiredSize));
        await System.Threading.Tasks.Task.Delay(300);
        await CaptureVisualAsync(panel, 1500, (int)panel.DesiredSize.Height, System.IO.Path.Combine(dir, "icons.png"));
        Dispatcher.UIThread.Post(Close);
    }

    private async System.Threading.Tasks.Task CaptureVisualAsync(Visual visual, int width, int height, string path)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await System.Threading.Tasks.Task.Delay(200);
        using var rtb = new RenderTargetBitmap(
            new PixelSize(Math.Max(1, width), Math.Max(1, height)), new Vector(96, 96));
        rtb.Render(visual);
#pragma warning disable CS0618 // QA-only capture; encoder-options overload not needed
        using (var fs = System.IO.File.Create(path))
            rtb.Save(fs);
#pragma warning restore CS0618
        System.Diagnostics.Debug.WriteLine($"[Shot] {path} {width}x{height}");
    }

    /// <summary>QA: reaching the end of a video page must bring more, until the list ends.</summary>
    private async System.Threading.Tasks.Task CheckVideoPageGrowsAsync(string name)
    {
        if (_viewModel?.CurrentView is not SoundLibert.UI.ViewModels.VideoFeedViewModel page) return;
        var first = page.Videos.Count;
        for (var i = 0; i < 8; i++)
        {
            this.GetVisualDescendants().OfType<VideoFeedView>().FirstOrDefault()?.ScrollToEnd();
            await System.Threading.Tasks.Task.Delay(1500);
        }
        System.Diagnostics.Trace.WriteLine(
            $"[QA] {name} video page: {first} cards at first, {page.Videos.Count} after scrolling, \"{page.CountText}\"");
    }

    private async System.Threading.Tasks.Task CaptureAsync(string path)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await System.Threading.Tasks.Task.Delay(300);
        var size = new PixelSize(
            Math.Max(1, (int)ClientSize.Width),
            Math.Max(1, (int)ClientSize.Height));
        using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
        rtb.Render(Root);
#pragma warning disable CS0618 // QA-only capture; encoder-options overload not needed
        using (var fs = System.IO.File.Create(path))
            rtb.Save(fs);
#pragma warning restore CS0618
        System.Diagnostics.Debug.WriteLine($"[Shot] {path} {size.Width}x{size.Height}");
    }
}
