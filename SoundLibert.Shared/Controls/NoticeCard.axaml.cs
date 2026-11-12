using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace SoundLibert.UI.Controls;

/// <summary>What a notice says and offers.</summary>
public sealed record Notice(
    string Title,
    string Body,
    string PrimaryText,
    Action Primary,
    string SecondaryText,
    Action? Secondary = null,
    string? Notes = null,
    IconKind? Symbol = null);

/// <summary>
/// A card that rises above the player in the corner: a new version is ready, or a gentle
/// question. One at a time, it stays until answered, and it never covers the music's controls.
/// </summary>
public partial class NoticeCard : UserControl
{
    private static readonly TransformOperations Away = TransformOperations.Parse("translateY(18px) scale(0.98)");
    private static readonly TransformOperations Here = TransformOperations.Parse("translateY(0px) scale(1)");

    private Notice? _notice;
    private IDisposable? _hiding;

    public NoticeCard()
    {
        InitializeComponent();
    }

    /// <summary>Something is showing and waits for an answer.</summary>
    public bool IsShowing => _notice != null;

    public void Show(Notice notice)
    {
        _hiding?.Dispose();
        _notice = notice;
        TitleText.Text = notice.Title;
        BodyText.Text = notice.Body;
        NotesText.Text = notice.Notes;
        NotesText.IsVisible = !string.IsNullOrWhiteSpace(notice.Notes);
        PrimaryButton.Content = notice.PrimaryText;
        SecondaryButton.Content = notice.SecondaryText;
        Brand.IsVisible = notice.Symbol == null;
        SymbolDisc.IsVisible = notice.Symbol != null;
        if (notice.Symbol is { } symbol)
        {
            Symbol.Kind = symbol;
            Symbol.IsFilled = true;
        }

        IsVisible = true;
        if (!Motion.Enabled)
        {
            Opacity = 1;
            RenderTransform = Here;
            return;
        }
        // From below, after its starting place has been drawn.
        Dispatcher.UIThread.Post(() =>
        {
            Opacity = 1;
            RenderTransform = Here;
        }, DispatcherPriority.Background);
    }

    public void Hide()
    {
        _notice = null;
        Opacity = 0;
        RenderTransform = Away;
        _hiding = DispatcherTimer.RunOnce(() => IsVisible = false, TimeSpan.FromMilliseconds(Motion.Enabled ? 440 : 0));
    }

    private void Primary_Click(object? sender, RoutedEventArgs e)
    {
        var notice = _notice;
        Hide();
        notice?.Primary();
    }

    private void Secondary_Click(object? sender, RoutedEventArgs e)
    {
        var notice = _notice;
        Hide();
        notice?.Secondary?.Invoke();
    }
}
