using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using SoundLibert.UI.Views;

namespace SoundLibert.UI;

public partial class App : Application
{
    public static IServiceProvider? ServiceProvider { get; set; }

    public override void Initialize()
    {
        SoundLibert.UI.Services.StartupClock.Mark("ui framework");
        AvaloniaXamlLoader.Load(this);
        SoundLibert.UI.Services.StartupClock.Mark("styles");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = ServiceProvider?.GetService(typeof(SoundLibert.Domain.Interfaces.IAppSettings)) as SoundLibert.Domain.Interfaces.IAppSettings;
            if (SoundLibert.UI.Splash.SplashWindow.IsWanted(settings))
            {
                // The opening shows at once and plays while the app's window is built behind it.
                var splash = new SoundLibert.UI.Splash.SplashWindow();
                SoundLibert.UI.Services.StartupClock.Mark("opening built");
                desktop.MainWindow = splash;
                splash.Shown += () =>
                {
                    SoundLibert.UI.Services.StartupClock.Mark("opening shown");
                    MainWindow main;
                    try
                    {
                        main = new MainWindow();
                    }
                    catch (System.Exception ex)
                    {
                        // Raised inside an event, this would only be logged and the app
                        // would close with the opening: tell the user first.
                        SoundLibert.UI.Services.StartupFailure.Show(ex);
                        splash.Close();
                        throw;
                    }
                    main.Opened += (_, _) => SoundLibert.UI.Services.StartupFailure.Started = true;
                    SoundLibert.UI.Services.StartupClock.Mark("window built");
                    desktop.MainWindow = main;
                    // A welcome under the opening takes the bird over as the opening fades.
                    main.WaitForOpening();
                    splash.Leaving += () => main.OpeningLeaves(splash.LogoOnScreen());
                    main.ContentShown += splash.HandOver;
                    splash.Closed += (_, _) => System.Diagnostics.Trace.WriteLine(
                        $"[Splash] gone {(int)SoundLibert.UI.Services.StartupClock.Elapsed.TotalMilliseconds} ms after launch");
                    main.Show();
                };
            }
            else
            {
                var main = new MainWindow();
                main.Opened += (_, _) => SoundLibert.UI.Services.StartupFailure.Started = true;
                desktop.MainWindow = main;
                SoundLibert.UI.Services.StartupClock.Mark("window built");
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
