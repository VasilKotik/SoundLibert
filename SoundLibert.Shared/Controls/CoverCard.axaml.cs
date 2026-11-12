using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SoundLibert.UI.Controls;

/// <summary>
/// The one card of the app: album, playlist, mix, artist (round) or video (wide).
/// Used by Home, Search, Library and the artist pages, so a card looks and moves the
/// same everywhere. The hover play button carries the class <c>cardPlay</c>; the main
/// window plays the card's item from it unless <see cref="PlayClick"/> is handled.
/// </summary>
public partial class CoverCard : UserControl
{
    public enum CardShape { Square, Round, Wide }

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<CoverCard, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<CoverCard, string?>(nameof(Subtitle));

    public static readonly StyledProperty<string?> ImageUrlProperty =
        AvaloniaProperty.Register<CoverCard, string?>(nameof(ImageUrl));

    public static readonly StyledProperty<IReadOnlyList<string>?> ImageUrlsProperty =
        AvaloniaProperty.Register<CoverCard, IReadOnlyList<string>?>(nameof(ImageUrls));

    public static readonly StyledProperty<CardShape> ShapeProperty =
        AvaloniaProperty.Register<CoverCard, CardShape>(nameof(Shape));

    public static readonly StyledProperty<string?> BadgeProperty =
        AvaloniaProperty.Register<CoverCard, string?>(nameof(Badge));

    public static readonly StyledProperty<bool> CanPlayProperty =
        AvaloniaProperty.Register<CoverCard, bool>(nameof(CanPlay), true);

    public static readonly StyledProperty<bool> CanManageProperty =
        AvaloniaProperty.Register<CoverCard, bool>(nameof(CanManage));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public string? ImageUrl { get => GetValue(ImageUrlProperty); set => SetValue(ImageUrlProperty, value); }
    public IReadOnlyList<string>? ImageUrls { get => GetValue(ImageUrlsProperty); set => SetValue(ImageUrlsProperty, value); }
    public CardShape Shape { get => GetValue(ShapeProperty); set => SetValue(ShapeProperty, value); }
    public string? Badge { get => GetValue(BadgeProperty); set => SetValue(BadgeProperty, value); }
    public bool CanPlay { get => GetValue(CanPlayProperty); set => SetValue(CanPlayProperty, value); }
    public bool CanManage { get => GetValue(CanManageProperty); set => SetValue(CanManageProperty, value); }

    /// <summary>The hover play button. The sender is the button, its Tag this card's Tag.</summary>
    public event EventHandler<RoutedEventArgs>? PlayClick;

    /// <summary>The hover "more" button of a card that can be managed.</summary>
    public event EventHandler<RoutedEventArgs>? MoreClick;

    public CoverCard()
    {
        InitializeComponent();
        PlayButton.Click += (_, e) =>
        {
            PlayButton.Tag = Tag;
            PlayClick?.Invoke(PlayButton, e);
        };
        MoreButton.Click += (_, e) => MoreClick?.Invoke(MoreButton, e);
        // A round card needs its exact radius: a shadow under an oversized radius
        // renders as a box.
        ArtBox.SizeChanged += (_, e) =>
        {
            if (Shape != CardShape.Round) return;
            var radius = new CornerRadius(e.NewSize.Width / 2);
            ArtShadow.CornerRadius = radius;
            ArtClip.CornerRadius = radius;
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TitleProperty)
        {
            TitleText.Text = Title;
        }
        else if (change.Property == SubtitleProperty)
        {
            SubtitleText.Text = Subtitle;
            SubtitleText.IsVisible = !string.IsNullOrEmpty(Subtitle);
        }
        else if (change.Property == ImageUrlProperty)
        {
            Cover.Url = ImageUrl;
            UpdatePlaceholder();
        }
        else if (change.Property == ImageUrlsProperty)
        {
            Cover.Urls = ImageUrls;
            UpdatePlaceholder();
        }
        else if (change.Property == ShapeProperty)
        {
            Classes.Set("round", Shape == CardShape.Round);
            ArtBox.Ratio = Shape == CardShape.Wide ? 16.0 / 9 : 1;
            if (Shape == CardShape.Round)
            {
                Placeholder.Kind = IconKind.User;
                PlayButton.IsVisible = false;
            }
            else if (Shape == CardShape.Wide)
            {
                Placeholder.Kind = IconKind.Video;
                PlayButton.IsVisible = CanPlay;
            }
        }
        else if (change.Property == BadgeProperty)
        {
            BadgeText.Text = Badge;
            BadgeChip.IsVisible = !string.IsNullOrEmpty(Badge);
        }
        else if (change.Property == CanPlayProperty)
        {
            PlayButton.IsVisible = CanPlay && Shape != CardShape.Round;
        }
        else if (change.Property == CanManageProperty)
        {
            MoreButton.IsVisible = CanManage;
        }
    }

    private void UpdatePlaceholder() =>
        Placeholder.IsVisible = string.IsNullOrWhiteSpace(ImageUrl) && (ImageUrls is null || ImageUrls.Count == 0);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Motion.Reveal(this);
    }
}
