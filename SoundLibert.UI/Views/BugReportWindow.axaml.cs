using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SoundLibert.Domain.Interfaces;
using SoundLibert.UI.Services;

namespace SoundLibert.UI.Views;

/// <summary>
/// "Report a bug": a short title + description, optional (redacted) diagnostics
/// the user can preview, then either a pre-filled GitHub issue (the full report
/// goes to the clipboard — URLs cannot carry a whole log) or a saved .txt.
/// </summary>
public partial class BugReportWindow : Window
{
    // Longer new-issue links fail on GitHub (VS Code's issue reporter uses the
    // same cap): whatever does not fit goes to the clipboard and is pasted.
    private const int MaxUrlLength = 7500;

    // HTML comments: visible while editing the issue, gone once it is posted.
    private const string PasteLogHint =
        "<!-- SoundLibert copied the recent log to your clipboard: paste it below (Ctrl+V). -->";
    private const string PasteReportHint =
        "<!-- The report was too long for a link, so SoundLibert copied it to your clipboard: paste it here (Ctrl+V). -->";

    private UpdateService? _updates;
    private IAppSettings? _settings;
    private Action<string>? _notify;

    public BugReportWindow() => InitializeComponent();

    public static Task ShowAsync(Window owner, UpdateService updates, IAppSettings settings, Action<string>? notify)
        => Create(updates, settings, notify).ShowDialog(owner);

    internal static BugReportWindow Create(UpdateService updates, IAppSettings settings, Action<string>? notify)
    {
        var window = new BugReportWindow { _updates = updates, _settings = settings, _notify = notify };
        window.Opened += (_, _) => window.TitleInput.Focus();
        window.IncludeDiagnostics.IsCheckedChanged += (_, _) => window.RefreshPreview();
        window.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            window.Close();
        };
        return window;
    }

    /// <summary>QA (<c>SL_SHOT=report</c>): fill the form and open the preview.</summary>
    internal void QaFill(string title, string description)
    {
        TitleInput.Text = title;
        DescriptionInput.Text = description;
        if (!PreviewBox.IsVisible) PreviewToggle_Click(null, new RoutedEventArgs());
    }

    private bool WithLogs => IncludeDiagnostics.IsChecked == true;

    private string EnvironmentSection()
        => _updates != null && _settings != null
            ? DiagnosticsReport.EnvironmentSection(_updates, _settings)
            : string.Empty;

    private string Summary()
    {
        var title = TitleInput.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(title)) return title!;
        var firstLine = DescriptionInput.Text?.Split('\n').FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(firstLine) ? "Bug report" : firstLine!.Length > 80 ? firstLine[..80] + "…" : firstLine;
    }

    private string Description() => DescriptionInput.Text?.Trim() is { Length: > 0 } d ? d : "_(no description)_";

    /// <summary>
    /// The issue text: the description, then (with diagnostics on) the
    /// environment and <paramref name="tail"/> — the log itself, or a note to paste it.
    /// </summary>
    private string Body(string? tail)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### What happened");
        sb.AppendLine(Description());
        if (WithLogs)
        {
            sb.AppendLine();
            sb.Append(EnvironmentSection());
            if (!string.IsNullOrEmpty(tail))
            {
                sb.AppendLine();
                sb.AppendLine(tail);
            }
        }
        return sb.ToString();
    }

    /// <summary>Everything, as previewed, saved to a file or pasted into the issue.</summary>
    internal string FullReport()
        => $"## {Summary()}{Environment.NewLine}{Environment.NewLine}" +
           Body(WithLogs ? DiagnosticsReport.LogSections() : null);

    private void RefreshPreview()
    {
        if (PreviewBox.IsVisible) PreviewBox.Text = FullReport();
    }

    private void PreviewToggle_Click(object? sender, RoutedEventArgs e)
    {
        PreviewBox.IsVisible = !PreviewBox.IsVisible;
        PreviewToggle.Content = PreviewBox.IsVisible ? "Hide preview" : "Show what will be shared";
        RefreshPreview();
    }

    /// <summary>The pre-filled new-issue link, and the text to paste into it (null = nothing).</summary>
    internal (string Url, string? Paste) BuildIssueLink()
    {
        var baseUrl = $"{AppLinks.NewIssue}?labels=bug&title={Uri.EscapeDataString(Summary())}";

        // A log never fits in a link: the issue gets the description and the
        // environment, the log itself goes to the clipboard.
        var log = WithLogs ? DiagnosticsReport.LogSections() : string.Empty;
        var paste = log.Length > 0 ? log : null;
        var url = $"{baseUrl}&body={Uri.EscapeDataString(Body(paste != null ? PasteLogHint : null))}";

        // A very long description does not fit either: then everything is pasted.
        if (url.Length > MaxUrlLength)
        {
            paste = FullReport();
            url = $"{baseUrl}&body={Uri.EscapeDataString(PasteReportHint)}";
        }
        return (url, paste);
    }

    private async void OpenIssue_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var (url, paste) = BuildIssueLink();
            if (paste != null && Clipboard is { } clipboard) await clipboard.SetTextAsync(paste);
            AppLinks.Open(url);
            _notify?.Invoke(paste != null
                ? "The report is on your clipboard — paste it into the GitHub issue"
                : "Opening the GitHub issue…");
            Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[BugReport] open issue failed: {ex.Message}");
        }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save bug report",
                SuggestedFileName = $"SoundLibert-report-{DateTime.Now:yyyyMMdd-HHmm}.txt",
                DefaultExtension = "txt",
                FileTypeChoices = new[] { new FilePickerFileType("Text file") { Patterns = new[] { "*.txt" } } }
            });
            if (file == null) return;
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(FullReport());
            _notify?.Invoke("Bug report saved");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[BugReport] save failed: {ex.Message}");
            _notify?.Invoke("Couldn't save the report");
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
