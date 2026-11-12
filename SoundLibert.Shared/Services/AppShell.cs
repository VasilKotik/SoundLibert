using Avalonia;
using Avalonia.Controls;
using SoundLibert.Domain.Interfaces;
using SoundLibert.UI.ViewModels;

namespace SoundLibert.UI.Services;

/// <summary>
/// What each platform gives the shared screens: its dialogs, its sign-in and its
/// welcome. Windows answers with windows; Android with sheets over the one view it has.
/// </summary>
public interface IAppShell
{
    /// <summary>Asks for a line of text (a playlist's name). Null when cancelled.</summary>
    Task<string?> AskAsync(Visual owner, string title, string initial = "", string placeholder = "");

    /// <summary>Asks before something that cannot be undone. True when confirmed.</summary>
    Task<bool> ConfirmAsync(Visual owner, string title, string message, string confirm);

    /// <summary>Opens the bug report: what will be shared, then the issue page.</summary>
    Task ReportBugAsync(Visual owner, UpdateService updates, IAppSettings settings, Action<string>? notify);

    /// <summary>Why sign-in cannot open here (a missing web runtime), or null when it can.</summary>
    string? SignInBlocker { get; }

    /// <summary>Opens the Google sign-in; completes when it closes, signed in or not.</summary>
    Task SignInAsync(Visual owner);

    /// <summary>Deletes the sign-in's own browser data (its Google session). Runs off the UI thread.</summary>
    void ForgetSignIn();

    /// <summary>Shows the first-run welcome again.</summary>
    void ShowWelcome(Visual owner);
}

/// <summary>Where the progress of a song stands, for a taskbar button or the like.</summary>
public enum SystemProgressState { None, Loading, Playing, Paused }

/// <summary>
/// The system's own media controls: Windows' media overlay, keys and taskbar button;
/// Android's notification, lock screen and headset buttons.
/// </summary>
public interface ISystemMediaControls
{
    Action? PlayRequested { get; set; }
    Action? PauseRequested { get; set; }
    Action? NextRequested { get; set; }
    Action? PreviousRequested { get; set; }

    void SetNowPlaying(string? title, string? artist, string? album, string? coverUrl);
    void SetPlaybackState(bool isPlaying, bool hasTrack);
    void SetProgress(SystemProgressState state, double fraction);
}

/// <summary>The platform's services for code that cannot take them by injection (controls, views).</summary>
public static class AppServices
{
    /// <summary>The app's main view model where no window carries it (a phone has no window).</summary>
    public static MainWindowViewModel? Host { get; set; }

    /// <summary>The main view model for a view: its window's, else <see cref="Host"/>.</summary>
    public static MainWindowViewModel? HostOf(Visual visual) =>
        TopLevel.GetTopLevel(visual)?.DataContext as MainWindowViewModel ?? Host;

    /// <summary>Nobody can see the app (a phone app in the background): moving pictures rest.</summary>
    public static bool Hidden { get; set; }

    public static IServiceProvider? Provider { get; set; }

    public static IAppShell Shell { get; set; } = null!;
}
