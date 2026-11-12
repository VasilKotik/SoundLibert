using System.Diagnostics;
using System.Text;

namespace SoundLibert.UI.Services;

/// <summary>
/// Where the time goes between a click on the icon and the first frame on screen. One line
/// in the log per start, so a slow start can be told apart from a slow network.
/// </summary>
internal static class StartupClock
{
    private static readonly long Started = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    private static readonly StringBuilder Marks = new();
    private static bool _written;

    /// <summary>Time since the process started.</summary>
    public static TimeSpan Elapsed => new(DateTime.UtcNow.Ticks - Started);

    public static void Mark(string what)
    {
        if (_written) return;
        if (Marks.Length > 0) Marks.Append(", ");
        Marks.Append(what).Append(' ').Append((int)Elapsed.TotalMilliseconds);
    }

    /// <summary>The first frame is on screen: the line is written once.</summary>
    public static void Finish()
    {
        if (_written) return;
        Mark("first frame");
        _written = true;
        Trace.WriteLine($"[App] start, ms since launch: {Marks}");
    }
}
