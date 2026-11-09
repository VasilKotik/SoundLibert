using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using SoundLibert.Domain.Interfaces;

namespace SoundLibert.UI.Services;

/// <summary>
/// Builds the text attached to a bug report: app/OS facts, a few settings and
/// the tail of the log and crash log — with anything personal removed (stream
/// URLs carry the user's IP, paths carry the Windows user name). The user sees
/// the exact text before it leaves the machine.
/// </summary>
public static class DiagnosticsReport
{
    private const int LogLines = 250;
    private const int CrashEntries = 15;
    // GitHub caps an issue body at 65,536 characters: keep the newest text within these.
    private const int LogBudget = 40_000;
    private const int CrashBudget = 15_000;

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundLibert");

    private static readonly Regex UrlQuery = new(@"(https?://[^\s?""'<>]+)\?[^\s""'<>]*", RegexOptions.Compiled);
    private static readonly Regex IpAddress = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    // The CDN node in a stream host ("rr2---sn-abc123.googlevideo.com") hints at the ISP and city.
    private static readonly Regex StreamHost = new(@"\b[a-z0-9-]+\.googlevideo\.com", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // A log cut off by a crash can hold NULs and other control characters.
    private static readonly Regex ControlChars = new(@"[\x00-\x08\x0B\x0C\x0E-\x1F]", RegexOptions.Compiled);

    public static string Build(UpdateService updates, IAppSettings settings, bool includeLogs)
        => includeLogs
            ? EnvironmentSection(updates, settings) + Environment.NewLine + LogSections()
            : EnvironmentSection(updates, settings);

    /// <summary>Version, Windows, .NET, language and a few settings — short enough for a link.</summary>
    public static string EnvironmentSection(UpdateService updates, IAppSettings settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### Environment");
        sb.AppendLine($"- SoundLibert: {updates.CurrentVersion} ({InstallKind(updates)})");
        sb.AppendLine($"- Windows: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"- .NET: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"- Language: {System.Globalization.CultureInfo.CurrentUICulture.Name}");
        sb.AppendLine($"- Settings: lyrics={settings.LyricsEnabled}, canvas={settings.ShowCanvas}, " +
                      $"audioQuality={settings.AudioQuality}, videoQuality={settings.VideoQuality}, " +
                      $"closeToTray={settings.CloseToTray}, autoUpdates={settings.AutoCheckUpdates}");
        return sb.ToString();
    }

    /// <summary>The redacted tails of the log and the crash log; empty when neither exists.</summary>
    public static string LogSections()
    {
        var sb = new StringBuilder();
        AppendTail(sb, "### Recent log", Path.Combine(DataDir, "soundlibert.log"), LogLines);
        AppendCrashes(sb, Path.Combine(DataDir, "crash.log"));
        return sb.ToString().TrimStart();
    }

    private static string InstallKind(UpdateService updates) => updates.IsInstalled ? "installed" : "portable / dev build";

    private static void AppendTail(StringBuilder sb, string title, string path, int lines)
    {
        var tail = ReadTail(path, lines);
        if (tail.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine(title);
        sb.AppendLine("```");
        // VLC's own chatter is noise for a bug report.
        var kept = tail.Where(l => !l.Contains("[VLC:", StringComparison.Ordinal)).Select(Redact).ToList();
        foreach (var line in Newest(kept, LogBudget)) sb.AppendLine(line);
        sb.AppendLine("```");
    }

    /// <summary>The last lines whose total length fits <paramref name="budget"/>.</summary>
    private static IEnumerable<string> Newest(List<string> lines, int budget)
    {
        var start = lines.Count;
        var total = 0;
        while (start > 0 && total + lines[start - 1].Length + 1 <= budget)
            total += lines[--start].Length + 1;
        return lines.Skip(start);
    }

    private static void AppendCrashes(StringBuilder sb, string path)
    {
        var lines = ReadTail(path, 400);
        if (lines.Count == 0) return;
        // Each entry starts with "[timestamp] source: ..." — keep the last few.
        var starts = lines.Select((l, i) => (l, i)).Where(x => x.l.StartsWith("[20", StringComparison.Ordinal)).Select(x => x.i).ToList();
        var from = starts.Count > CrashEntries ? starts[^CrashEntries] : 0;
        sb.AppendLine();
        sb.AppendLine("### Crash log (latest)");
        sb.AppendLine("```");
        foreach (var line in Newest(lines.Skip(from).Select(Redact).ToList(), CrashBudget)) sb.AppendLine(line);
        sb.AppendLine("```");
    }

    /// <summary>Last <paramref name="count"/> lines, read while the app keeps writing the file.</summary>
    private static List<string> ReadTail(string path, int count)
    {
        var result = new List<string>();
        try
        {
            if (!File.Exists(path)) return result;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Only the end of a large log matters.
            const long window = 512 * 1024;
            if (stream.Length > window) stream.Seek(-window, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var queue = new Queue<string>(count + 1);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                queue.Enqueue(line);
                if (queue.Count > count) queue.Dequeue();
            }
            result.AddRange(queue);
        }
        catch (Exception ex)
        {
            result.Add($"(could not read {Path.GetFileName(path)}: {ex.Message})");
        }
        return result;
    }

    /// <summary>
    /// Removes what could identify the user: the profile path, URL queries (IPs,
    /// tokens), stream CDN nodes, IPs and emails — plus stray control characters.
    /// </summary>
    public static string Redact(string text)
    {
        text = ControlChars.Replace(text, string.Empty);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
            text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        text = UrlQuery.Replace(text, "$1?…");
        text = StreamHost.Replace(text, "<cdn>.googlevideo.com");
        text = IpAddress.Replace(text, "<ip>");
        text = Email.Replace(text, "<email>");
        return text;
    }
}
