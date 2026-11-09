using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using SoundLibert.Domain.Interfaces;
using SoundLibert.UI.Controls;
using SoundLibert.UI.Views;

namespace SoundLibert.UI.Welcome;

/// <summary>
/// The first thing a new listener sees after the opening: the bird the opening drew settles
/// above a greeting, over warm light and a drifting wall of covers, and a second step lets
/// them choose artists for Home to start from. It lies over the whole window and leaves by
/// fading into the app.
/// </summary>
public partial class WelcomeView : UserControl
{
    /// <summary>How strongly the wall shows behind each step.</summary>
    private const double WallHello = 0.46;
    private const double WallTaste = 0.22;

    private readonly WelcomeViewModel? _vm;
    private readonly CancellationTokenSource _cts = new();
    private IDisposable? _searchDelay;
    private bool _holding;
    private bool _entered;
    private bool _leaving;

    // Building the wall takes the UI thread a moment: not while something glides.
    private bool _still = true;
    private IReadOnlyList<string>? _coversWaiting;

    public WelcomeView()
    {
        InitializeComponent();
    }

    public WelcomeView(WelcomeViewModel vm) : this()
    {
        DataContext = _vm = vm;
        Field.Source = Aurora.Make();
        vm.CoversFound += covers =>
        {
            if (_still) Wall.SetCovers(covers);
            else _coversWaiting = covers;
        };
        _ = vm.LoadAsync(_cts.Token);

        // Nothing shows until it enters, so the opening can hand over to it.
        if (Motion.Enabled)
        {
            MarkHost.Opacity = 0;
            foreach (var element in HelloWords) element.Opacity = 0;
        }
        else Wall.Opacity = WallHello;

        ArtistScroll.ScrollChanged += (_, _) => FadeEdges();
        ArtistList.LayoutUpdated += (_, _) => FadeEdges();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    private Control[] HelloWords => new Control[] { HelloTitle, HelloLine, GetStarted, SignIn };

    /// <summary>They are done: Home may start from the choice now.</summary>
    public event Action? Finished;

    /// <summary>It has faded away: the window can let it go.</summary>
    public event Action? Gone;

    public bool IsLoadingArtists => _vm?.IsLoading ?? false;

    // ---- Entering ----

    /// <summary>
    /// The opening plays over it. It waits as the opening will end: the bird big, in the same
    /// place, on the same dark ground, so when the opening fades there is no seam, however
    /// late the word that it fades arrives.
    /// </summary>
    public void HoldForOpening()
    {
        if (_entered || !Motion.Enabled) return;
        _holding = true;
        Night.Opacity = 1;
        MarkHost.Opacity = 1;
        LayoutUpdated += PlaceAtOpening;
    }

    private void PlaceAtOpening(object? sender, EventArgs e)
    {
        if (!_holding)
        {
            LayoutUpdated -= PlaceAtOpening;
            return;
        }
        var size = Bounds.Size;
        if (size.Width <= 0 || Mark.Bounds.Width <= 0 || Mark.Bounds.Height <= 0 || MarkCentre() is not { } centre) return;
        // Where SplashArt draws it: a third of the window wide, a little above the middle.
        var aspect = Mark.Bounds.Width / Mark.Bounds.Height;
        var width = Math.Min(size.Width * 0.27, size.Height * 0.34 * aspect);
        MarkHost.RenderTransform = Ops(width / Mark.Bounds.Width, size.Width / 2 - centre.X, size.Height * 0.44 - centre.Y);
    }

    /// <summary>The bird's resting centre, where the layout puts it (its own transform aside).</summary>
    private Point? MarkCentre() => Hello.TranslatePoint(MarkHost.Bounds.Center, this);

    /// <summary>
    /// Shows it. With the place the opening drew the bird at, the bird is taken over from
    /// there and settles above the greeting while the colour dawns; without it, everything
    /// rises gently into place.
    /// </summary>
    public void Enter((PixelPoint Centre, double Width)? logo)
    {
        if (_entered) return;
        _entered = true;
        var held = _holding;
        _holding = false;
        Focus();
        if (!Motion.Enabled) return;

        _still = false;
        DispatcherTimer.RunOnce(() =>
        {
            _still = true;
            if (_coversWaiting is { } covers)
            {
                _coversWaiting = null;
                Wall.SetCovers(covers);
            }
        }, TimeSpan.FromMilliseconds(2400));

        var fromOpening = (held || logo != null) && Mark.Bounds.Width > 0;
        if (fromOpening)
        {
            // The opening fades on its dark ground: the colour comes up from under it.
            Night.Opacity = 1;
            Animate(Night, null, 0, delayMs: 160, durationMs: 1500, Motion.Emphasized, opacityShare: 1);
        }
        Animate(Wall, null, WallHello, delayMs: fromOpening ? 420 : 120, durationMs: 1900, Motion.Emphasized, opacityShare: 1);

        var settled = Ops(1, 0, 0);
        if (fromOpening && MarkCentre() is { } c)
        {
            // Where the opening really drew it, when it said; else where it was waiting.
            if (logo is { } at)
            {
                var from = this.PointToClient(at.Centre);
                MarkHost.RenderTransform = Ops(at.Width / Mark.Bounds.Width, from.X - c.X, from.Y - c.Y);
            }
            MarkHost.Opacity = 1;
            Animate(MarkHost, settled, 1, delayMs: 140, durationMs: 1050, Motion.Emphasized);
        }
        else
        {
            MarkHost.RenderTransform = Ops(0.82, 0, 12);
            Animate(MarkHost, settled, 1, delayMs: 40, durationMs: 800, Motion.Emphasized);
        }

        var delay = fromOpening ? 560 : 180;
        foreach (var element in HelloWords)
        {
            Rise(element, delay);
            delay += 90;
        }
    }

    private static TransformOperations Ops(double scale, double x, double y) =>
        TransformOperations.Parse(string.Format(CultureInfo.InvariantCulture,
            "scale({0:0.####}) translateX({1:0.##}px) translateY({2:0.##}px)", scale, x, y));

    /// <summary>Fades and lifts in from a little below.</summary>
    private static void Rise(Control element, int delayMs)
    {
        element.Opacity = 0;
        element.RenderTransform = TransformOperations.Parse("translateY(18px)");
        Animate(element, TransformOperations.Parse("translateY(0px)"), 1, delayMs, 620, Motion.Emphasized);
    }

    /// <summary>
    /// From what is on screen to the given state, after a pause. The first values reach the
    /// screen before the change, so the transitions have something to move from.
    /// </summary>
    private static void Animate(Control element, TransformOperations? to, double opacity,
        int delayMs, int durationMs, Easing easing, double opacityShare = 0.6)
    {
        element.Transitions = null;
        DispatcherTimer.RunOnce(() =>
        {
            element.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(durationMs * opacityShare),
                    Easing = opacityShare >= 1 ? easing : new CubicEaseOut()
                },
                new TransformOperationsTransition
                {
                    Property = RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(durationMs),
                    Easing = easing
                }
            };
            element.Opacity = opacity;
            if (to != null) element.RenderTransform = to;
        }, TimeSpan.FromMilliseconds(Math.Max(16, delayMs)));
    }

    // ---- Hello ----

    private void GetStarted_Click(object? sender, RoutedEventArgs e) => ShowTaste();

    private void ShowTaste()
    {
        if (_leaving || Taste.IsVisible) return;
        Hello.IsHitTestVisible = false;
        Taste.IsVisible = true;
        if (!Motion.Enabled)
        {
            Hello.IsVisible = false;
            Wall.Opacity = WallTaste;
            return;
        }

        Animate(Hello, TransformOperations.Parse("translateY(-28px)"), 0, 0, 360, Motion.Emphasized);
        DispatcherTimer.RunOnce(() => Hello.IsVisible = false, TimeSpan.FromMilliseconds(380));
        Animate(Wall, null, WallTaste, 0, 900, Motion.Emphasized, opacityShare: 1);
        Rise(TasteTitle, 140);
        Rise(TasteLine, 220);
        Rise(SearchHost, 300);
        Rise(TasteBar, 380);
    }

    private void SignIn_Click(object? sender, RoutedEventArgs e)
    {
        // Without the runtime the sign-in window cannot draw: say so instead of failing.
        if (!SoundLibert.UI.Services.WebViewRuntime.IsAvailable())
        {
            SignInNote.Text = "Sign-in needs the Microsoft Edge WebView2 Runtime. You can sign in later in Settings.";
            SignInNote.IsVisible = true;
            return;
        }

        try
        {
            var account = App.ServiceProvider?.GetService(typeof(IAccountService)) as IAccountService;
            var login = new LoginWindow();
            login.Closed += (_, _) =>
            {
                // Their library brings their taste: nothing left to choose.
                if (account?.IsSignedIn == true) Finish();
            };
            if (TopLevel.GetTopLevel(this) is Window owner) _ = login.ShowDialog(owner);
            else login.Show();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Welcome] sign-in failed to open: {ex.Message}");
            SignInNote.Text = "The sign-in window didn't open. You can sign in later in Settings.";
            SignInNote.IsVisible = true;
        }
    }

    // ---- Your artists ----

    private void Artist_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WelcomeArtistVm artist }) _vm?.Toggle(artist);
    }

    private void ArtistSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchDelay?.Dispose();
        var text = ArtistSearch.Text;
        _searchDelay = DispatcherTimer.RunOnce(() =>
        {
            if (_vm != null) _ = _vm.SearchAsync(text);
            ArtistScroll.Offset = default;
        }, TimeSpan.FromMilliseconds(string.IsNullOrWhiteSpace(text) ? 0 : 350));
    }

    /// <summary>
    /// Rows fade as they leave the list at its top and bottom. An opacity mask would do the
    /// same, but over moving pictures it costs frames.
    /// </summary>
    private void FadeEdges()
    {
        var viewport = ArtistScroll.Bounds.Height;
        if (viewport <= 0 || ArtistList.ItemsPanelRoot is not { } panel) return;
        foreach (var item in panel.Children)
        {
            if (item.TranslatePoint(default, ArtistScroll) is not { } at) continue;
            var reach = item.Bounds.Height * 0.6;
            var above = (at.Y + reach) / (reach + 10);
            var below = (viewport - at.Y - item.Bounds.Height + reach) / (reach + 10);
            item.Opacity = Math.Clamp(Math.Min(above, below), 0, 1);
        }
    }

    private void Done_Click(object? sender, RoutedEventArgs e) => Finish();

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (_leaving) return;
        if (e.Key == Key.Enter && !Taste.IsVisible)
        {
            ShowTaste();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Taste.IsVisible && !string.IsNullOrEmpty(ArtistSearch.Text))
        {
            ArtistSearch.Text = string.Empty;
            e.Handled = true;
        }
    }

    // ---- Leaving ----

    /// <summary>Keeps the choice, then fades into the app.</summary>
    private void Finish()
    {
        if (_leaving) return;
        _leaving = true;
        IsHitTestVisible = false;
        _searchDelay?.Dispose();
        _cts.Cancel();
        _vm?.Commit();
        Finished?.Invoke();

        if (!Motion.Enabled)
        {
            Gone?.Invoke();
            return;
        }
        Animate(this, TransformOperations.Parse("scale(1.04)"), 0, 0, 520, Motion.Emphasized);
        DispatcherTimer.RunOnce(() => Gone?.Invoke(), TimeSpan.FromMilliseconds(560));
    }

    // ---- QA (SL_SHOT=welcome) ----

    internal void QaNext() => ShowTaste();

    internal void QaChoose(int count)
    {
        if (_vm == null) return;
        foreach (var artist in _vm.Shown.Where(a => !a.IsChosen).Take(count).ToList()) _vm.Toggle(artist);
    }

    internal void QaSearch(string text) => ArtistSearch.Text = text;

    internal void QaDone() => Finish();

    internal IReadOnlyList<string> QaShownNames() => _vm?.Shown.Select(a => a.Name).ToList() ?? new List<string>();
}
