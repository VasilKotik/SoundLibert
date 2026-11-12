using Avalonia;
using Avalonia.Media;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SoundLibert.App.Services;
using SoundLibert.Domain.Interfaces;
using SoundLibert.UI.ViewModels;

namespace SoundLibert.UI;

class Program
{
    // IMPORTANT: Main must stay synchronous and carry [STAThread]. With
    // `async Task Main` the attribute is not applied to the compiler-generated
    // entry point, so the thread starts as MTA — and WebView2 (Google sign-in)
    // then fails with RPC_E_CHANGED_MODE (0x80010106) and crashes the app.
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack install/update/uninstall hooks run (and exit) here; it also
        // applies an update that was downloaded but not yet installed. Must be
        // the very first thing in Main.
        Velopack.VelopackApp.Build().Run();

        // A second launch only brings the running player to the front.
        if (!SoundLibert.UI.Services.SingleInstance.Acquire()) return;

        // Mirror all Debug/Trace output ([StreamResolver], [Audio], ...) to a
        // log file so playback failures can be diagnosed without the VS
        // Output window: %LocalAppData%\SoundLibert\soundlibert.log
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SoundLibert");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "soundlibert.log");
            // Rotate: an ever-growing log from long sessions slows startup I/O.
            try
            {
                var logInfo = new FileInfo(logPath);
                if (logInfo.Exists && logInfo.Length > 5 * 1024 * 1024)
                {
                    var oldPath = Path.Combine(logDir, "soundlibert.old.log");
                    try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch { }
                    try { File.Move(logPath, oldPath); } catch { }
                }
            }
            catch { }
            var listener = new TimestampedTraceListener(logPath) { Name = "SoundLibertFile" };
            System.Diagnostics.Trace.Listeners.Add(listener);
            System.Diagnostics.Trace.AutoFlush = true;
            System.Diagnostics.Trace.WriteLine(
                $"[App] ---- SoundLibert {Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)} on " +
                $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), {DateTime.Now:yyyy-MM-dd} ----");
        }
        catch { }

        // Log any unhandled exception instead of dying silently — important for
        // a public app, and the only way to diagnose native (WebView2) faults.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            System.Diagnostics.Trace.WriteLine($"[App] UNHANDLED: {e.ExceptionObject}");
            WriteCrashLog("AppDomain", e.ExceptionObject);
            // Before the window opens nothing else on screen would tell the user.
            if (!SoundLibert.UI.Services.StartupFailure.Started && e.ExceptionObject is Exception fatal)
                SoundLibert.UI.Services.StartupFailure.Show(fatal);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            System.Diagnostics.Trace.WriteLine($"[App] Unobserved task: {e.Exception}");
            WriteCrashLog("Task", e.Exception);
            e.SetObserved();
        };
        // UI-thread faults (most crashes) surface here before the process dies.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            System.Diagnostics.Trace.WriteLine($"[App] UI UNHANDLED: {e.Exception}");
            WriteCrashLog("UI", e.Exception);
            // A failed click handler (async void like/playlist buttons) or a
            // network hiccup must not close the player mid-song. Only truly
            // fatal runtime states are allowed to bring the process down.
            if (e.Exception is not (OutOfMemoryException or StackOverflowException
                    or AccessViolationException or InvalidProgramException))
                e.Handled = true;
        };

        SoundLibert.UI.Services.StartupClock.Mark("log");
        var services = new ServiceCollection();
        services.AddSoundLibert();
        // Windows plays through NAudio (Media Foundation decodes, WaveOut plays).
        services.AddSingleton<IAudioPlayer, SoundLibert.Audio.NAudioPlayer>();
        services.AddSingleton<IMediaProbe, SoundLibert.Audio.MediaFoundationProbe>();
        services.AddSingleton<SoundLibert.UI.Services.ArtworkService>();
        services.AddSingleton<SoundLibert.UI.Services.UpdateService>();
        services.AddSingleton<MainWindowViewModel>();

        App.ServiceProvider = services.BuildServiceProvider();
        SoundLibert.UI.Services.AppServices.Provider = App.ServiceProvider;
        SoundLibert.UI.Services.AppServices.Shell = new SoundLibert.UI.Services.DesktopShell();

        // Database + account init run before Avalonia has a synchronization
        // context, so blocking here cannot deadlock (continuations go to the
        // thread pool).
        try
        {
            App.ServiceProvider.GetRequiredService<IMusicDatabase>()
                .InitializeAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[App] Database init failed: {ex.Message}");
        }

        SoundLibert.UI.Services.StartupClock.Mark("database");
        try
        {
            var account = App.ServiceProvider.GetRequiredService<IAccountService>();
            account.InitializeAsync().GetAwaiter().GetResult();
            // Signed out (now or by an earlier sign-out that could not finish):
            // no Google session may stay behind in the sign-in window's profile.
            if (!account.IsSignedIn) _ = Task.Run(SoundLibert.UI.Services.WebViewProfile.Clear);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[App] Account init failed: {ex.Message}");
        }

        SoundLibert.UI.Services.StartupClock.Mark("account");
        // The opening's drawing is prepared while the window system starts.
        if (SoundLibert.UI.Splash.SplashWindow.IsWanted(App.ServiceProvider.GetService<IAppSettings>()))
            SoundLibert.UI.Splash.SplashArt.PrepareAhead(SoundLibert.UI.Controls.BrandArt.MarkData);
        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // The window system or the first window failed: say so instead of vanishing.
            WriteCrashLog("Startup", ex);
            SoundLibert.UI.Services.StartupFailure.Show(ex);
        }
    }

    /// <summary>Writes a fatal exception to %LocalAppData%\SoundLibert\crash.log.</summary>
    private static void WriteCrashLog(string source, object? error)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SoundLibert");
            Directory.CreateDirectory(dir);
            var line = $"[{DateTime.Now:O}] {source}: {error}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(dir, "crash.log"), line);
            System.Diagnostics.Trace.Flush();
        }
        catch { }
    }

    /// <summary>The log file with the time on every line: what happened right before a freeze is the question.</summary>
    private sealed class TimestampedTraceListener(string path) : TextWriterTraceListener(path)
    {
        public override void WriteLine(string? message) => base.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
            .LogToTrace();

        // QA: with the display off the usual way of composing pulses at about 4 fps,
        // so frame rates can be measured on a plain surface instead.
        if (Environment.GetEnvironmentVariable("SL_SHOT_SURFACE") == "1")
            builder = builder.With(new Win32PlatformOptions { CompositionMode = new[] { Win32CompositionMode.RedirectionSurface } });

        return builder;
    }
}
