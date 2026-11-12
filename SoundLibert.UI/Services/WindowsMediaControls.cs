namespace SoundLibert.UI.Services;

/// <summary>
/// Windows' media controls for the shared player: the media overlay and keys (SMTC)
/// and the progress on the taskbar button.
/// </summary>
internal sealed class WindowsMediaControls : ISystemMediaControls
{
    private readonly SmtcService _smtc = new();
    private readonly TaskbarProgress _taskbar;

    public WindowsMediaControls(IntPtr hwnd)
    {
        try
        {
            _smtc.Initialize(hwnd);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[SMTC] wire failed: {ex.Message}");
        }
        _taskbar = new TaskbarProgress(hwnd);
    }

    public Action? PlayRequested { get => _smtc.PlayRequested; set => _smtc.PlayRequested = value; }
    public Action? PauseRequested { get => _smtc.PauseRequested; set => _smtc.PauseRequested = value; }
    public Action? NextRequested { get => _smtc.NextRequested; set => _smtc.NextRequested = value; }
    public Action? PreviousRequested { get => _smtc.PreviousRequested; set => _smtc.PreviousRequested = value; }

    public void SetNowPlaying(string? title, string? artist, string? album, string? coverUrl)
        => _smtc.SetNowPlaying(title, artist, album, coverUrl);

    public void SetPlaybackState(bool isPlaying, bool hasTrack) => _smtc.SetPlaybackState(isPlaying, hasTrack);

    public void SetProgress(SystemProgressState state, double fraction) => _taskbar.Update(state switch
    {
        SystemProgressState.Loading => TaskbarProgress.TaskbarState.Indeterminate,
        SystemProgressState.Playing => TaskbarProgress.TaskbarState.Normal,
        SystemProgressState.Paused => TaskbarProgress.TaskbarState.Paused,
        _ => TaskbarProgress.TaskbarState.None
    }, fraction);
}
