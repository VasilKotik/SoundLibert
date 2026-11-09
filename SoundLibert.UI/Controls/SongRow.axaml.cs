using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using SoundLibert.Domain.Models;

namespace SoundLibert.UI.Controls;

/// <summary>
/// The one song row of the app. The DataContext is the <see cref="SongItem"/>;
/// the page decides what its buttons do (<see cref="LikeClick"/>, <see cref="MoreClick"/>,
/// with the inner button as sender and the song as its Tag) and what a tap on the
/// row does.
/// </summary>
public partial class SongRow : UserControl
{
    /// <summary>Show the album under the title instead of the artist (an artist's own page).</summary>
    public static readonly StyledProperty<bool> ShowAlbumProperty =
        AvaloniaProperty.Register<SongRow, bool>(nameof(ShowAlbum));

    /// <summary>Entrance animation; off in virtualised lists, where rows appear while scrolling.</summary>
    public static readonly StyledProperty<bool> RevealProperty =
        AvaloniaProperty.Register<SongRow, bool>(nameof(Reveal));

    /// <summary>
    /// Set on a list: its rows show the track number instead of the cover (an album's
    /// own page, where the cover would repeat twenty times). Inherited by the rows.
    /// </summary>
    public static readonly AttachedProperty<bool> NumberRowsProperty =
        AvaloniaProperty.RegisterAttached<SongRow, Control, bool>("NumberRows", inherits: true);

    public static bool GetNumberRows(Control control) => control.GetValue(NumberRowsProperty);
    public static void SetNumberRows(Control control, bool value) => control.SetValue(NumberRowsProperty, value);

    public bool ShowAlbum { get => GetValue(ShowAlbumProperty); set => SetValue(ShowAlbumProperty, value); }
    private bool ShowIndex => GetValue(NumberRowsProperty);
    public bool Reveal { get => GetValue(RevealProperty); set => SetValue(RevealProperty, value); }

    public event EventHandler<RoutedEventArgs>? LikeClick;
    public event EventHandler<RoutedEventArgs>? MoreClick;

    public SongRow()
    {
        InitializeComponent();
        LikeButton.Click += (_, e) =>
        {
            Motion.Pop(LikeGlyph);
            if (DataContext is SoundLibert.Domain.Models.SongItem { IsLiked: false }) Motion.Burst(LikeGlyph);
            LikeClick?.Invoke(LikeButton, e);
        };
        MoreButton.Click += (_, e) => MoreClick?.Invoke(MoreButton, e);
        DataContextChanged += (_, _) => UpdateIndex();
    }

    private void UpdateIndex()
    {
        Cover.IsVisible = !ShowIndex;
        IndexText.IsVisible = ShowIndex;
        Thumb.Background = ShowIndex ? Avalonia.Media.Brushes.Transparent : null;
        RowGrid.ColumnDefinitions[0].Width = new GridLength(ShowIndex ? 30 : 44);
        Thumb.Width = ShowIndex ? 30 : 44;
        if (!ShowIndex || DataContext is null) return;

        var list = this.FindLogicalAncestorOfType<ItemsControl>();
        var index = list?.Items.IndexOf(DataContext) ?? -1;
        IndexText.Text = index >= 0 ? (index + 1).ToString() : string.Empty;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowAlbumProperty && ShowAlbum)
            SubtitleText.Bind(TextBlock.TextProperty, new Binding(nameof(SongItem.AlbumTitle)));
        else if (change.Property == NumberRowsProperty)
            UpdateIndex();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (ShowIndex) UpdateIndex();
        if (Reveal) Motion.Reveal(this);
    }
}
