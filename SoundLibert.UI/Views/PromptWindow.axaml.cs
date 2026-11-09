using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SoundLibert.UI.Views;

/// <summary>
/// A tiny modal text prompt (used for "name your playlist"). Avoids pulling in
/// a dialog library: show it with <see cref="ShowAsync"/> and await the text.
/// </summary>
public partial class PromptWindow : Window
{
    private bool _accepted;

    public PromptWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            Card.Opacity = 1;
            Card.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1)");
        };
    }

    /// <summary>Asks before something that cannot be undone. True when confirmed.</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string confirm)
    {
        var window = new PromptWindow();
        window.TitleText.Text = title;
        window.MessageText.Text = message;
        window.MessageText.IsVisible = true;
        window.Input.IsVisible = false;
        window.OkButton.Content = confirm;
        await window.ShowDialog(owner);
        return window._accepted;
    }

    /// <summary>Shows the prompt centered on <paramref name="owner"/>; null when cancelled.</summary>
    public static async Task<string?> ShowAsync(Window owner, string title,
        string initial = "", string placeholder = "")
    {
        var window = new PromptWindow();
        window.TitleText.Text = title;
        window.Input.Text = initial;
        window.Input.PlaceholderText = placeholder;
        window.Opened += (_, _) =>
        {
            window.Input.Focus();
            window.Input.SelectAll();
        };

        await window.ShowDialog(owner);
        return window._accepted ? window.Input.Text?.Trim() : null;
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => Accept();

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    private void Input_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Accept();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void Accept()
    {
        _accepted = true;
        Close();
    }
}
