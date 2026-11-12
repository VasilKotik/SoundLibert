using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using SoundLibert.Domain.Models;
using SoundLibert.UI.ViewModels;

namespace SoundLibert.UI.Controls;

/// <summary>
/// One thing worth a look at the top of Home: the newest release of an artist you follow,
/// what is number one right now. It moves on by itself and waits while the pointer is on it.
/// </summary>
public partial class Spotlight : UserControl
{
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(8);

    public static readonly StyledProperty<IReadOnlyList<SpotlightEntry>?> EntriesProperty =
        AvaloniaProperty.Register<Spotlight, IReadOnlyList<SpotlightEntry>?>(nameof(Entries));

    public IReadOnlyList<SpotlightEntry>? Entries
    {
        get => GetValue(EntriesProperty);
        set => SetValue(EntriesProperty, value);
    }

    private readonly DispatcherTimer _timer = new() { Interval = Dwell };
    private int _index;
    private int _shown;
    private bool _pointerOver;

    public Spotlight()
    {
        InitializeComponent();
        _timer.Tick += (_, _) =>
        {
            if (!_pointerOver && Motion.Enabled) ShowAt(_index + 1, animate: true);
        };
        PointerEntered += (_, _) => _pointerOver = true;
        PointerExited += (_, _) => _pointerOver = false;
    }

    private bool? _compact;

    /// <summary>On a phone the block keeps its shape, smaller: the cover and the words side by side still.</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var compact = e.NewSize.Width < 560;
        if (compact == _compact) return;
        _compact = compact;

        Card.Height = compact ? 196 : 244;
        Body.Margin = compact ? new Thickness(18, 0) : new Thickness(30, 0);
        Body.ColumnSpacing = compact ? 18 : 28;
        var art = compact ? 132 : 184;
        ArtShadow.Width = ArtShadow.Height = art;
        TitleText.FontSize = compact ? 23 : 34;
        TitleText.LetterSpacing = compact ? -0.4 : -0.7;
        TitleText.MaxLines = compact ? 3 : 2;
        SubtitleText.FontSize = compact ? 13 : 14;
        Dots.Margin = compact ? new Thickness(0, 0, 16, 14) : new Thickness(0, 0, 22, 18);
    }

    private MainWindowViewModel? Host =>
        SoundLibert.UI.Services.AppServices.HostOf(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != EntriesProperty) return;

        _index = 0;
        BuildDots();
        ShowAt(0, animate: false);

        // The first one gets its full time too.
        _timer.Stop();
        if (TopLevel.GetTopLevel(this) != null) _timer.Start();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    private void BuildDots()
    {
        Dots.Children.Clear();
        var count = Entries?.Count ?? 0;
        if (count < 2) return;

        for (var i = 0; i < count; i++)
        {
            var index = i;
            var dot = new Border
            {
                Height = 6,
                Width = 6,
                CornerRadius = new CornerRadius(3),
                Cursor = new Cursor(StandardCursorType.Hand),
                Transitions = new Transitions
                {
                    new DoubleTransition { Property = WidthProperty, Duration = TimeSpan.FromMilliseconds(280), Easing = Motion.Emphasized }
                }
            };
            dot.Tapped += (_, e) =>
            {
                e.Handled = true;
                ShowAt(index, animate: true);
                _timer.Stop();
                _timer.Start();
            };
            Dots.Children.Add(dot);
        }
    }

    private void ShowAt(int index, bool animate)
    {
        var entries = Entries;
        if (entries is not { Count: > 0 }) return;

        _index = (index % entries.Count + entries.Count) % entries.Count;
        var entry = entries[_index];
        var shown = ++_shown;

        for (var i = 0; i < Dots.Children.Count; i++)
        {
            if (Dots.Children[i] is not Border dot) continue;
            dot.Width = i == _index ? 20 : 6;
            dot.Background = new SolidColorBrush(Colors.White, i == _index ? 0.9 : 0.3);
        }

        if (!animate || !Motion.Enabled)
        {
            Fill(entry);
            Body.Opacity = 1;
            return;
        }

        Body.Opacity = 0;
        DispatcherTimer.RunOnce(() =>
        {
            // Something else has been shown since: the feed changed, a dot was tapped.
            if (shown != _shown) return;
            Fill(entry);
            Body.Opacity = 1;
        }, TimeSpan.FromMilliseconds(240));
    }

    private void Fill(SpotlightEntry entry)
    {
        var card = entry.Card;
        Glow.Url = card.ThumbnailUrl;
        Art.Url = card.ThumbnailUrl;
        EyebrowText.Text = entry.Eyebrow;
        TitleText.Text = card.Title;
        SubtitleText.Text = card.Subtitle;

        var radius = new CornerRadius(card.IsArtist ? 92 : 14);
        ArtShadow.CornerRadius = radius;
        ArtClip.CornerRadius = radius;
        PlayButton.IsVisible = card.CanPlay;
    }

    private SpotlightEntry? Current =>
        Entries is { Count: > 0 } entries && _index < entries.Count ? entries[_index] : null;

    private void Play_Click(object? sender, RoutedEventArgs e)
    {
        if (Current is { } entry) Host?.PlayShelfCard(entry.Card);
    }

    private void Open_Click(object? sender, RoutedEventArgs e)
    {
        if (Current is { } entry) Host?.OpenShelfCard(entry.Card);
    }
}
