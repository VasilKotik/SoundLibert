using System;
using System.Diagnostics;

namespace SoundLibert.UI.Services;

/// <summary>
/// The project's public links in one place (About, sidebar, bug reports).
/// </summary>
public static class AppLinks
{
    /// <summary>Source code, releases (updates) and the issue tracker.</summary>
    public const string Repository = UpdateService.RepoUrl;

    public const string NewIssue = Repository + "/issues/new";

    /// <summary>
    /// Donation page (Monobank jar, Ko-fi, PayPal, GitHub Sponsors…). While it is
    /// empty, "Support SoundLibert" opens the repository instead (star / share).
    /// </summary>
    public const string Donate = "";

    public static string SupportTarget => string.IsNullOrWhiteSpace(Donate) ? Repository : Donate;

    /// <summary>Opens a link in the default browser; failures are logged, never thrown.</summary>
    public static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Links] could not open {url}: {ex.Message}");
        }
    }
}
