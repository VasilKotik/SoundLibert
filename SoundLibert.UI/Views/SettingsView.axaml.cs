using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SoundLibert.UI.ViewModels;

namespace SoundLibert.UI.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void SignIn_Click(object? sender, RoutedEventArgs e)
    {
        var vm = DataContext as SettingsViewModel;
        var owner = TopLevel.GetTopLevel(this) as Window;

        // Pre-check the runtime so the user gets a clear instruction instead of
        // a native WebView2 failure.
        if (!SoundLibert.UI.Services.WebViewRuntime.IsAvailable())
        {
            if (vm != null)
                vm.AccountStatus = "Sign-in needs the Microsoft Edge WebView2 Runtime. Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and try again.";
            return;
        }

        try
        {
            var login = new LoginWindow();
            login.Closed += (_, _) => vm?.Refresh();

            if (owner != null) _ = login.ShowDialog(owner);
            else login.Show();
        }
        catch (Exception ex)
        {
            // WebView2 runtime missing / blocked — do not crash the app.
            System.Diagnostics.Trace.WriteLine($"[Login] Failed to open: {ex}");
            if (vm != null)
                vm.AccountStatus = "Couldn't open the sign-in window. On Windows, install the Microsoft Edge WebView2 Runtime and try again.";
        }
    }

    private async void Reimport_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel host) return;
        if (sender is Button button) button.IsEnabled = false;
        try { await host.ReimportAccountAsync(); }
        finally { if (sender is Button b) b.IsEnabled = true; }
    }

    private void ReportBug_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        _ = BugReportWindow.ShowAsync(owner, vm.Updates, vm.SettingsForReports, vm.Notify ?? (_ => { }));
    }

    private void Support_Click(object? sender, RoutedEventArgs e)
        => SoundLibert.UI.Services.AppLinks.Open(SoundLibert.UI.Services.AppLinks.SupportTarget);

    private void ShowWelcome_Click(object? sender, RoutedEventArgs e)
        => (TopLevel.GetTopLevel(this) as MainWindow)?.ShowWelcome();

    private void GitHub_Click(object? sender, RoutedEventArgs e)
    {
        // They went to give the star: the app will not ask for it.
        if (DataContext is SettingsViewModel vm)
        {
            vm.SettingsForReports.Nudges.StarGiven = true;
            vm.SettingsForReports.Save();
        }
        SoundLibert.UI.Services.AppLinks.Open(SoundLibert.UI.Services.AppLinks.Repository);
    }

    private void Releases_Click(object? sender, RoutedEventArgs e)
        => SoundLibert.UI.Services.AppLinks.Open(SoundLibert.UI.Services.UpdateService.ReleasesUrl);

    private void OpenLogs_Click(object? sender, RoutedEventArgs e)
    {
        var folder = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SoundLibert");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = true
            });
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Settings] open log folder failed: {ex.Message}");
        }
    }

    private void OpenDownloads_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        try
        {
            var folder = vm.DownloadsFolderText;
            if (System.IO.Directory.Exists(folder))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{folder}\"",
                    UseShellExecute = true
                });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Settings] open folder failed: {ex.Message}");
        }
    }

    private async void ClearDownloads_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (await PromptWindow.ConfirmAsync(owner, "Remove all downloads?",
                "Every offline copy will be deleted from this device. The songs stay in your library.",
                "Remove all"))
        {
            vm.ClearDownloadsCommand.Execute(null);
        }
    }

    private async void ChangeDownloads_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        var folders = await storage.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Choose a folder for offline downloads",
            AllowMultiple = false
        });
        if (folders.Count == 0) return;
        await vm.SetDownloadFolderAsync(folders[0].Path.LocalPath);
    }
}
