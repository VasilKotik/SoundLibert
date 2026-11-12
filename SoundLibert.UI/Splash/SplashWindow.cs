using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using SoundLibert.Domain.Interfaces;
using SoundLibert.UI.Controls;

namespace SoundLibert.UI.Splash;

/// <summary>
/// The window that opens first, at once: it plays the opening (<see cref="SplashArt"/>) while
/// the app builds its own window behind it, then fades and leaves that window in its place.
/// Same size and place as the app's window, so nothing jumps. The drawing runs on the render
/// thread, so it stays smooth while the app is busy building.
/// </summary>
internal sealed class SplashWindow : Window
{
    private readonly SplashSurface _surface;

    /// <summary>
    /// Only for a window the user is about to see: not when the app starts in the tray, not for
    /// those who asked for less motion, and not in the screenshot runs (unless they ask for it).
    /// </summary>
    public static bool IsWanted(IAppSettings? settings)
    {
        var shot = Environment.GetEnvironmentVariable("SL_SHOT");
        if (shot is not null) return shot is "splash" or "welcome";
        return settings?.StartMinimized != true && settings?.ReduceMotion != true && WindowsAnimates();
    }

    // Asked before the window system starts, so not through Motion.
    private static bool WindowsAnimates()
    {
        const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
        try
        {
            return !OperatingSystem.IsWindows() || !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out var on, 0) || on;
        }
        catch
        {
            return true;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);

    public SplashWindow()
    {
        Title = "SoundLibert";
        Width = 1280;
        Height = 800;
        MinWidth = 1040;
        MinHeight = 660;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SoundLibert.UI/Assets/app.ico"))); } catch { }

        var art = SplashArt.Take(BrandArt.MarkData);
        art.NameFont = () =>
        {
            try
            {
                using var font = AssetLoader.Open(new Uri("avares://Avalonia.Fonts.Inter/Assets/Inter-Bold.ttf"));
                return SKTypeface.FromStream(font);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Splash] no Inter for the name: {ex.Message}");
                return null;
            }
        };
        _art = art;
        Content = _surface = new SplashSurface(art, () => Leaving?.Invoke());
    }

    private readonly SplashArt _art;

    /// <summary>Its first frame is on screen: the app may start building its own window.</summary>
    public event Action? Shown;

    /// <summary>It has begun to fade (on the UI thread): what lies under it may start to move.</summary>
    public event Action? Leaving;

    /// <summary>The bird's centre on the screen, and its width in this window's units.</summary>
    public (PixelPoint Centre, double Width)? LogoOnScreen()
    {
        var size = _surface.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return null;
        var (x, y, width) = _art.LogoAt((float)size.Width, (float)size.Height);
        return (_surface.PointToScreen(new Point(x, y)), width);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // By the second frame request the first frame has been drawn.
        RequestAnimationFrame(_ => RequestAnimationFrame(_ => Dispatcher.UIThread.Post(() => Shown?.Invoke())));
    }

    /// <summary>The app's window is ready under it: let the bird finish, fade, close.</summary>
    public void HandOver() => _surface.HandOver(Close);
}
