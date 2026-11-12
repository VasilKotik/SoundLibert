using Avalonia;
using Avalonia.Controls;

namespace SoundLibert.UI.Controls;

/// <summary>
/// The header of an album, playlist, collection or artist page: cover, colour wash
/// from that cover, name, details and the page's actions.
/// </summary>
public partial class PageHero : UserControl
{
    public static readonly StyledProperty<string?> EyebrowProperty =
        AvaloniaProperty.Register<PageHero, string?>(nameof(Eyebrow));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<PageHero, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<PageHero, string?>(nameof(Subtitle));

    public static readonly StyledProperty<string?> MetaProperty =
        AvaloniaProperty.Register<PageHero, string?>(nameof(Meta));

    public static readonly StyledProperty<string?> ImageUrlProperty =
        AvaloniaProperty.Register<PageHero, string?>(nameof(ImageUrl));

    public static readonly StyledProperty<IReadOnlyList<string>?> ImageUrlsProperty =
        AvaloniaProperty.Register<PageHero, IReadOnlyList<string>?>(nameof(ImageUrls));

    public static readonly StyledProperty<IconKind> GlyphProperty =
        AvaloniaProperty.Register<PageHero, IconKind>(nameof(Glyph), IconKind.Music);

    public static readonly StyledProperty<bool> IsRoundProperty =
        AvaloniaProperty.Register<PageHero, bool>(nameof(IsRound));

    public static readonly StyledProperty<double> ArtSizeProperty =
        AvaloniaProperty.Register<PageHero, double>(nameof(ArtSize), 200);

    public static readonly StyledProperty<Control?> ActionsProperty =
        AvaloniaProperty.Register<PageHero, Control?>(nameof(Actions));

    public string? Eyebrow { get => GetValue(EyebrowProperty); set => SetValue(EyebrowProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public string? Meta { get => GetValue(MetaProperty); set => SetValue(MetaProperty, value); }
    public string? ImageUrl { get => GetValue(ImageUrlProperty); set => SetValue(ImageUrlProperty, value); }
    public IReadOnlyList<string>? ImageUrls { get => GetValue(ImageUrlsProperty); set => SetValue(ImageUrlsProperty, value); }
    public IconKind Glyph { get => GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public bool IsRound { get => GetValue(IsRoundProperty); set => SetValue(IsRoundProperty, value); }
    public double ArtSize { get => GetValue(ArtSizeProperty); set => SetValue(ArtSizeProperty, value); }
    public Control? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    public PageHero()
    {
        InitializeComponent();
        UpdateText();
        UpdateArt();
    }

    private bool? _narrow;

    /// <summary>On a phone the cover is smaller and the name a size down, so a long word stays whole.</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < 640;
        if (narrow == _narrow) return;
        _narrow = narrow;

        var side = narrow ? 136 : 200;
        ArtShadow.Width = ArtShadow.Height = side;
        Layout.ColumnSpacing = narrow ? 18 : 28;
        if (narrow) TitleText.FontSize = 28;
        else TitleText.ClearValue(TextBlock.FontSizeProperty);
        GlyphIcon.Size = narrow ? 52 : 72;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == EyebrowProperty || change.Property == TitleProperty ||
            change.Property == SubtitleProperty || change.Property == MetaProperty)
        {
            UpdateText();
        }
        else if (change.Property == ImageUrlProperty || change.Property == ImageUrlsProperty ||
                 change.Property == GlyphProperty)
        {
            UpdateArt();
        }
        else if (change.Property == IsRoundProperty)
        {
            UpdateRadius();
        }
        else if (change.Property == ArtSizeProperty)
        {
            ArtShadow.Width = ArtSize;
            ArtShadow.Height = ArtSize;
            GlyphIcon.Size = ArtSize * 0.36;
            UpdateRadius();
        }
        else if (change.Property == ActionsProperty)
        {
            ActionsHost.Child = Actions;
            ActionsHost.IsVisible = Actions != null;
        }
    }

    /// <summary>The exact radius: a shadow under an oversized one renders as a box.</summary>
    private void UpdateRadius()
    {
        var radius = new CornerRadius(IsRound ? ArtSize / 2 : 14);
        ArtShadow.CornerRadius = radius;
        ArtClip.CornerRadius = radius;
    }

    private void UpdateText()
    {
        Set(EyebrowText, Eyebrow);
        TitleText.Text = Title;
        Set(SubtitleText, Subtitle);
        Set(MetaText, Meta);
    }

    private static void Set(TextBlock block, string? text)
    {
        block.Text = text;
        block.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private void UpdateArt()
    {
        var hasImage = !string.IsNullOrWhiteSpace(ImageUrl) || ImageUrls is { Count: > 0 };
        Cover.Url = ImageUrl;
        Cover.Urls = ImageUrls;
        Cover.IsVisible = hasImage;
        GlyphIcon.Kind = Glyph;
        GlyphIcon.IsVisible = !hasImage;
        Glow.Url = ImageUrls is { Count: > 0 } urls ? urls[0] : ImageUrl;
    }
}
