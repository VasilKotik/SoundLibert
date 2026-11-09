using System;
using System.Threading;

namespace SoundLibert.UI.Services;

/// <summary>
/// One SoundLibert per user session. A second launch (Start menu, pinned
/// taskbar icon, a double-click while the app sits in the tray) used to open a
/// second player that played on top of the first and fought over the database
/// and log file. Now it just asks the running instance to show itself and exits.
/// </summary>
internal static class SingleInstance
{
    private const string Name = "SoundLibert-8c1d6f2e-single";

    // Static so the GC never releases the mutex while the app runs.
    private static Mutex? _mutex;
    private static EventWaitHandle? _activate;

    /// <summary>
    /// True when this process is the (only) instance. False when another one
    /// already runs — it has been told to come to the front.
    /// </summary>
    public static bool Acquire()
    {
        // QA screenshot runs may deliberately run next to a normal instance.
        if (Environment.GetEnvironmentVariable("SL_SHOT") is not null) return true;
        try
        {
            _mutex = new Mutex(initiallyOwned: true, $"Local\\{Name}", out var createdNew);
            _activate = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\{Name}-activate");
            if (createdNew) return true;
            _activate.Set();
            return false;
        }
        catch (Exception ex)
        {
            // Never block startup on this; worst case two instances run.
            System.Diagnostics.Trace.WriteLine($"[App] single-instance check failed: {ex.Message}");
            return true;
        }
    }

    /// <summary>Calls <paramref name="onActivate"/> whenever a second launch asks for the window.</summary>
    public static void ListenForActivation(Action onActivate)
    {
        var handle = _activate;
        if (handle == null) return;
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    handle.WaitOne();
                    onActivate();
                }
                catch (ObjectDisposedException) { return; }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[App] activation failed: {ex.Message}");
                }
            }
        })
        {
            IsBackground = true,
            Name = "SoundLibert single-instance"
        };
        thread.Start();
    }
}
