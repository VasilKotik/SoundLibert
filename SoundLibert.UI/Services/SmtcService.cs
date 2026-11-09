#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Media;
using WinRT;

namespace SoundLibert.UI.Services;

/// <summary>
/// Publishes the current track to the Windows System Media Transport Controls
/// (lock screen / volume flyout / hardware media keys overlay). A Win32 desktop
/// app cannot use <c>GetForCurrentView</c> (UWP only); it must QI the runtime
/// class activation factory for <see cref="ISystemMediaTransportControlsInterop"/>
/// and call <c>GetForWindow</c>. Everything is best-effort: if anything fails the
/// app keeps working without the overlay.
/// </summary>
public sealed class SmtcService
{
    private SystemMediaTransportControls? _smtc;

    public Action? PlayRequested { get; set; }
    public Action? PauseRequested { get; set; }
    public Action? NextRequested { get; set; }
    public Action? PreviousRequested { get; set; }

    public bool IsActive => _smtc != null;

    // ISystemMediaTransportControlsInterop and ISystemMediaTransportControls.
    private static readonly Guid InteropIid = new("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a");
    private static readonly Guid SmtcIid = new("99fa3ff4-1742-42a6-902e-087d41f965ec");

    /// <summary>
    /// CsWinRT cannot marshal a classic [ComImport] interop interface
    /// ("WinRT.IInspectable cannot be assigned to ..." on every start), so
    /// QI the activation factory and call GetForWindow (vtable slot 6, after
    /// IUnknown ×3 + IInspectable ×3) directly.
    /// </summary>
    private static unsafe SystemMediaTransportControls GetForWindow(IntPtr hwnd)
    {
        var factory = ActivationFactory.Get("Windows.Media.SystemMediaTransportControls");
        var interopIid = InteropIid;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in interopIid, out var interop));
        try
        {
            var smtcIid = SmtcIid;
            IntPtr result;
            var getForWindow = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)interop)[6];
            Marshal.ThrowExceptionForHR(getForWindow(interop, hwnd, &smtcIid, &result));
            try { return SystemMediaTransportControls.FromAbi(result); }
            finally { Marshal.Release(result); }
        }
        finally
        {
            Marshal.Release(interop);
        }
    }

    public void Initialize(IntPtr hwnd)
    {
        if (_smtc != null || hwnd == IntPtr.Zero) return;
        try
        {
            _smtc = GetForWindow(hwnd);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SMTC] init failed: {ex.Message}");
            return;
        }

        try
        {
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;
            _smtc.IsStopEnabled = true;
            _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
            _smtc.ButtonPressed += OnButtonPressed;
            Trace.WriteLine("[SMTC] initialized");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SMTC] configure failed: {ex.Message}");
            _smtc = null;
        }
    }

    public void SetNowPlaying(string? title, string? artist, string? album, string? thumbnailUrl)
    {
        if (_smtc == null) return;
        try
        {
            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            var music = updater.MusicProperties;
            music.Title = title ?? string.Empty;
            music.Artist = artist ?? string.Empty;
            music.AlbumTitle = album ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(thumbnailUrl) && Uri.TryCreate(thumbnailUrl, UriKind.Absolute, out var uri))
                updater.Thumbnail = Windows.Storage.Streams.RandomAccessStreamReference.CreateFromUri(uri);
            updater.Update();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SMTC] metadata failed: {ex.Message}");
        }
    }

    public void SetPlaybackState(bool isPlaying, bool hasTrack)
    {
        if (_smtc == null) return;
        try
        {
            _smtc.PlaybackStatus = !hasTrack
                ? MediaPlaybackStatus.Stopped
                : isPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SMTC] state failed: {ex.Message}");
        }
    }

    public void Clear()
    {
        if (_smtc == null) return;
        try
        {
            _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
            _smtc.DisplayUpdater.ClearAll();
            _smtc.DisplayUpdater.Update();
        }
        catch { }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        try
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play: PlayRequested?.Invoke(); break;
                case SystemMediaTransportControlsButton.Pause: PauseRequested?.Invoke(); break;
                case SystemMediaTransportControlsButton.Stop: PauseRequested?.Invoke(); break;
                case SystemMediaTransportControlsButton.Next: NextRequested?.Invoke(); break;
                case SystemMediaTransportControlsButton.Previous: PreviousRequested?.Invoke(); break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SMTC] button failed: {ex.Message}");
        }
    }
}

#endif
