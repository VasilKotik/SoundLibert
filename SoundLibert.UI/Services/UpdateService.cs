using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Velopack;
using Velopack.Sources;

namespace SoundLibert.UI.Services;

/// <summary>
/// In-app updates from GitHub Releases (Velopack). Installed builds check in
/// the background, download the new version (delta when possible) and offer
/// "Restart to update"; a downloaded update that was not applied is installed
/// on the next launch. Portable / developer builds (not installed by the
/// Velopack setup) only report that updates need the installed version.
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    /// <summary>Where releases are published (GitHub Releases of this repo).</summary>
    public const string RepoUrl = "https://github.com/VasilKotik/SoundLibert";

    /// <summary>Every version, with what changed in it.</summary>
    public const string ReleasesUrl = RepoUrl + "/releases";

    public enum UpdateState { Idle, NotInstalled, Checking, UpToDate, Downloading, ReadyToRestart, Failed }

    private readonly UpdateManager? _manager;
    private UpdateInfo? _ready;
    private int _busy;
    private DispatcherTimer? _timer;

    [ObservableProperty] private UpdateState _state = UpdateState.Idle;
    [ObservableProperty] private int _progress;
    [ObservableProperty] private string? _availableVersion;
    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>What the new version changes, as plain text (from its release notes).</summary>
    [ObservableProperty] private string? _releaseNotes;

    public bool HasNotes => !string.IsNullOrWhiteSpace(ReleaseNotes);

    partial void OnReleaseNotesChanged(string? value) => OnPropertyChanged(nameof(HasNotes));

    public bool IsInstalled => _manager?.IsInstalled == true;

    /// <summary>The running version (installed package version, else the assembly's).</summary>
    public string CurrentVersion =>
        _manager?.CurrentVersion?.ToString()
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public bool IsReadyToRestart => State == UpdateState.ReadyToRestart;
    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading;
    public bool IsChecking => State == UpdateState.Checking;

    /// <summary>Raised on the UI thread when a downloaded update is ready.</summary>
    public event Action<string>? UpdateReady;

    /// <summary>Set by the shell: saves what a normal exit saves. The restart skips window closing.</summary>
    public Action? BeforeRestart { get; set; }

    public UpdateService()
    {
        try
        {
            _manager = new UpdateManager(new GithubSource(RepoUrl, null, false, null));
            if (!_manager.IsInstalled) State = UpdateState.NotInstalled;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Update] init failed: {ex.Message}");
            State = UpdateState.NotInstalled;
        }
        StatusText = DescribeState();
    }

    partial void OnStateChanged(UpdateState value)
    {
        StatusText = DescribeState();
        OnPropertyChanged(nameof(IsReadyToRestart));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsChecking));
    }

    partial void OnProgressChanged(int value)
    {
        if (State == UpdateState.Downloading) StatusText = DescribeState();
    }

    private string DescribeState() => State switch
    {
        UpdateState.NotInstalled => "Updates install automatically in the installed version of SoundLibert.",
        UpdateState.Checking => "Checking for updates…",
        UpdateState.UpToDate => "SoundLibert is up to date.",
        UpdateState.Downloading => $"Downloading version {AvailableVersion}… {Progress}%",
        UpdateState.ReadyToRestart => $"Version {AvailableVersion} is ready — restart to finish updating.",
        UpdateState.Failed => "Couldn't check for updates. Try again later.",
        _ => string.Empty
    };

    /// <summary>Background checks: shortly after launch, then every six hours.</summary>
    public void StartAutomaticChecks()
    {
        if (!IsInstalled || _timer != null) return;
        _ = CheckAndDownloadAsync(userInitiated: false, initialDelay: TimeSpan.FromSeconds(25));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _timer.Tick += (_, _) => _ = CheckAndDownloadAsync(userInitiated: false);
        _timer.Start();
    }

    public void StopAutomaticChecks()
    {
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>Checks GitHub and downloads a newer version if there is one.</summary>
    public async Task CheckAndDownloadAsync(bool userInitiated, TimeSpan? initialDelay = null)
    {
        if (_manager == null || !IsInstalled)
        {
            State = UpdateState.NotInstalled;
            return;
        }
        // Outside the busy guard: a manual check right after launch must not be
        // ignored because the automatic one is still waiting for its turn.
        if (initialDelay is { } delay) await Task.Delay(delay);
        if (State == UpdateState.ReadyToRestart) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            State = UpdateState.Checking;
            var info = await _manager.CheckForUpdatesAsync();
            if (info == null)
            {
                State = UpdateState.UpToDate;
                return;
            }

            AvailableVersion = info.TargetFullRelease.Version.ToString();
            ReleaseNotes = PlainNotes(info.TargetFullRelease.NotesMarkdown);
            Progress = 0;
            State = UpdateState.Downloading;
            await _manager.DownloadUpdatesAsync(info,
                p => Dispatcher.UIThread.Post(() => Progress = p),
                CancellationToken.None);
            _ready = info;
            State = UpdateState.ReadyToRestart;
            System.Diagnostics.Trace.WriteLine($"[Update] {AvailableVersion} downloaded, ready to apply");
            UpdateReady?.Invoke(AvailableVersion);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Update] check/download failed: {ex.Message}");
            // A silent background failure keeps the last good state; a manual
            // check says it failed.
            State = userInitiated ? UpdateState.Failed : UpdateState.Idle;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>
    /// Release notes are written in Markdown for GitHub: here they are read as text, headings
    /// as lines of their own, list items with a bullet, links and emphasis as their words.
    /// </summary>
    public static string? PlainNotes(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;
        var lines = new List<string>();
        foreach (var raw in markdown.Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd();
            var indent = line.Length - line.TrimStart().Length;
            line = line.Trim();
            if (line.Length == 0)
            {
                if (lines.Count > 0 && lines[^1].Length > 0) lines.Add(string.Empty);
                continue;
            }
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\[([^\]]+)\]\([^)]*\)", "$1");
            line = line.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty);
            if (line.StartsWith('#')) line = line.TrimStart('#').Trim();
            else if (line.StartsWith("- ") || line.StartsWith("* "))
                line = (indent >= 2 ? "    ◦ " : "• ") + line[2..].Trim();
            lines.Add(line);
        }
        return string.Join("\n", lines).Trim();
    }

    /// <summary>Closes the app, installs the downloaded version and starts it again.</summary>
    public void RestartToUpdate()
    {
        if (_manager == null || _ready == null) return;
        try
        {
            BeforeRestart?.Invoke();
            _manager.ApplyUpdatesAndRestart(_ready.TargetFullRelease, Array.Empty<string>());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Update] apply failed: {ex.Message}");
            State = UpdateState.Failed;
        }
    }
}
