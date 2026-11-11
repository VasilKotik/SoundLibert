using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SoundLibert.UI.Services;

/// <summary>
/// When SoundLibert cannot start, it says so in a plain Windows message instead of
/// vanishing without a trace, and points to the logs a bug report needs.
/// </summary>
internal static class StartupFailure
{
    private const uint MB_ICONERROR = 0x10;
    private const uint MB_SETFOREGROUND = 0x10000;
    private static int _shown;

    /// <summary>True once the main window has opened: from then on a failure is a stop, not a failed start.</summary>
    public static bool Started { get; set; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    public static void Show(Exception error)
    {
        System.Diagnostics.Trace.WriteLine($"[App] Startup failed: {error}");
        // QA runs must never wait on a dialog, and one message is enough.
        if (Environment.GetEnvironmentVariable("SL_SHOT") is not null) return;
        if (Interlocked.Exchange(ref _shown, 1) == 1) return;
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundLibert");
            var what = Started ? "SoundLibert stopped because of an error." : "SoundLibert could not start.";
            MessageBoxW(IntPtr.Zero,
                $"{what}\n\n{error.GetType().Name}: {error.Message}\n\n" +
                $"The details are in crash.log and soundlibert.log in\n{folder}\n\n" +
                $"Please attach them to a bug report:\n{AppLinks.NewIssue}",
                "SoundLibert", MB_ICONERROR | MB_SETFOREGROUND);
        }
        catch
        {
            // Nothing more can be done: the logs still have the details.
        }
    }
}
