using Avalonia;
using Avalonia.Controls;
using SoundLibert.Domain.Interfaces;
using SoundLibert.UI.Views;

namespace SoundLibert.UI.Services;

/// <summary>The shared screens' dialogs on Windows: small windows over the main one.</summary>
internal sealed class DesktopShell : IAppShell
{
    private static Window? OwnerOf(Visual owner) => TopLevel.GetTopLevel(owner) as Window;

    public async Task<string?> AskAsync(Visual owner, string title, string initial = "", string placeholder = "")
        => OwnerOf(owner) is { } window ? await PromptWindow.ShowAsync(window, title, initial, placeholder) : null;

    public async Task<bool> ConfirmAsync(Visual owner, string title, string message, string confirm)
        => OwnerOf(owner) is { } window && await PromptWindow.ConfirmAsync(window, title, message, confirm);

    public Task ReportBugAsync(Visual owner, UpdateService updates, IAppSettings settings, Action<string>? notify)
        => OwnerOf(owner) is { } window ? BugReportWindow.ShowAsync(window, updates, settings, notify) : Task.CompletedTask;

    public string? SignInBlocker => WebViewRuntime.IsAvailable()
        ? null
        : "Sign-in needs the Microsoft Edge WebView2 Runtime. Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and try again.";

    public Task SignInAsync(Visual owner)
    {
        var login = new LoginWindow();
        var closed = new TaskCompletionSource();
        login.Closed += (_, _) => closed.TrySetResult();
        if (OwnerOf(owner) is { } window) _ = login.ShowDialog(window);
        else login.Show();
        return closed.Task;
    }

    public void ForgetSignIn() => WebViewProfile.Clear();

    public void ShowWelcome(Visual owner) => (TopLevel.GetTopLevel(owner) as MainWindow)?.ShowWelcome();
}
